using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace CodeNav.Core.Discovery;

public sealed record EvaluatedParseContext(
    string ContextKey,
    string LanguageVersion,
    IReadOnlyList<string> PreprocessorSymbols,
    bool Complete);

public sealed record EvaluatedOutput(
    string OutputPath,
    string TargetPath,
    string? Condition,
    string Evidence = "evaluated");

public sealed record EvaluatedItemFact(
    string Kind,
    string Include,
    string? Metadata,
    string? Condition,
    string EvaluationStatus);

public sealed record EvaluatedVariantCoverage(
    bool ParseContextComplete,
    bool CompileOwnershipComplete,
    bool ReferenceGraphComplete,
    IReadOnlyList<string> Reasons)
{
    public bool Complete => ParseContextComplete && CompileOwnershipComplete && ReferenceGraphComplete;
}

public sealed record EvaluatedProjectVariant(
    string DimensionKey,
    string StableVariantKey,
    string TargetFramework,
    string Configuration,
    string Platform,
    string AssemblyName,
    string TargetName,
    string TargetExt,
    EvaluatedParseContext ParseContext,
    IReadOnlyList<EvaluatedOutput> Outputs,
    IReadOnlyList<EvaluatedItemFact> CompileFacts,
    IReadOnlyList<EvaluatedItemFact> ProjectReferenceFacts,
    IReadOnlyList<EvaluatedItemFact> AssemblyReferenceFacts,
    IReadOnlyList<EvaluatedItemFact> PackageReferenceFacts,
    IReadOnlyList<string> StructuralInputPaths,
    EvaluatedVariantCoverage Coverage);

public sealed record ProjectVariantEvaluation(
    string ProjectPath,
    string AssemblyName,
    IReadOnlyList<EvaluatedProjectVariant> Variants,
    bool Complete,
    IReadOnlyList<string> Reasons);

/// <summary>Bounded, MSBuild-free evaluation of the project facts that affect semantic variants.
/// Unknown constructs are preserved as incomplete evidence; they are never silently false.</summary>
public static class ProjectVariantEvaluator
{
    public const int MaxDimensionTuples = 256;
    public const int MaxExpansionDepth = 32;
    public const int MaxSubstitutionsPerTuple = 4096;
    private const int MaxProjectBytes = 16 * 1024 * 1024;
    private static readonly Regex PropertyReference = new(@"\$\(([^)]+)\)", RegexOptions.Compiled);

    public static ProjectVariantEvaluation Evaluate(string projectRelativePath,
        ReadOnlyMemory<byte> exactXmlBytes, byte[]? packagesConfigBytes = null,
        CancellationToken cancellationToken = default)
    {
        string relPath = WorkspacePaths.Normalize(projectRelativePath);
        var globalReasons = new HashSet<string>(StringComparer.Ordinal);
        XDocument document;
        try
        {
            if (exactXmlBytes.Length > MaxProjectBytes)
                throw new InvalidDataException("project XML exceeds evaluator limit");
            using var stream = new MemoryStream(exactXmlBytes.ToArray(), writable: false);
            using XmlReader reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaxProjectBytes,
            });
            document = XDocument.Load(reader, LoadOptions.PreserveWhitespace);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            string fallback = Path.GetFileNameWithoutExtension(relPath);
            return new ProjectVariantEvaluation(relPath, fallback, [], false,
                [$"project_parse_failed:{ex.GetType().Name}"]);
        }

        XElement root = document.Root ?? throw new InvalidDataException("project root is missing");
        bool sdkStyle = root.Attribute("Sdk") is not null;

        IReadOnlyList<EvaluatedItemFact> packagesConfigFacts = ParsePackagesConfig(
            packagesConfigBytes, globalReasons);

        IReadOnlyList<string> tfms = DimensionValues(root, "TargetFrameworks", "TargetFramework");
        if (tfms.Count == 0)
        {
            string? legacy = LiteralPropertyValues(root, "TargetFrameworkVersion").FirstOrDefault();
            tfms = legacy is null ? [""] : ["net" + legacy.Trim().TrimStart('v').Replace(".", "")];
        }
        IReadOnlyList<string> configurations = DimensionValues(root, "Configurations");
        if (configurations.Count == 0) configurations = ConditionDimensionValues(root, "Configuration");
        if (configurations.Count == 0) configurations = ["Debug"];
        IReadOnlyList<string> platforms = DimensionValues(root, "Platforms");
        if (platforms.Count == 0) platforms = ConditionDimensionValues(root, "Platform");
        if (platforms.Count == 0) platforms = ["AnyCPU"];

        var tuples = (from tfm in tfms from configuration in configurations from platform in platforms
            select (Tfm: tfm, Configuration: configuration, Platform: platform)).ToList();
        if (tuples.Count > MaxDimensionTuples)
        {
            globalReasons.Add("variant_dimension_limit");
            tuples = tuples.Take(MaxDimensionTuples).ToList();
        }

        var variants = new List<EvaluatedProjectVariant>(tuples.Count);
        foreach (var tuple in tuples)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var reasons = new HashSet<string>(globalReasons, StringComparer.Ordinal);
            var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["TargetFramework"] = tuple.Tfm,
                ["Configuration"] = tuple.Configuration,
                ["Platform"] = tuple.Platform,
                ["MSBuildProjectName"] = Path.GetFileNameWithoutExtension(relPath),
                ["MSBuildProjectDirectory"] = WorkspacePaths.ToGitPath(Path.GetDirectoryName(relPath) ?? ""),
            };
            int substitutions = 0;
            foreach (XElement group in root.Elements().Where(e => e.Name.LocalName == "PropertyGroup"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                ConditionResult groupCondition = EvaluateCondition(group.Attribute("Condition")?.Value,
                    properties, ref substitutions, reasons);
                if (groupCondition == ConditionResult.False) continue;
                if (groupCondition == ConditionResult.Unknown) reasons.Add("unknown_property_condition");
                foreach (XElement property in group.Elements())
                {
                    ConditionResult propertyCondition = EvaluateCondition(property.Attribute("Condition")?.Value,
                        properties, ref substitutions, reasons);
                    if (propertyCondition == ConditionResult.False) continue;
                    if (propertyCondition == ConditionResult.Unknown) reasons.Add("unknown_property_condition");
                    properties[property.Name.LocalName] = Expand(property.Value, properties, ref substitutions, reasons);
                }
            }

            var structuralInputs = new HashSet<string>(WorkspacePaths.FileSystemPathComparer);
            foreach (XElement import in root.Descendants().Where(element =>
                         element.Name.LocalName == "Import"))
            {
                ConditionResult importCondition = EvaluateCondition(import.Attribute("Condition")?.Value,
                    properties, ref substitutions, reasons);
                if (importCondition == ConditionResult.False) continue;
                if (import.Attribute("Project")?.Value is not { Length: > 0 } importedPath) continue;
                reasons.Add("unsupported_import");
                string expanded = Expand(importedPath, properties, ref substitutions, reasons);
                if (!expanded.Contains("$(", StringComparison.Ordinal))
                    structuralInputs.Add(NormalizeFromProject(relPath, expanded));
            }

            string assemblyName = Value(properties, "AssemblyName", Path.GetFileNameWithoutExtension(relPath));
            string targetName = Value(properties, "TargetName",
                assemblyName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                    ? assemblyName[..^4]
                    : assemblyName);
            string targetExt = Value(properties, "TargetExt",
                string.Equals(Value(properties, "OutputType", "Library"), "Exe", StringComparison.OrdinalIgnoreCase)
                    ? ".exe" : ".dll");
            string outputPath = Value(properties, "OutDir", Value(properties, "OutputPath", ""));
            if (string.IsNullOrWhiteSpace(outputPath))
                outputPath = CombineGit(Value(properties, "BaseOutputPath", "bin"), tuple.Configuration);
            bool appendTfm = sdkStyle && !string.Equals(Value(properties,
                "AppendTargetFrameworkToOutputPath", "true"), "false", StringComparison.OrdinalIgnoreCase);
            if (appendTfm && tuple.Tfm.Length > 0 && !EndsWithSegment(outputPath, tuple.Tfm))
                outputPath = CombineGit(outputPath, tuple.Tfm);
            outputPath = NormalizeFromProject(relPath, outputPath);
            string targetPath = CombineGit(outputPath, targetName + targetExt);

            var compileFacts = new List<EvaluatedItemFact>();
            var projectRefs = new List<EvaluatedItemFact>();
            var assemblyRefs = new List<EvaluatedItemFact>();
            var packageRefs = new List<EvaluatedItemFact>(packagesConfigFacts);
            bool compileComplete = true;
            bool graphComplete = !reasons.Any(reason => reason.StartsWith(
                "packages_parse_failed:", StringComparison.Ordinal));
            foreach (XElement group in root.Elements().Where(e => e.Name.LocalName == "ItemGroup"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                ConditionResult groupCondition = EvaluateCondition(group.Attribute("Condition")?.Value,
                    properties, ref substitutions, reasons);
                foreach (XElement item in group.Elements())
                {
                    string kind = item.Name.LocalName;
                    if (kind is not ("Compile" or "ProjectReference" or "Reference" or "PackageReference")) continue;
                    ConditionResult itemCondition = groupCondition == ConditionResult.False
                        ? ConditionResult.False
                        : EvaluateCondition(item.Attribute("Condition")?.Value,
                            properties, ref substitutions, reasons);
                    ConditionResult effectiveCondition = And(groupCondition, itemCondition);
                    bool known = effectiveCondition != ConditionResult.Unknown;
                    string status = effectiveCondition switch
                    {
                        ConditionResult.True => "evaluated",
                        ConditionResult.False => "excluded",
                        _ => "unknown",
                    };
                    string? rawInclude = item.Attribute("Include")?.Value ?? item.Attribute("Remove")?.Value;
                    if (rawInclude is null) continue;
                    string? condition = item.Attribute("Condition")?.Value ?? group.Attribute("Condition")?.Value;
                    if (effectiveCondition == ConditionResult.False) continue;
                    switch (kind)
                    {
                        case "Compile":
                            if (!known) compileComplete = false;
                            string operation = item.Attribute("Remove") is null ? "include" : "remove";
                            string compilePath = NormalizeFromProject(relPath,
                                Expand(rawInclude, properties, ref substitutions, reasons));
                            compileFacts.Add(new EvaluatedItemFact(operation, compilePath,
                                item.Attribute("Exclude")?.Value, condition, status));
                            break;
                        case "ProjectReference":
                            if (!known) graphComplete = false;
                            string include = Expand(rawInclude, properties, ref substitutions, reasons);
                            projectRefs.Add(new EvaluatedItemFact("projectReference",
                                NormalizeFromProject(relPath, include), Child(item, "SetTargetFramework") ??
                                Child(item, "AdditionalProperties"), condition, status));
                            break;
                        case "Reference":
                            if (!known) graphComplete = false;
                            include = Expand(rawInclude, properties, ref substitutions, reasons);
                            string simple = include.Split(',')[0].Trim();
                            string? hint = Child(item, "HintPath");
                            assemblyRefs.Add(new EvaluatedItemFact("assemblyReference", simple,
                                hint is null ? null : NormalizeFromProject(relPath,
                                    Expand(hint, properties, ref substitutions, reasons)), condition, status));
                            break;
                        case "PackageReference":
                            if (!known) graphComplete = false;
                            include = Expand(rawInclude, properties, ref substitutions, reasons);
                            packageRefs.Add(new EvaluatedItemFact("packageReference", include,
                                item.Attribute("Version")?.Value ?? Child(item, "Version") ?? "", condition, status));
                            break;
                    }
                }
            }

            var symbols = new HashSet<string>(StringComparer.Ordinal);
            foreach (string symbol in Value(properties, "DefineConstants", "")
                         .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                symbols.Add(symbol);
            AddFrameworkSymbols(tuple.Tfm, symbols, reasons);
            string language = Value(properties, "LangVersion", "latest");
            string contextKey = $"lang={language}|defines={string.Join(',', symbols.OrderBy(s => s, StringComparer.Ordinal))}";
            bool parseComplete = !reasons.Any(r =>
                r is "unknown_property_condition" or "unknown_tfm_symbols");
            string dimensionKey = $"tfm={tuple.Tfm}|configuration={tuple.Configuration}|platform={tuple.Platform}";
            string stableKey = $"{relPath}|{dimensionKey}";
            var coverage = new EvaluatedVariantCoverage(parseComplete, compileComplete, graphComplete,
                reasons.OrderBy(r => r, StringComparer.Ordinal).ToList());
            variants.Add(new EvaluatedProjectVariant(dimensionKey, stableKey, tuple.Tfm,
                tuple.Configuration, tuple.Platform, assemblyName, targetName, targetExt,
                new EvaluatedParseContext(contextKey, language,
                    symbols.OrderBy(s => s, StringComparer.Ordinal).ToList(), parseComplete),
                [new EvaluatedOutput(outputPath, targetPath, null)], compileFacts, projectRefs,
                assemblyRefs, packageRefs, structuralInputs.OrderBy(path => path,
                    StringComparer.Ordinal).ToList(), coverage));
        }

        string projectAssembly = variants.FirstOrDefault()?.AssemblyName ?? Path.GetFileNameWithoutExtension(relPath);
        return new ProjectVariantEvaluation(relPath, projectAssembly, variants,
            globalReasons.Count == 0 && variants.All(v => v.Coverage.Complete),
            globalReasons.OrderBy(r => r, StringComparer.Ordinal).ToList());
    }

    private static IReadOnlyList<string> DimensionValues(XElement root, params string[] names)
    {
        foreach (string name in names)
        {
            var values = LiteralPropertyValues(root, name)
                .SelectMany(v => v.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (values.Count > 0) return values;
        }
        return [];
    }

    private static IReadOnlyList<string> ConditionDimensionValues(XElement root, string propertyName)
    {
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string condition in root.DescendantsAndSelf().Attributes("Condition")
                     .Select(attribute => attribute.Value))
        {
            Match pair = Regex.Match(condition,
                """\$\(Configuration\)\s*\|\s*\$\(Platform\)\s*['"]?\s*==\s*['"](?<value>[^'"]+)['"]""",
                RegexOptions.IgnoreCase);
            if (pair.Success)
            {
                string[] dimensions = pair.Groups["value"].Value.Split('|');
                int index = propertyName.Equals("Configuration", StringComparison.OrdinalIgnoreCase) ? 0 : 1;
                if (dimensions.Length > index && !string.IsNullOrWhiteSpace(dimensions[index]))
                    values.Add(dimensions[index].Trim());
                continue;
            }
            Match direct = Regex.Match(condition,
                @"\$\(" + Regex.Escape(propertyName) +
                """\)\s*['"]?\s*==\s*['"](?<value>[^'"]*)['"]""",
                RegexOptions.IgnoreCase);
            if (direct.Success && !string.IsNullOrWhiteSpace(direct.Groups["value"].Value))
                values.Add(direct.Groups["value"].Value.Trim());
        }
        return values.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IReadOnlyList<EvaluatedItemFact> ParsePackagesConfig(
        byte[]? bytes, HashSet<string> reasons)
    {
        if (bytes is null) return [];
        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using XmlReader reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaxProjectBytes,
            });
            XDocument document = XDocument.Load(reader);
            return document.Descendants().Where(element => element.Name.LocalName == "package")
                .Select(element => new EvaluatedItemFact("packageReference",
                    element.Attribute("id")?.Value ?? "", element.Attribute("version")?.Value ?? "",
                    null, "evaluated"))
                .Where(fact => fact.Include.Length > 0).ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            reasons.Add($"packages_parse_failed:{ex.GetType().Name}");
            return [];
        }
    }

    private static IEnumerable<string> LiteralPropertyValues(XElement root, string name) =>
        root.Descendants().Where(e => e.Name.LocalName == name && !e.Value.Contains("$(", StringComparison.Ordinal))
            .Select(e => e.Value.Trim()).Where(v => v.Length > 0);

    private static string Value(IReadOnlyDictionary<string, string> values, string name, string fallback) =>
        values.TryGetValue(name, out string? value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : fallback;

    private static string Expand(string value, IReadOnlyDictionary<string, string> properties,
        ref int substitutions, HashSet<string> reasons)
    {
        string current = value;
        for (int depth = 0; depth < MaxExpansionDepth; depth++)
        {
            bool changed = false;
            var builder = new StringBuilder(current.Length);
            int copied = 0;
            foreach (Match match in PropertyReference.Matches(current))
            {
                builder.Append(current, copied, match.Index - copied);
                if (++substitutions > MaxSubstitutionsPerTuple)
                {
                    reasons.Add("property_expansion_limit");
                    builder.Append(match.Value);
                }
                else if (!properties.TryGetValue(match.Groups[1].Value, out string? replacement))
                {
                    reasons.Add("unresolved_property");
                    builder.Append(match.Value);
                }
                else
                {
                    changed = true;
                    builder.Append(replacement);
                }
                copied = match.Index + match.Length;
            }
            builder.Append(current, copied, current.Length - copied);
            current = builder.ToString();
            if (!changed) return current;
        }
        reasons.Add("property_expansion_limit");
        return current;
    }

    private static ConditionResult EvaluateCondition(string? condition,
        IReadOnlyDictionary<string, string> properties, ref int substitutions, HashSet<string> reasons)
    {
        if (string.IsNullOrWhiteSpace(condition)) return ConditionResult.True;
        string expanded = Expand(condition, properties, ref substitutions, reasons).Trim();
        if (expanded.Contains("$(", StringComparison.Ordinal) || expanded.Contains("Exists(", StringComparison.OrdinalIgnoreCase))
            return ConditionResult.Unknown;
        return EvaluateBoolean(expanded);
    }

    private static ConditionResult EvaluateBoolean(string expression)
    {
        expression = TrimOuterParentheses(expression.Trim());
        int or = IndexOfBooleanOperator(expression, "Or");
        if (or >= 0) return Or(EvaluateBoolean(expression[..or]), EvaluateBoolean(expression[(or + 2)..]));
        int and = IndexOfBooleanOperator(expression, "And");
        if (and >= 0) return And(EvaluateBoolean(expression[..and]), EvaluateBoolean(expression[(and + 3)..]));
        Match comparison = Regex.Match(expression, "^\\s*['\"]?(.*?)['\"]?\\s*(==|!=)\\s*['\"]?(.*?)['\"]?\\s*$");
        if (comparison.Success)
        {
            bool equals = string.Equals(comparison.Groups[1].Value.Trim(), comparison.Groups[3].Value.Trim(),
                StringComparison.OrdinalIgnoreCase);
            return comparison.Groups[2].Value == "==" == equals ? ConditionResult.True : ConditionResult.False;
        }
        if (bool.TryParse(expression.Trim(' ', '\'', '"'), out bool value))
            return value ? ConditionResult.True : ConditionResult.False;
        return ConditionResult.Unknown;
    }

    private static int IndexOfBooleanOperator(string expression, string op)
    {
        int depth = 0;
        for (int i = 0; i <= expression.Length - op.Length; i++)
        {
            if (expression[i] == '(') depth++;
            else if (expression[i] == ')') depth--;
            if (depth == 0 && expression.AsSpan(i, op.Length).Equals(op, StringComparison.OrdinalIgnoreCase) &&
                (i == 0 || char.IsWhiteSpace(expression[i - 1])) &&
                (i + op.Length == expression.Length || char.IsWhiteSpace(expression[i + op.Length]))) return i;
        }
        return -1;
    }

    private static string TrimOuterParentheses(string value)
    {
        while (value.Length >= 2 && value[0] == '(' && value[^1] == ')')
        {
            int depth = 0;
            bool wraps = true;
            for (int i = 0; i < value.Length; i++)
            {
                if (value[i] == '(') depth++;
                else if (value[i] == ')') depth--;
                if (depth == 0 && i < value.Length - 1) { wraps = false; break; }
            }
            if (!wraps) break;
            value = value[1..^1].Trim();
        }
        return value;
    }

    private static ConditionResult And(ConditionResult left, ConditionResult right) =>
        left == ConditionResult.False || right == ConditionResult.False ? ConditionResult.False :
        left == ConditionResult.True && right == ConditionResult.True ? ConditionResult.True : ConditionResult.Unknown;

    private static ConditionResult Or(ConditionResult left, ConditionResult right) =>
        left == ConditionResult.True || right == ConditionResult.True ? ConditionResult.True :
        left == ConditionResult.False && right == ConditionResult.False ? ConditionResult.False : ConditionResult.Unknown;

    private static void AddFrameworkSymbols(string tfm, HashSet<string> symbols, HashSet<string> reasons)
    {
        string normalized = tfm.Trim().ToLowerInvariant().Replace('.', '_');
        if (normalized.StartsWith("netstandard", StringComparison.Ordinal))
        {
            symbols.Add("NETSTANDARD");
            symbols.Add(normalized.ToUpperInvariant());
            return;
        }
        Match modern = Regex.Match(tfm, "^net(?<major>[5-9]|[1-9][0-9])(?:\\.(?<minor>[0-9]+))?$", RegexOptions.IgnoreCase);
        if (modern.Success)
        {
            int major = int.Parse(modern.Groups["major"].Value);
            symbols.Add("NET");
            symbols.Add($"NET{major}_0");
            for (int version = 5; version <= major; version++)
                symbols.Add($"NET{version}_0_OR_GREATER");
            return;
        }
        Match framework = Regex.Match(tfm, "^net(?<digits>[2-4][0-9]{1,2})$", RegexOptions.IgnoreCase);
        if (framework.Success)
        {
            string digits = framework.Groups["digits"].Value;
            symbols.Add("NETFRAMEWORK");
            symbols.Add("NET" + digits);
            symbols.Add("NET" + digits + "_OR_GREATER");
            return;
        }
        if (tfm.Length > 0) reasons.Add("unknown_tfm_symbols");
    }

    private static string? Child(XElement item, string name) =>
        item.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value;

    private static string NormalizeFromProject(string projectRelPath, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        if (Path.IsPathRooted(path)) return WorkspacePaths.ToGitPath(Path.GetFullPath(path));
        string dir = WorkspacePaths.ToGitPath(Path.GetDirectoryName(projectRelPath) ?? "");
        string combined = CombineGit(dir, path).Replace('\\', '/');
        var segments = new List<string>();
        foreach (string segment in combined.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".") continue;
            if (segment == "..")
            {
                if (segments.Count > 0 && segments[^1] != "..") segments.RemoveAt(segments.Count - 1);
                else segments.Add(segment);
                continue;
            }
            segments.Add(segment);
        }
        return WorkspacePaths.Normalize(string.Join('/', segments));
    }

    private static string CombineGit(string left, string right) =>
        WorkspacePaths.ToGitPath(Path.Combine(left.Replace('/', Path.DirectorySeparatorChar),
            right.Replace('/', Path.DirectorySeparatorChar))).TrimEnd('/');

    private static bool EndsWithSegment(string path, string segment) =>
        path.TrimEnd('/', '\\').EndsWith('/' + segment, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(path.TrimEnd('/', '\\'), segment, StringComparison.OrdinalIgnoreCase);

    private enum ConditionResult { False, True, Unknown }
}
