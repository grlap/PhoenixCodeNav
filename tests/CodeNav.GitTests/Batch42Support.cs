using System.Text.Json;
using CodeNav.Core.Indexing;

namespace CodeNav.Tests;

/// <summary>
/// Owns: the shared Batch 42 fixture + helpers (review repo incl. the k6mf line-ending pin,
/// manager startup, git shell, polling, cleanup) consumed via 'using static' by the three
/// Batch42 test slices.
/// Deliberately does not own: any tests.
/// Split out of: Batch42Tests.cs (PhoenixCodeNav-6zdy); bodies moved verbatim, visibility
/// private -> internal (required by the move).
/// </summary>
internal static class Batch42Support
{
    internal static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    // ---------------------------------------------------------------- fixture + helpers

    /// <summary>Lib (Widget.DoWork with a replaceable body marker + Old.cs OldThing) and
    /// Consumer (ProjectReference -> Lib; names Widget/DoWork/OldThing so reference candidates
    /// and the dependent split have material). Committed on branch 'main'.</summary>
    internal static void WriteReviewRepo(string root)
    {
        string lib = Path.Combine(root, "Lib");
        Directory.CreateDirectory(lib);
        File.WriteAllText(Path.Combine(lib, "Lib.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(lib, "Widget.cs"),
            """
            namespace Lib
            {
                public class Widget
                {
                    public void DoWork()
                    {
                        // body-marker
                    }
                }
            }
            """);
        File.WriteAllText(Path.Combine(lib, "Old.cs"),
            "namespace Lib { public class OldThing { public void Legacy() { } } }");
        string consumer = Path.Combine(root, "Consumer");
        Directory.CreateDirectory(consumer);
        File.WriteAllText(Path.Combine(consumer, "Consumer.csproj"),
            """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net9.0</TargetFramework></PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="../Lib/Lib.csproj" />
              </ItemGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(consumer, "Use.cs"),
            """
            namespace Consumer
            {
                public class Use
                {
                    public void Run()
                    {
                        var w = new Lib.Widget();
                        w.DoWork();
                        var o = new Lib.OldThing();
                        o.Legacy();
                    }
                }
            }
            """);
        File.WriteAllText(Path.Combine(root, ".gitignore"), ".codenav/\n");
        Git(root, "init -q -b main");
        Git(root, "config user.email test@example.com");
        Git(root, "config user.name CodeNavTest");
        Git(root, "config commit.gpgsign false");
        // k6mf: pin line-ending behaviour BEFORE add so blob bytes == on-disk bytes regardless
        // of the host's system/global core.autocrlf. Fixture content inherits this source file's
        // checkout endings (raw string literals), and the exact-move correlator compares RAW
        // BYTES (base blob vs untracked file): under system autocrlf=true the add-time LF
        // normalization made every unstaged move "normalization-only" — honestly uncorrelated —
        // and movedFiles vanished. Machine-dependent red, exposed by a CRLF re-checkout.
        Git(root, "config core.autocrlf false");
        Git(root, "add -A");
        Git(root, "commit -q -m init");
    }

    internal static IndexManager StartManager(string root)
    {
        string dbPath = IndexBuilder.DefaultDbPath(root);
        IndexBuilder.Build(root, dbPath);
        var m = new IndexManager(root, dbPath);
        m.Start();
        Assert.True(WaitUntil(() => m.IsQueryable && m.Health().IndexedCommit is not null, 30000),
            "manager did not become queryable with a git baseline");
        return m;
    }

    /// <summary>Push one file through the pump and wait until its symbol state is visible —
    /// review_pack maps hunk ranges against INDEX spans, so the index must reflect the edit.</summary>
    internal static void RefreshAndWait(IndexManager m, string relPath, string symbolName)
    {
        m.RequestRefresh(new[] { relPath });
        Assert.True(WaitUntil(() =>
        {
            using var q = m.OpenQueries();
            return q.SearchSymbols(symbolName, "exact", null, 2).Count > 0;
        }, 20000), $"index did not reflect {relPath}");
    }

    internal static void Git(string dir, string args) => TestGit.Run(dir, args); // n7ly: loud + retried

    internal static string GitOutput(string dir, string args)
    {
        string? output = GitInfo.RunProcess("git", dir,
            "-c core.fsmonitor=false -c core.useBuiltinFSMonitor=false " + args,
            waitMs: 20000);
        Assert.NotNull(output);
        return output!;
    }

    internal static bool WaitUntil(Func<bool> cond, int timeoutMs)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (cond()) return true;
            Thread.Sleep(50);
        }
        return cond();
    }

    internal static void Cleanup(string root)
    {
        TestWorkspaceCleanup.ClearIndexPools(root);
        try { Directory.Delete(root, recursive: true); } catch { /* windows locks */ }
    }
}
