using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace CodeNav.Core.Discovery;

public sealed record ParsedSolution(string RelPath, string Name, List<string> ProjectRelPaths);

/// <summary>
/// Owns: extracting project membership from .sln (classic), .slnx (xml), and .slnf (filter) files.
/// Does not own: csproj parsing or discovery.
/// </summary>
public static partial class SolutionParser
{
    private const int MaxSnapshotBytes = 16 * 1024 * 1024;

    [GeneratedRegex("""Project\("\{[0-9A-Fa-f-]+\}"\)\s*=\s*"[^"]*",\s*"([^"]+)"\s*,""", RegexOptions.Compiled)]
    private static partial Regex SlnProjectLine();

    public static ParsedSolution Parse(string workspaceRoot, string relPath)
    {
        string full = Path.Combine(workspaceRoot, relPath.Replace('/', Path.DirectorySeparatorChar));
        try { return ParseSnapshot(relPath, File.ReadAllBytes(full)); }
        catch
        {
            return new ParsedSolution(relPath, Path.GetFileNameWithoutExtension(relPath), []);
        }
    }

    /// <summary>Parses solution membership from the exact bounded, no-follow byte snapshot
    /// captured by the indexer, rather than reopening a scanner path at a later epoch.</summary>
    public static ParsedSolution ParseSnapshot(string relPath, byte[] bytes)
    {
        string name = Path.GetFileNameWithoutExtension(relPath);
        string slnDir = WorkspacePaths.ToGitPath(Path.GetDirectoryName(relPath) ?? "");
        var projects = new List<string>();

        try
        {
            if (bytes.Length > MaxSnapshotBytes)
                throw new InvalidDataException("solution exceeds the snapshot limit");
            string ext = Path.GetExtension(relPath);
            if (ext.Equals(".slnf", StringComparison.OrdinalIgnoreCase))
            {
                using var json = JsonDocument.Parse(DecodeSnapshotText(bytes),
                    new JsonDocumentOptions { MaxDepth = 64 });
                var solution = json.RootElement.GetProperty("solution");
                string slnPath = solution.GetProperty("path").GetString() ?? "";
                string baseDir = ProjectFileParser.NormalizeRelative(slnDir,
                    PortableDirectoryName(slnPath));
                foreach (var p in solution.GetProperty("projects").EnumerateArray())
                {
                    string? proj = p.GetString();
                    if (proj is not null)
                    {
                        projects.Add(ProjectFileParser.NormalizeRelative(baseDir, proj));
                    }
                }
            }
            else if (ext.Equals(".slnx", StringComparison.OrdinalIgnoreCase))
            {
                using var stream = new MemoryStream(bytes, writable: false);
                using XmlReader reader = XmlReader.Create(stream, new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null,
                    MaxCharactersInDocument = MaxSnapshotBytes,
                });
                XDocument doc = XDocument.Load(reader, LoadOptions.None);
                foreach (var e in doc.Descendants().Where(e => e.Name.LocalName == "Project"))
                {
                    string? path = e.Attribute("Path")?.Value;
                    if (path is not null) projects.Add(ProjectFileParser.NormalizeRelative(slnDir, path));
                }
            }
            else
            {
                using var stream = new MemoryStream(bytes, writable: false);
                using var lines = new StreamReader(stream, Encoding.UTF8,
                    detectEncodingFromByteOrderMarks: true);
                while (lines.ReadLine() is { } line)
                {
                    var m = SlnProjectLine().Match(line);
                    if (m.Success)
                    {
                        string path = m.Groups[1].Value;
                        // Only file-backed projects (skip solution folders, websites).
                        if (path.EndsWith("proj", StringComparison.OrdinalIgnoreCase))
                        {
                            projects.Add(ProjectFileParser.NormalizeRelative(slnDir, path));
                        }
                    }
                }
            }
        }
        catch
        {
            // Unreadable solutions yield an empty membership; discovery still records the file.
        }

        return new ParsedSolution(relPath, name, projects.Distinct().ToList());
    }

    private static string DecodeSnapshotText(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new StreamReader(stream, Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    /// <summary>Directory portion of a solution-format path. Solution files use portable
    /// slash/backslash separators independent of the host OS.</summary>
    internal static string PortableDirectoryName(string path)
    {
        string portable = path.Replace('\\', '/');
        int slash = portable.LastIndexOf('/');
        return slash < 0 ? "" : portable[..slash];
    }
}
