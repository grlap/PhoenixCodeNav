using System.Xml.Linq;
using static CodeNav.Core.Discovery.ProjectFileParser;

namespace CodeNav.Core.Discovery;

/// <summary>Shared raw project inputs for Roslyn and FCS. Imports and build conditions are not executed.</summary>
public static partial class SimpleProjectModelBuilder
{
    public static ParsedProject Build(string relPath, byte[] projectBytes,
        byte[]? packagesConfigBytes = null)
    {
        string name = Path.GetFileNameWithoutExtension(relPath);
        try
        {
            XDocument project = LoadSnapshotXml(projectBytes);
            XDocument? packages = null;
            if (packagesConfigBytes is not null)
            {
                try { packages = LoadSnapshotXml(packagesConfigBytes); }
                catch { /* packages.config remains optional/partial */ }
            }
            return ProjectFileParser.ParseDocuments(relPath, project, packages);
        }
        catch (Exception ex)
        {
            string language = ProjectLanguage(relPath);
            return new ParsedProject(relPath, name, "unknown", null, "",
                LooksLikeTestName(name), [], [], null, [], $"failed:{ex.GetType().Name}",
                DefaultCompileItems: language == "cs",
                CompileOwnershipComplete: language == "cs",
                Language: language);
        }
    }


    internal static XDocument ReadProject(string xml) =>
        LoadSnapshotXml(System.Text.Encoding.UTF8.GetBytes(xml));
}
