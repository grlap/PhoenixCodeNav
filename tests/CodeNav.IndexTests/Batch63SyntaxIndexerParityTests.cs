using System.Text;
using CodeNav.Core.Indexing;
using Microsoft.Data.Sqlite;

namespace CodeNav.Tests;

public sealed class Batch63SyntaxIndexerParityTests
{
    [Fact]
    public void DeepMixedDeltaRowsMatchFreshBuild()
    {
        string root = Directory.CreateTempSubdirectory("codenav-63-syntax-parity").FullName;
        try
        {
            string projectDir = Path.Combine(root, "P");
            Directory.CreateDirectory(projectDir);
            File.WriteAllText(Path.Combine(projectDir, "P.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
                </Project>
                """);
            string sourcePath = Path.Combine(projectDir, "Deep.cs");
            File.WriteAllText(sourcePath, DeepSource(includeInsertedMember: false));

            string deltaDb = IndexBuilder.DefaultDbPath(root);
            IndexBuilder.Build(root, deltaDb);

            File.WriteAllText(sourcePath, DeepSource(includeInsertedMember: true));
            using (var store = new IndexStore(deltaDb, createNew: false))
            {
                RefreshResult refreshed = DeltaRefresher.Refresh(store, root, ["P/Deep.cs"]);
                Assert.Equal(1, refreshed.ChangedFiles);
                Assert.Equal(0, refreshed.AddedFiles);
                Assert.Equal(0, refreshed.DeletedFiles);
            }
            string[] deltaRows = DumpRows(deltaDb, "P/Deep.cs");

            string fullDb = Path.Combine(root, ".codenav", "full-rebuild.db");
            IndexBuilder.Build(root, fullDb);
            string[] fullRows = DumpRows(fullDb, "P/Deep.cs");

            Assert.Equal(fullRows, deltaRows);
            Assert.Contains(deltaRows, row => row.Contains("method\u001fInsertedMidChain\u001f",
                StringComparison.Ordinal));
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    private static string DeepSource(bool includeInsertedMember)
    {
        const int depth = 50;
        var source = new StringBuilder("namespace DeepParity;\n\n");
        for (int i = 0; i < depth; i++)
        {
            source.Append(' ', i * 2)
                .Append("public class Level").Append(i).Append("<T>")
                .AppendLine()
                .Append(' ', i * 2).AppendLine("{");
        }

        source.Append(' ', depth * 2).AppendLine("int first, second;");
        source.Append(' ', depth * 2).AppendLine("enum State");
        source.Append(' ', depth * 2).AppendLine("{");
        source.Append(' ', depth * 2 + 2).AppendLine("None,");
        source.Append(' ', depth * 2 + 2).AppendLine("Ready");
        source.Append(' ', depth * 2).AppendLine("}");

        for (int i = depth - 1; i >= 0; i--)
        {
            if (includeInsertedMember && i == depth / 2)
            {
                source.Append(' ', (i + 1) * 2)
                    .AppendLine("void InsertedMidChain() { }");
            }
            source.Append(' ', i * 2).AppendLine("}");
        }
        return source.ToString();
    }

    private static string[] DumpRows(string dbPath, string filePath)
    {
        IndexQueries.ClearPoolsFor(dbPath);
        using var connection = new SqliteConnection(
            IndexQueries.ReadConnectionString(dbPath, pinReadSnapshot: false, pooling: false));
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT s.kind, s.name, COALESCE(s.ns, ''), COALESCE(s.container, ''),
                   s.signature, s.accessibility, s.start_line, s.end_line,
                   s.is_partial, s.arity, COALESCE(s.attr_markers, ''),
                   COALESCE(s.modifiers, ''), COALESCE(s.accessors, ''),
                   s.declaration_key, COALESCE(p.kind, ''), COALESCE(p.name, '')
            FROM symbols s
            JOIN files f ON f.id = s.file_id
            LEFT JOIN symbols p ON p.id = s.parent_id
            WHERE f.path = $path
            ORDER BY s.id
            """;
        command.Parameters.AddWithValue("$path", filePath);

        var rows = new List<string>();
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(string.Join('\u001f', Enumerable.Range(0, reader.FieldCount)
                .Select(index => Convert.ToString(reader.GetValue(index),
                    System.Globalization.CultureInfo.InvariantCulture) ?? "")));
        }
        return rows.ToArray();
    }
}
