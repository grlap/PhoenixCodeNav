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

public sealed record FSharpOutlineResult(
    List<FSharpOutlineItem> Symbols,
    string? Error = null,
    long? FileBytes = null,
    long? MaxBytes = null,
    string? PartialReason = null);

public sealed partial class SemanticService
{
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

        List<string>? parsingArgs = null;
        var partialReasons = new SortedSet<string>(StringComparer.Ordinal);
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

            if (parsingArgs is null)
            {
                parsingArgs = options.CommandLineArgs;
            }
            else if (!parsingArgs.SequenceEqual(options.CommandLineArgs,
                         StringComparer.Ordinal))
            {
                return new([], "fsharp_project_options_conflict");
            }

            if (options.PartialReason is { Length: > 0 } partialReason)
            {
                foreach (string reason in partialReason.Split(';',
                             StringSplitOptions.RemoveEmptyEntries))
                {
                    partialReasons.Add(reason);
                }
            }
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
                fileName, source, parsingArgs?.ToArray() ?? []);
            if (parsed.Error is { } error)
                return new([], error);

            var symbols = parsed.Symbols
                .Select(MapOutlineItem)
                .OrderBy(item => item.StartLine)
                .ThenBy(item => item.Name, StringComparer.Ordinal)
                .ToList();
            return new(symbols, PartialReason: partialReasons.Count == 0
                ? null
                : string.Join(';', partialReasons));
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
}
