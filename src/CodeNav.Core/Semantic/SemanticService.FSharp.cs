using CodeNav.Core.Discovery;
using CodeNav.Core.Indexing;
using CodeNav.FSharp;

namespace CodeNav.Core.Semantic;

public sealed record FSharpOutlineItem(
    string Name,
    string Kind,
    string? Signature,
    string Accessibility,
    int StartLine,
    int EndLine,
    string? Modifiers,
    string? Accessors,
    List<FSharpOutlineItem> Members);

public sealed record FSharpOutlineContext(
    string Project,
    string? TargetFramework);

public sealed record FSharpOutlineResult(
    List<FSharpOutlineItem> Symbols,
    string? Error = null,
    long? FileBytes = null,
    long? MaxBytes = null,
    string? PartialReason = null,
    string? SelectedProject = null,
    string? SelectedTargetFramework = null,
    List<FSharpOutlineContext>? AvailableContexts = null);

public sealed partial class SemanticService
{
    private sealed record FSharpOwnerOptions(
        ProjectRow Owner,
        FSharpParsingOptionsSnapshot Options);

    // Match the existing structural-input ceiling used by the C# parser. F# text indexing may
    // retain much larger files, but an on-demand compiler parse is a different cost profile.
    public const int MaxFSharpOutlineBytes = IndexBuilder.MaxStructuralFileBytes;

    /// <summary>
    /// Returns an FCS-derived declaration outline for an indexed, project-owned .fs/.fsi file.
    /// This is intentionally syntax-only: project type checking belongs to later F# stages.
    /// </summary>
    public FSharpOutlineResult FSharpOutline(string path)
    {
        using var queries = _manager.OpenQueries();
        FileHit? file = queries.FileByPath(path);
        if (file is not { Language: "fs" })
            return new([], "unsupported_language");

        string extension = Path.GetExtension(path);
        if (!extension.Equals(".fs", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".fsi", StringComparison.OrdinalIgnoreCase))
        {
            return new([], "unsupported_fsharp_file_kind");
        }

        List<ProjectRow> owners = queries.ProjectsContaining(path)
            .Where(project => project.Language == "fs")
            .OrderBy(project => project.Path, StringComparer.Ordinal)
            .ToList();
        if (owners.Count == 0)
            return new([], "fsharp_project_not_found");

        var ownerOptions = new List<FSharpOwnerOptions>(owners.Count);
        foreach (ProjectRow owner in owners)
        {
            string? projectXml = queries.ContentByPathBounded(
                owner.Path, IndexBuilder.MaxStructuralFileBytes);
            if (projectXml is null)
                return new([], "fsharp_project_options_unavailable");

            FSharpParsingOptionsSnapshot options =
                ProjectFileParser.ParseFSharpParsingOptionsSnapshot(
                    owner.Path, projectXml, owner.Tfms);
            if (options.Error is { } optionError)
                return new([], optionError);

            ownerOptions.Add(new(owner, options));
        }

        var availableContexts = new List<FSharpOutlineContext>();
        foreach (FSharpOwnerOptions owner in ownerOptions)
        {
            if (owner.Options.AvailableTargetFrameworks is { Count: > 0 } frameworks)
            {
                availableContexts.AddRange(frameworks.Select(framework =>
                    new FSharpOutlineContext(owner.Owner.Path, framework)));
            }
            else
            {
                availableContexts.Add(new(owner.Owner.Path, null));
            }
        }

        FSharpOwnerOptions? pairedBase = SelectPairedBaseProject(ownerOptions);
        FSharpOwnerOptions selected = pairedBase ?? ownerOptions[0];
        availableContexts = availableContexts
            .OrderBy(context => context.Project.Equals(selected.Owner.Path,
                StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ToList();
        var partialReasons = new SortedSet<string>(StringComparer.Ordinal);
        if (pairedBase is null)
        {
            foreach (FSharpOwnerOptions owner in ownerOptions)
            {
                if (!selected.Options.CommandLineArgs.SequenceEqual(
                        owner.Options.CommandLineArgs, StringComparer.Ordinal))
                {
                    return new([], "fsharp_project_options_conflict");
                }
                AddPartialReasons(partialReasons, owner.Options.PartialReason);
            }
        }
        else
        {
            AddPartialReasons(partialReasons, selected.Options.PartialReason);
            partialReasons.Add("fsharp_alternate_syntax_contexts");
        }

        if (file.Size > MaxFSharpOutlineBytes)
            return new([], "file_too_large", file.Size, MaxFSharpOutlineBytes);

        string? source = queries.ContentByPathBounded(path, MaxFSharpOutlineBytes);
        if (source is null)
            return new([], "file_content_unavailable", file.Size, MaxFSharpOutlineBytes);

        try
        {
            string fileName = Path.GetFullPath(Path.Combine(
                _manager.WorkspaceRoot,
                path.Replace('/', Path.DirectorySeparatorChar)));
            OutlineParseResult parsed = OutlineParser.Parse(
                fileName, source, selected.Options.CommandLineArgs.ToArray());
            if (parsed.Error is { } error)
                return new([], error);

            var symbols = parsed.Symbols
                .Select(MapOutlineItem)
                .OrderBy(item => item.StartLine)
                .ThenBy(item => item.Name, StringComparer.Ordinal)
                .ToList();
            return new(symbols, PartialReason: partialReasons.Count == 0
                ? null
                : string.Join(';', partialReasons),
                SelectedProject: selected.Owner.Path,
                SelectedTargetFramework: selected.Options.SelectedTargetFramework,
                AvailableContexts: availableContexts);
        }
        catch (Exception ex)
        {
            _log($"F# outline failed: {ex.GetType().Name}");
            return new([], "fsharp_outline_failed");
        }
    }

    private static FSharpOutlineItem MapOutlineItem(OutlineItem item) =>
        new(
            item.Name,
            item.Kind,
            item.Signature,
            item.Accessibility,
            item.StartLine,
            item.EndLine,
            item.Modifiers,
            item.Accessors,
            item.Members.Select(MapOutlineItem).ToList());

    private static FSharpOwnerOptions? SelectPairedBaseProject(
        List<FSharpOwnerOptions> owners)
    {
        // Migration convention, syntax selection only: the old single-target project remains the
        // build/process baseline while the exact .Net companion dual-compiles the same source for
        // binary comparison and the new runtime. Do not collapse their graph or reference facts.
        if (owners.Count != 2) return null;

        foreach (FSharpOwnerOptions candidate in owners)
        {
            if (!candidate.Owner.Style.Equals("legacy", StringComparison.OrdinalIgnoreCase) ||
                candidate.Options.AvailableTargetFrameworks is not { Count: 1 } baseFrameworks)
            {
                continue;
            }

            FSharpOwnerOptions companion = ReferenceEquals(candidate, owners[0])
                ? owners[1]
                : owners[0];
            if (!companion.Owner.Style.Equals("sdk", StringComparison.OrdinalIgnoreCase) ||
                companion.Options.AvailableTargetFrameworks is not { Count: > 1 } companionFrameworks ||
                !IsExactNetCompanion(candidate.Owner.Path, companion.Owner.Path) ||
                !companionFrameworks.Contains(baseFrameworks[0],
                    StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            return candidate;
        }

        return null;
    }

    private static bool IsExactNetCompanion(string baseProject, string companionProject)
    {
        string normalizedBase = baseProject.Replace('\\', '/');
        string normalizedCompanion = companionProject.Replace('\\', '/');
        string baseDirectory = normalizedBase.Contains('/')
            ? normalizedBase[..normalizedBase.LastIndexOf('/')]
            : "";
        string companionDirectory = normalizedCompanion.Contains('/')
            ? normalizedCompanion[..normalizedCompanion.LastIndexOf('/')]
            : "";
        if (!baseDirectory.Equals(companionDirectory, StringComparison.OrdinalIgnoreCase))
            return false;

        string baseName = Path.GetFileNameWithoutExtension(normalizedBase);
        string companionName = Path.GetFileNameWithoutExtension(normalizedCompanion);
        return companionName.Equals($"{baseName}.Net", StringComparison.OrdinalIgnoreCase);
    }

    private static void AddPartialReasons(SortedSet<string> destination, string? reasons)
    {
        if (reasons is not { Length: > 0 }) return;
        foreach (string reason in reasons.Split(';', StringSplitOptions.RemoveEmptyEntries))
            destination.Add(reason);
    }
}
