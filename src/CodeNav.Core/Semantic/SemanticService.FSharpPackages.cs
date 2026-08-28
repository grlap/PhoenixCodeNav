using CodeNav.Core.Discovery;
using CodeNav.Core.Indexing;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CodeNav.Core.Semantic;

public sealed partial class SemanticService
{
    private sealed record FSharpPackageCompileAsset(
        string SourceIdentity,
        string FullPath,
        string PackageRoot);

    private sealed record FSharpPackageAssetsSnapshot(
        string Identity,
        List<FSharpPackageCompileAsset> CompileAssets);

    private sealed record FSharpNuGetVersion(
        ulong Major,
        ulong Minor,
        ulong Patch,
        ulong Revision,
        string[] Prerelease,
        string Normalized);

    private sealed record FSharpPackageVersionConstraint(
        string Canonical,
        FSharpNuGetVersion? Lower,
        bool LowerInclusive,
        FSharpNuGetVersion? Upper,
        bool UpperInclusive,
        ulong[]? FloatingPrefix);

    private bool TryResolveFSharpPackageAssets(
        string projectPath,
        string indexedProjectXml,
        string targetFramework,
        IReadOnlyCollection<FSharpPackageReferenceSnapshot> packageReferences,
        IReadOnlyDictionary<string, string> evaluatedAuthorityInputs,
        CancellationToken cancellationToken,
        out FSharpPackageAssetsSnapshot? snapshot,
        out string? error)
    {
        snapshot = null;
        error = null;
        if (packageReferences.Count == 0)
        {
            snapshot = new("", []);
            return true;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!TryWorkspaceAbsolutePath(projectPath, out string? projectFullPath,
                rejectReparsePoints: true))
        {
            error = "fsharp_semantic_package_assets_unavailable";
            return false;
        }

        string workspaceRoot = Path.GetFullPath(_manager.WorkspaceRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!TryReadVerifiedFSharpInput(projectPath, projectFullPath!, workspaceRoot,
                ".fsproj", cancellationToken, out byte[]? projectBytes,
                out string? projectIdentity))
        {
            error = "fsharp_semantic_package_assets_unavailable";
            return false;
        }
        using (var projectReader = new StreamReader(new MemoryStream(projectBytes!),
                   Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
        {
            if (!projectReader.ReadToEnd().Equals(indexedProjectXml, StringComparison.Ordinal))
            {
                error = "fsharp_semantic_package_assets_stale";
                return false;
            }
        }

        string projectDirectory = Path.GetDirectoryName(projectFullPath!)!;
        string assetsFullPath = Path.Combine(projectDirectory, "obj", "project.assets.json");
        if (!TryReadVerifiedFSharpInput($"{projectPath}:project.assets.json", assetsFullPath,
                workspaceRoot, ".json", cancellationToken, out byte[]? assetsBytes,
                out string? assetsIdentity))
        {
            error = "fsharp_semantic_package_assets_unavailable";
            return false;
        }
        DateTime assetsLastWriteTime = File.GetLastWriteTimeUtc(assetsFullPath);
        if (assetsLastWriteTime <= File.GetLastWriteTimeUtc(projectFullPath!))
        {
            error = "fsharp_semantic_package_assets_stale";
            return false;
        }

        var authorityIdentities = new List<string>(evaluatedAuthorityInputs.Count);
        foreach ((string authorityPath, string indexedContent) in evaluatedAuthorityInputs
                     .OrderBy(input => input.Key, WorkspacePaths.FileSystemPathComparer))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string extension = Path.GetExtension(authorityPath);
            if ((!extension.Equals(".props", StringComparison.OrdinalIgnoreCase) &&
                 !extension.Equals(".targets", StringComparison.OrdinalIgnoreCase)) ||
                !TryWorkspaceAbsolutePath(authorityPath, out string? authorityFullPath,
                    rejectReparsePoints: true) ||
                !TryReadVerifiedFSharpInput(authorityPath, authorityFullPath!, workspaceRoot,
                    extension, cancellationToken, out byte[]? authorityBytes,
                    out string? authorityIdentity) ||
                !FSharpInputTextEquals(authorityBytes!, indexedContent) ||
                assetsLastWriteTime <= File.GetLastWriteTimeUtc(authorityFullPath!))
            {
                error = "fsharp_semantic_package_assets_stale";
                return false;
            }
            authorityIdentities.Add(authorityIdentity!);
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(assetsBytes!,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = ProjectFileParser.MaxFSharpSemanticEvaluationDepth,
                });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !TryValidateFSharpAssetsProject(root, projectFullPath!, targetFramework,
                    packageReferences))
            {
                error = "fsharp_semantic_package_assets_stale";
                return false;
            }
            if (!root.TryGetProperty("targets", out JsonElement targets) ||
                targets.ValueKind != JsonValueKind.Object ||
                !TryGetFSharpAssetsFrameworkProperty(targets, targetFramework,
                    out JsonElement target) ||
                target.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("libraries", out JsonElement libraries) ||
                libraries.ValueKind != JsonValueKind.Object ||
                !TryGetFSharpPackageRoots(root, cancellationToken,
                    out List<string>? packageRoots))
            {
                error = "fsharp_semantic_package_assets_unavailable";
                return false;
            }

            var packageTargets = new Dictionary<string,
                (string LibraryKey, string ExpectedLibraryPath, JsonElement Target)>(
                StringComparer.OrdinalIgnoreCase);
            int dependencyNodes = 0;
            foreach (JsonProperty libraryTarget in target.EnumerateObject())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++dependencyNodes > ProjectFileParser.MaxFSharpSemanticDependencyNodes)
                {
                    error = "fsharp_semantic_dependency_limit";
                    return false;
                }
                if (libraryTarget.Value.ValueKind != JsonValueKind.Object ||
                    !libraryTarget.Value.TryGetProperty("type", out JsonElement targetType) ||
                    !targetType.ValueEquals("package"u8))
                    continue;
                int separator = libraryTarget.Name.LastIndexOf('/');
                string packageId = separator > 0 ? libraryTarget.Name[..separator] : "";
                string packageVersion = separator < libraryTarget.Name.Length - 1
                    ? libraryTarget.Name[(separator + 1)..]
                    : "";
                string expectedLibraryPath =
                    $"{packageId.ToLowerInvariant()}/{packageVersion.ToLowerInvariant()}";
                if (separator <= 0 || separator == libraryTarget.Name.Length - 1 ||
                    !TryGetSafeRelativeFSharpAssetPath(expectedLibraryPath,
                        out string? normalizedExpectedLibraryPath) ||
                    !packageTargets.TryAdd(packageId,
                        (libraryTarget.Name, normalizedExpectedLibraryPath!,
                            libraryTarget.Value)))
                {
                    error = "fsharp_semantic_package_assets_unavailable";
                    return false;
                }
            }

            var pendingPackages = new Queue<string>(packageReferences.Select(reference =>
                reference.Id));
            var visitedPackages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var assets = new List<FSharpPackageCompileAsset>();
            while (pendingPackages.TryDequeue(out string? packageId))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!visitedPackages.Add(packageId)) continue;
                if (!packageTargets.TryGetValue(packageId,
                        out (string LibraryKey, string ExpectedLibraryPath,
                            JsonElement Target) packageTarget) ||
                    !libraries.TryGetProperty(packageTarget.LibraryKey,
                        out JsonElement library) ||
                    library.ValueKind != JsonValueKind.Object ||
                    !library.TryGetProperty("type", out JsonElement libraryType) ||
                    !libraryType.ValueEquals("package"u8) ||
                    !library.TryGetProperty("path", out JsonElement libraryPathElement) ||
                    libraryPathElement.ValueKind != JsonValueKind.String ||
                    !TryGetSafeRelativeFSharpAssetPath(libraryPathElement.GetString(),
                        out string? libraryPath) ||
                    !libraryPath!.Equals(packageTarget.ExpectedLibraryPath, PathComparison))
                {
                    error = "fsharp_semantic_package_assets_unavailable";
                    return false;
                }

                if (packageTarget.Target.TryGetProperty("dependencies",
                        out JsonElement dependencies))
                {
                    if (dependencies.ValueKind != JsonValueKind.Object)
                    {
                        error = "fsharp_semantic_package_assets_unavailable";
                        return false;
                    }
                    foreach (JsonProperty dependency in dependencies.EnumerateObject())
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!packageTargets.ContainsKey(dependency.Name))
                        {
                            error = "fsharp_semantic_package_assets_unavailable";
                            return false;
                        }
                        pendingPackages.Enqueue(dependency.Name);
                    }
                }

                if (!packageTarget.Target.TryGetProperty("compile", out JsonElement compile))
                    continue;
                if (compile.ValueKind != JsonValueKind.Object)
                {
                    error = "fsharp_semantic_package_assets_unavailable";
                    return false;
                }

                string? libraryDirectory = null;
                string? selectedPackageRoot = null;
                foreach (string packageRoot in packageRoots!)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    BeforeFSharpPackageRootProbeForTest?.Invoke(packageRoot);
                    string candidate = Path.Combine(packageRoot,
                        libraryPath!.Replace('/', Path.DirectorySeparatorChar));
                    if (!TryNormalizeContainedPath(candidate, packageRoot,
                            out string? normalized) || !Directory.Exists(normalized))
                        continue;
                    libraryDirectory = normalized;
                    selectedPackageRoot = packageRoot;
                    break;
                }
                if (libraryDirectory is null || selectedPackageRoot is null)
                {
                    error = "fsharp_semantic_package_asset_unavailable";
                    return false;
                }

                foreach (JsonProperty compileAsset in compile.EnumerateObject())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (++dependencyNodes > ProjectFileParser.MaxFSharpSemanticDependencyNodes)
                    {
                        error = "fsharp_semantic_dependency_limit";
                        return false;
                    }
                    if (compileAsset.Name.EndsWith("/_._", StringComparison.Ordinal) ||
                        compileAsset.Name.Equals("_._", StringComparison.Ordinal))
                        continue;
                    if (!TryGetSafeRelativeFSharpAssetPath(compileAsset.Name,
                            out string? assetPath) ||
                        !Path.GetExtension(assetPath!).Equals(".dll",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        error = "fsharp_semantic_package_assets_unavailable";
                        return false;
                    }

                    string candidate = Path.Combine(libraryDirectory,
                        assetPath!.Replace('/', Path.DirectorySeparatorChar));
                    if (!TryNormalizeContainedPath(candidate, selectedPackageRoot,
                            out string? normalized) || !File.Exists(normalized))
                    {
                        error = "fsharp_semantic_package_asset_unavailable";
                        return false;
                    }
                    assets.Add(new($"package:{packageTarget.LibraryKey}/{assetPath}",
                        normalized!, selectedPackageRoot));
                }
            }

            snapshot = new(string.Join("|", new[] { projectIdentity!, assetsIdentity! }
                    .Concat(authorityIdentities)), assets
                .DistinctBy(asset => asset.FullPath, WorkspacePaths.FileSystemPathComparer)
                .OrderBy(asset => asset.SourceIdentity, StringComparer.Ordinal)
                .ToList());
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            error = "fsharp_semantic_package_assets_unavailable";
            return false;
        }
    }

    private bool TryReadVerifiedFSharpInput(string sourceIdentity, string fullPath,
        string allowedRoot, string requiredExtension, CancellationToken cancellationToken,
        out byte[]? bytes, out string? identity)
    {
        bytes = null;
        identity = null;
        if (!TryOpenVerifiedReference(sourceIdentity, fullPath, allowedRoot,
                requiredExtension, out FileStream? stream))
            return false;
        using (FileStream input = stream!)
        {
            if (input.Length < 0 || input.Length > IndexBuilder.MaxStructuralFileBytes)
                return false;
            bytes = new byte[checked((int)input.Length)];
            int offset = 0;
            while (offset < bytes.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int read = input.Read(bytes, offset, bytes.Length - offset);
                if (read == 0) return false;
                offset += read;
            }
            if (input.ReadByte() != -1 || input.Length != bytes.Length) return false;
            identity = $"{Path.GetFullPath(fullPath)}|{bytes.Length}|" +
                       Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            return true;
        }
    }

    private static bool FSharpInputTextEquals(byte[] bytes, string indexedContent)
    {
        using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd().Equals(indexedContent, StringComparison.Ordinal);
    }

    private static bool TryValidateFSharpAssetsProject(JsonElement root,
        string projectFullPath, string targetFramework,
        IReadOnlyCollection<FSharpPackageReferenceSnapshot> packageReferences)
    {
        if (!root.TryGetProperty("project", out JsonElement project) ||
            project.ValueKind != JsonValueKind.Object ||
            !project.TryGetProperty("restore", out JsonElement restore) ||
            restore.ValueKind != JsonValueKind.Object ||
            !restore.TryGetProperty("projectPath", out JsonElement restoredPath) ||
            restoredPath.ValueKind != JsonValueKind.String ||
            !Path.GetFullPath(restoredPath.GetString()!).Equals(
                Path.GetFullPath(projectFullPath), PathComparison) ||
            !project.TryGetProperty("frameworks", out JsonElement frameworks) ||
            frameworks.ValueKind != JsonValueKind.Object ||
            !TryGetFSharpAssetsFrameworkProperty(frameworks, targetFramework,
                out JsonElement framework) ||
            framework.ValueKind != JsonValueKind.Object ||
            !framework.TryGetProperty("dependencies", out JsonElement dependencies) ||
            dependencies.ValueKind != JsonValueKind.Object)
            return false;

        if (!root.TryGetProperty("targets", out JsonElement targets) ||
            targets.ValueKind != JsonValueKind.Object ||
            !TryGetFSharpAssetsFrameworkProperty(targets, targetFramework,
                out JsonElement selectedTarget) ||
            selectedTarget.ValueKind != JsonValueKind.Object)
            return false;

        var dependencyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var directPackageDependencies = new Dictionary<string, JsonElement>(
            StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty property in dependencies.EnumerateObject())
        {
            if (!dependencyNames.Add(property.Name) ||
                property.Value.ValueKind != JsonValueKind.Object ||
                !property.Value.TryGetProperty("target", out JsonElement target) ||
                target.ValueKind != JsonValueKind.String)
                return false;
            if (!target.GetString()!.Equals("Package", StringComparison.OrdinalIgnoreCase))
                continue;
            if (property.Value.TryGetProperty("autoReferenced",
                    out JsonElement autoReferenced))
            {
                if (autoReferenced.ValueKind is not (JsonValueKind.True or
                    JsonValueKind.False))
                    return false;
                if (autoReferenced.GetBoolean())
                {
                    if (!property.Value.TryGetProperty("version",
                            out JsonElement autoReferencedVersion) ||
                        autoReferencedVersion.ValueKind != JsonValueKind.String ||
                        !TryParseFSharpPackageVersionConstraint(
                            autoReferencedVersion.GetString(),
                            out FSharpPackageVersionConstraint? autoReferencedConstraint) ||
                        !TryGetSelectedFSharpPackageVersion(selectedTarget, property.Name,
                            out FSharpNuGetVersion? selectedAutoReferencedVersion) ||
                        !FSharpPackageConstraintContains(autoReferencedConstraint!,
                            selectedAutoReferencedVersion!))
                        return false;
                    continue;
                }
            }
            if (!directPackageDependencies.TryAdd(property.Name, property.Value))
                return false;
        }

        var evaluatedPackageIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (FSharpPackageReferenceSnapshot reference in packageReferences)
        {
            if (!evaluatedPackageIds.Add(reference.Id)) return false;
        }
        if (!evaluatedPackageIds.SetEquals(directPackageDependencies.Keys)) return false;

        foreach (FSharpPackageReferenceSnapshot reference in packageReferences)
        {
            if (!directPackageDependencies.TryGetValue(reference.Id,
                    out JsonElement dependency))
                return false;

            if (!TryParseFSharpPackageVersionConstraint(reference.RequestedVersion,
                    out FSharpPackageVersionConstraint? requestedConstraint) ||
                !dependency.TryGetProperty("version", out JsonElement restoredVersion) ||
                restoredVersion.ValueKind != JsonValueKind.String ||
                !TryParseFSharpPackageVersionConstraint(restoredVersion.GetString(),
                    out FSharpPackageVersionConstraint? restoredConstraint) ||
                !requestedConstraint!.Canonical.Equals(restoredConstraint!.Canonical,
                    StringComparison.OrdinalIgnoreCase) ||
                !TryGetSelectedFSharpPackageVersion(selectedTarget, reference.Id,
                    out FSharpNuGetVersion? selectedVersion) ||
                !FSharpPackageConstraintContains(requestedConstraint, selectedVersion!))
                return false;
        }
        return true;
    }

    private static bool TryGetSelectedFSharpPackageVersion(JsonElement selectedTarget,
        string packageId, out FSharpNuGetVersion? selectedVersion)
    {
        selectedVersion = null;
        string prefix = $"{packageId}/";
        foreach (JsonProperty library in selectedTarget.EnumerateObject())
        {
            if (!library.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                library.Value.ValueKind != JsonValueKind.Object ||
                !library.Value.TryGetProperty("type", out JsonElement type) ||
                !type.ValueEquals("package"u8))
                continue;
            if (selectedVersion is not null ||
                !TryParseFSharpNuGetVersion(library.Name[prefix.Length..],
                    out selectedVersion))
                return false;
        }
        return selectedVersion is not null;
    }

    private static bool TryParseFSharpPackageVersionConstraint(string? expression,
        out FSharpPackageVersionConstraint? constraint)
    {
        constraint = null;
        if (string.IsNullOrWhiteSpace(expression)) return false;
        string value = expression.Trim();

        if (value.EndsWith(".*", StringComparison.Ordinal))
            return TryParseFSharpFloatingPackageConstraint(value, out constraint);

        if (value.Length >= 3 && value[0] == '[' && value[^1] == ']' &&
            !value.Contains(','))
        {
            if (!TryParseFSharpNuGetVersion(value[1..^1].Trim(),
                    out FSharpNuGetVersion? exact))
                return false;
            constraint = new($"[{exact!.Normalized}]", exact, true, exact, true, null);
            return true;
        }

        if (value.Length >= 3 && value[0] is '[' or '(' && value[^1] is ']' or ')')
        {
            string body = value[1..^1];
            int comma = body.IndexOf(',');
            if (comma < 0 || comma != body.LastIndexOf(',')) return false;
            string lowerText = body[..comma].Trim();
            string upperText = body[(comma + 1)..].Trim();
            if (lowerText.Length == 0 && value[0] != '(' ||
                upperText.Length == 0 && value[^1] != ')' ||
                lowerText.Length == 0 && upperText.Length == 0)
                return false;
            if (lowerText.EndsWith(".*", StringComparison.Ordinal) &&
                upperText.Length == 0 && value[0] == '[' && value[^1] == ')')
                return TryParseFSharpFloatingPackageConstraint(lowerText, out constraint);
            FSharpNuGetVersion? lower = null;
            FSharpNuGetVersion? upper = null;
            if (lowerText.Length > 0 &&
                !TryParseFSharpNuGetVersion(lowerText, out lower))
                return false;
            if (upperText.Length > 0 &&
                !TryParseFSharpNuGetVersion(upperText, out upper))
                return false;
            bool lowerInclusive = value[0] == '[';
            bool upperInclusive = value[^1] == ']';
            if (lower is not null && upper is not null)
            {
                int comparison = CompareFSharpNuGetVersions(lower, upper);
                if (comparison > 0 || comparison == 0 &&
                    (!lowerInclusive || !upperInclusive))
                    return false;
            }
            constraint = new($"{value[0]}{lower?.Normalized ?? ""}," +
                             $"{upper?.Normalized ?? ""}{value[^1]}", lower,
                lowerInclusive, upper, upperInclusive, null);
            return true;
        }

        if (!TryParseFSharpNuGetVersion(value, out FSharpNuGetVersion? minimum))
            return false;
        constraint = new($"[{minimum!.Normalized},)", minimum, true, null, false, null);
        return true;
    }

    private static bool TryParseFSharpFloatingPackageConstraint(string expression,
        out FSharpPackageVersionConstraint? constraint)
    {
        constraint = null;
        if (!expression.EndsWith(".*", StringComparison.Ordinal)) return false;
        string[] components = expression[..^2].Split('.');
        if (components.Length is < 1 or > 3 || components.Any(component =>
                !ulong.TryParse(component, out _)))
            return false;
        ulong[] prefix = components.Select(ulong.Parse).ToArray();
        ulong[] lowerParts = [0, 0, 0, 0];
        Array.Copy(prefix, lowerParts, prefix.Length);
        ulong[] upperParts = (ulong[])lowerParts.Clone();
        if (upperParts[prefix.Length - 1] == ulong.MaxValue) return false;
        upperParts[prefix.Length - 1]++;
        for (int index = prefix.Length; index < upperParts.Length; index++)
            upperParts[index] = 0;
        FSharpNuGetVersion lower = CreateFSharpNuGetVersion(lowerParts, []);
        FSharpNuGetVersion upper = CreateFSharpNuGetVersion(upperParts, []);
        constraint = new($"[{lower.Normalized},{upper.Normalized})", lower, true,
            upper, false, prefix);
        return true;
    }

    private static bool TryParseFSharpNuGetVersion(string? version,
        out FSharpNuGetVersion? parsed)
    {
        parsed = null;
        if (string.IsNullOrWhiteSpace(version) || version.Any(char.IsWhiteSpace) ||
            version.IndexOfAny(['*', '[', ']', '(', ')', ',']) >= 0)
            return false;

        string value = version;
        int metadata = value.IndexOf('+');
        if (metadata >= 0)
        {
            if (metadata == value.Length - 1) return false;
            value = value[..metadata];
        }
        int prerelease = value.IndexOf('-');
        string[] release = prerelease >= 0
            ? value[(prerelease + 1)..].Split('.')
            : [];
        if (release.Any(label => label.Length == 0 || label.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character != '-')))
            return false;
        string core = prerelease >= 0 ? value[..prerelease] : value;
        string[] components = core.Split('.');
        if (components.Length is < 1 or > 4 || components.Any(component =>
                !ulong.TryParse(component, out _)))
            return false;
        ulong[] numbers = [0, 0, 0, 0];
        for (int index = 0; index < components.Length; index++)
            numbers[index] = ulong.Parse(components[index]);
        parsed = CreateFSharpNuGetVersion(numbers, release);
        return true;
    }

    private static FSharpNuGetVersion CreateFSharpNuGetVersion(ulong[] numbers,
        string[] prerelease)
    {
        string core = $"{numbers[0]}.{numbers[1]}.{numbers[2]}" +
                      (numbers[3] == 0 ? "" : $".{numbers[3]}");
        string normalized = prerelease.Length == 0
            ? core
            : $"{core}-{string.Join('.', prerelease)}";
        return new(numbers[0], numbers[1], numbers[2], numbers[3], prerelease,
            normalized);
    }

    private static bool FSharpPackageConstraintContains(
        FSharpPackageVersionConstraint constraint, FSharpNuGetVersion version)
    {
        if (constraint.FloatingPrefix is { } prefix)
        {
            ulong[] actual = [version.Major, version.Minor, version.Patch,
                version.Revision];
            for (int index = 0; index < prefix.Length; index++)
                if (actual[index] != prefix[index]) return false;
        }
        if (constraint.Lower is not null)
        {
            int comparison = CompareFSharpNuGetVersions(version, constraint.Lower);
            if (comparison < 0 || comparison == 0 && !constraint.LowerInclusive)
                return false;
        }
        if (constraint.Upper is not null)
        {
            int comparison = CompareFSharpNuGetVersions(version, constraint.Upper);
            if (comparison > 0 || comparison == 0 && !constraint.UpperInclusive)
                return false;
        }
        return true;
    }

    private static int CompareFSharpNuGetVersions(FSharpNuGetVersion left,
        FSharpNuGetVersion right)
    {
        foreach ((ulong leftPart, ulong rightPart) in new[]
                 {
                     (left.Major, right.Major), (left.Minor, right.Minor),
                     (left.Patch, right.Patch), (left.Revision, right.Revision),
                 })
        {
            int comparison = leftPart.CompareTo(rightPart);
            if (comparison != 0) return comparison;
        }
        if (left.Prerelease.Length == 0) return right.Prerelease.Length == 0 ? 0 : 1;
        if (right.Prerelease.Length == 0) return -1;
        int common = Math.Min(left.Prerelease.Length, right.Prerelease.Length);
        for (int index = 0; index < common; index++)
        {
            string leftLabel = left.Prerelease[index];
            string rightLabel = right.Prerelease[index];
            bool leftNumeric = ulong.TryParse(leftLabel, out ulong leftNumber);
            bool rightNumeric = ulong.TryParse(rightLabel, out ulong rightNumber);
            int comparison = leftNumeric && rightNumeric
                ? leftNumber.CompareTo(rightNumber)
                : leftNumeric
                    ? -1
                    : rightNumeric
                        ? 1
                        : StringComparer.OrdinalIgnoreCase.Compare(leftLabel, rightLabel);
            if (comparison != 0) return comparison;
        }
        return left.Prerelease.Length.CompareTo(right.Prerelease.Length);
    }

    private static bool TryGetFSharpAssetsFrameworkProperty(JsonElement value,
        string targetFramework, out JsonElement propertyValue)
    {
        string? fullName = TryGetFSharpNuGetFrameworkName(targetFramework);
        JsonElement match = default;
        int matches = 0;
        foreach (JsonProperty property in value.EnumerateObject())
        {
            if (!property.Name.Equals(targetFramework, StringComparison.OrdinalIgnoreCase) &&
                (fullName is null || !property.Name.Equals(fullName,
                    StringComparison.OrdinalIgnoreCase)))
                continue;
            match = property.Value;
            matches++;
        }
        propertyValue = match;
        return matches == 1;
    }

    private static string? TryGetFSharpNuGetFrameworkName(string targetFramework)
    {
        if (targetFramework.StartsWith("netstandard", StringComparison.OrdinalIgnoreCase))
        {
            string version = targetFramework["netstandard".Length..];
            return Version.TryParse(version, out _)
                ? $".NETStandard,Version=v{version}"
                : null;
        }
        if (targetFramework.StartsWith("netcoreapp", StringComparison.OrdinalIgnoreCase))
        {
            string version = targetFramework["netcoreapp".Length..];
            return Version.TryParse(version, out _)
                ? $".NETCoreApp,Version=v{version}"
                : null;
        }
        if (!targetFramework.StartsWith("net", StringComparison.OrdinalIgnoreCase))
            return null;
        string digits = targetFramework[3..];
        if (digits.Length is < 2 or > 4 || digits.Any(character =>
                !char.IsAsciiDigit(character)))
            return null;
        string versionText = string.Join('.', digits.Select(character => character.ToString()));
        return $".NETFramework,Version=v{versionText}";
    }

    private bool TryGetFSharpPackageRoots(JsonElement root,
        CancellationToken cancellationToken,
        out List<string>? packageRoots)
        => TryGetFSharpPackageRootsForEnvironment(root, cancellationToken,
            _manager.WorkspaceRoot, Environment.GetEnvironmentVariable("NUGET_PACKAGES"),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), out packageRoots);

    internal static bool TryGetFSharpPackageRootsForEnvironment(JsonElement root,
        CancellationToken cancellationToken, string workspaceRoot,
        string? configuredPackagesRoot, string userProfile,
        out List<string>? packageRoots)
    {
        packageRoots = null;
        if (!root.TryGetProperty("packageFolders", out JsonElement folders) ||
            folders.ValueKind != JsonValueKind.Object)
            return false;
        var roots = new List<string>();
        foreach (JsonProperty folder in folders.EnumerateObject())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Path.IsPathFullyQualified(folder.Name)) return false;
            string rootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder.Name));
            if (!IsAllowedFSharpPackageRootForEnvironment(rootPath, workspaceRoot,
                    configuredPackagesRoot, userProfile))
                continue;
            if (!Directory.Exists(rootPath)) continue;
            roots.Add(rootPath);
        }
        packageRoots = roots.Distinct(WorkspacePaths.FileSystemPathComparer).ToList();
        return packageRoots.Count > 0;
    }

    internal static bool IsAllowedFSharpPackageRootForEnvironment(string packageRoot,
        string workspaceRoot, string? configuredPackagesRoot, string userProfile)
    {
        string normalizedWorkspaceRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(workspaceRoot));
        string normalizedPackageRoot = Path.TrimEndingDirectorySeparator(
            Path.GetFullPath(packageRoot));
        if (normalizedPackageRoot.Equals(normalizedWorkspaceRoot,
                WorkspacePaths.FileSystemPathComparison) ||
            normalizedPackageRoot.StartsWith(
                normalizedWorkspaceRoot + Path.DirectorySeparatorChar,
                WorkspacePaths.FileSystemPathComparison))
            return true;

        string? allowedExternalRoot = !string.IsNullOrWhiteSpace(configuredPackagesRoot)
            ? configuredPackagesRoot
            : userProfile.Length > 0
                ? Path.Combine(userProfile, ".nuget", "packages")
                : null;
        return allowedExternalRoot is not null &&
               Path.TrimEndingDirectorySeparator(Path.GetFullPath(allowedExternalRoot))
                   .Equals(normalizedPackageRoot, WorkspacePaths.FileSystemPathComparison);
    }

    private static bool TryGetSafeRelativeFSharpAssetPath(string? value,
        out string? normalized)
    {
        normalized = null;
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathFullyQualified(value)) return false;
        string gitPath = value.Replace('\\', '/').Trim('/');
        if (gitPath.Length == 0 || gitPath.Split('/').Any(part =>
                part.Length == 0 || part is "." or ".."))
            return false;
        normalized = gitPath;
        return true;
    }
}
