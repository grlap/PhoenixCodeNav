using System.Text.Json;
using System.Text.RegularExpressions;

namespace CodeNav.Core.Discovery;

public sealed record ParsedSolution(string RelPath, string Name, List<string> ProjectRelPaths);

/// <summary>
/// Owns: extracting project membership from .sln (classic), .slnx (xml), and .slnf (filter) files.
/// Does not own: csproj parsing or discovery.
/// </summary>
public static partial class SolutionParser
{
    [GeneratedRegex("""Project\("\{[0-9A-Fa-f-]+\}"\)\s*=\s*"[^"]*",\s*"([^"]+)"\s*,""", RegexOptions.Compiled)]
    private static partial Regex SlnProjectLine();

    public static ParsedSolution Parse(string workspaceRoot, string relPath)
    {
        string full = Path.Combine(workspaceRoot, relPath.Replace('/', Path.DirectorySeparatorChar));
        string name = Path.GetFileNameWithoutExtension(relPath);
        string slnDir = Path.GetDirectoryName(relPath)?.Replace('\\', '/') ?? "";
        var projects = new List<string>();

        try
        {
            string ext = Path.GetExtension(relPath);
            if (ext.Equals(".slnf", StringComparison.OrdinalIgnoreCase))
            {
                using var json = JsonDocument.Parse(File.ReadAllText(full));
                var solution = json.RootElement.GetProperty("solution");
                string slnPath = solution.GetProperty("path").GetString() ?? "";
                string baseDir = ProjectFileParser.NormalizeRelative(slnDir, Path.GetDirectoryName(slnPath)?.Replace('\\', '/') ?? "");
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
                var doc = System.Xml.Linq.XDocument.Load(full);
                foreach (var e in doc.Descendants().Where(e => e.Name.LocalName == "Project"))
                {
                    string? path = e.Attribute("Path")?.Value;
                    if (path is not null) projects.Add(ProjectFileParser.NormalizeRelative(slnDir, path));
                }
            }
            else
            {
                foreach (var line in File.ReadLines(full))
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
}
