using System.Diagnostics;
using System.IO.Hashing;
using System.Text;
using CodeNav.Core.Discovery;

namespace CodeNav.Core.Indexing;

public sealed record RefreshResult(
    int ChangedFiles, int AddedFiles, int DeletedFiles, bool ProjectsRefreshed, TimeSpan Elapsed);

/// <summary>
/// Owns: incremental index updates — targeted (watcher batches) or detect-all
/// (mtime/size/hash sweep). Single-writer: callers must serialize invocations.
/// Does not own: watching (WorkspaceWatcher) or lifecycle/status (IndexManager).
/// </summary>
public static class DeltaRefresher
{
    public static RefreshResult Refresh(
        IndexStore store, string workspaceRoot, IReadOnlyCollection<string>? changedRelPaths,
        Action<string>? log = null)
    {
        var sw = Stopwatch.StartNew();
        var stored = store.AllFilesByPath();

        List<string> candidates;
        bool detectAll = changedRelPaths is null;
        ScanResult? scan = null;

        if (detectAll)
        {
            scan = WorkspaceScanner.Scan(workspaceRoot);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            candidates = new List<string>();
            foreach (var f in scan.CsFiles.Concat(scan.ProjectFiles).Concat(scan.SolutionFiles).Concat(scan.ConfigFiles))
            {
                seen.Add(f.RelPath);
                if (!stored.TryGetValue(f.RelPath, out _))
                {
                    candidates.Add(f.RelPath); // added
                }
                else
                {
                    candidates.Add(f.RelPath); // may be changed — hash check below decides
                }
            }
            foreach (var path in stored.Keys.Where(p => !seen.Contains(p)))
            {
                candidates.Add(path); // deleted
            }
        }
        else
        {
            candidates = changedRelPaths!.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        int added = 0, changed = 0, deleted = 0;
        bool projectDataDirty = false;

        using (var tx = store.BeginTransaction())
        {
            foreach (var rel in candidates)
            {
                // Reject caller-supplied paths (e.g. from refresh_index) that escape the
                // workspace root, so external files can never be read into the index.
                if (!WorkspacePaths.TryResolveInside(workspaceRoot, rel, out string full)) continue;
                bool exists = File.Exists(full);
                bool known = stored.TryGetValue(rel, out var old);
                string lang = LangOf(rel);
                if (lang == "other") continue;

                if (!exists)
                {
                    if (known)
                    {
                        store.DeleteFileCascade(tx, old!.Id, store.GetContentForWrite(old.Id));
                        deleted++;
                        if (lang is "csproj" or "sln") projectDataDirty = true;
                    }
                    continue;
                }

                byte[] bytes;
                FileInfo info;
                try
                {
                    info = new FileInfo(full);
                    bytes = File.ReadAllBytes(full);
                }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }

                ulong hash = XxHash64.HashToUInt64(bytes);
                if (known && unchecked((long)hash) == old!.Hash)
                {
                    continue; // touched but identical
                }

                string content = DecodeUtf8(bytes);
                if (lang == "cs")
                {
                    var parsed = SyntaxIndexer.Parse(rel, content);
                    if (known)
                    {
                        string oldContent = store.GetContentForWrite(old!.Id) ?? "";
                        store.UpdateFileRow(tx, old.Id, info.Length, info.LastWriteTimeUtc.Ticks, hash,
                            parsed.LineCount, parsed.LooksGenerated, parsed.HasTestAttributes);
                        store.ReplaceContent(tx, old.Id, oldContent, content);
                        store.DeleteSymbolsForFile(tx, old.Id);
                        store.InsertSymbols(tx, old.Id, parsed.Symbols);
                        changed++;
                    }
                    else
                    {
                        long id = store.InsertFile(tx, rel, info.Length, info.LastWriteTimeUtc.Ticks, hash,
                            "cs", parsed.LineCount, parsed.LooksGenerated, parsed.HasTestAttributes);
                        store.InsertContent(tx, id, content);
                        store.InsertSymbols(tx, id, parsed.Symbols);
                        added++;
                        projectDataDirty = true; // ownership needs recomputing (SDK glob or new legacy item)
                    }
                }
                else
                {
                    int lines = content.Count(c => c == '\n') + 1;
                    if (known)
                    {
                        string oldContent = store.GetContentForWrite(old!.Id) ?? "";
                        store.UpdateFileRow(tx, old.Id, info.Length, info.LastWriteTimeUtc.Ticks, hash, lines, false, false);
                        store.ReplaceContent(tx, old.Id, oldContent, content);
                        changed++;
                    }
                    else
                    {
                        long id = store.InsertFile(tx, rel, info.Length, info.LastWriteTimeUtc.Ticks, hash, lang, lines, false, false);
                        store.InsertContent(tx, id, content);
                        added++;
                    }
                    if (lang is "csproj" or "sln") projectDataDirty = true;
                }
            }
            tx.Commit();
        }

        if (projectDataDirty)
        {
            log?.Invoke("Project files changed — rebuilding project graph ...");
            RefreshProjectData(store, workspaceRoot, scan);
        }

        if (added + changed + deleted > 0)
        {
            store.SetMeta("last_refresh_utc", DateTime.UtcNow.ToString("O"));
        }

        return new RefreshResult(changed, added, deleted, projectDataDirty, sw.Elapsed);
    }

    /// <summary>Re-parses all csproj/sln files and rebuilds project tables + compile items.</summary>
    public static void RefreshProjectData(IndexStore store, string workspaceRoot, ScanResult? scan = null)
    {
        scan ??= WorkspaceScanner.Scan(workspaceRoot);

        var parsedProjects = new ParsedProject[scan.ProjectFiles.Count];
        Parallel.For(0, scan.ProjectFiles.Count, i =>
        {
            parsedProjects[i] = ProjectFileParser.Parse(workspaceRoot, scan.ProjectFiles[i].RelPath);
        });
        var parsedSolutions = scan.SolutionFiles
            .Select(s => SolutionParser.Parse(workspaceRoot, s.RelPath))
            .ToList();

        var csFileIds = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, path, lang) in store.FileIdPathLang())
        {
            if (lang == "cs") csFileIds[path] = id;
        }

        using var tx = store.BeginTransaction();
        store.ClearProjectData(tx);

        var projectIds = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in parsedProjects)
        {
            projectIds[p.RelPath] = store.InsertProject(tx, p);
        }
        foreach (var p in parsedProjects)
        {
            long fromId = projectIds[p.RelPath];
            foreach (var r in p.ProjectRefRelPaths)
            {
                if (projectIds.TryGetValue(r, out long toId)) store.InsertProjectRef(tx, fromId, toId);
            }
            foreach (var (pkg, version) in p.PackageRefs)
            {
                store.InsertPackageRef(tx, fromId, pkg, version);
            }
        }
        foreach (var s in parsedSolutions)
        {
            long slnId = store.InsertSolution(tx, s.RelPath, s.Name);
            foreach (var pr in s.ProjectRelPaths)
            {
                if (projectIds.TryGetValue(pr, out long pid)) store.InsertSolutionProject(tx, slnId, pid);
            }
        }
        CompileItemResolver.Write(store, tx, parsedProjects, projectIds, csFileIds);
        tx.Commit();
    }

    private static string LangOf(string relPath)
    {
        string ext = Path.GetExtension(relPath).ToLowerInvariant();
        string name = Path.GetFileName(relPath);
        return ext switch
        {
            ".cs" => "cs",
            ".csproj" => "csproj",
            ".sln" or ".slnx" or ".slnf" => "sln",
            ".config" or ".props" or ".targets" => "config",
            ".json" when name.StartsWith("appsettings", StringComparison.OrdinalIgnoreCase)
                        || name.Equals("global.json", StringComparison.OrdinalIgnoreCase) => "config",
            _ => name.Equals("packages.config", StringComparison.OrdinalIgnoreCase) ? "config" : "other",
        };
    }

    private static string DecodeUtf8(byte[] bytes)
    {
        string s = Encoding.UTF8.GetString(bytes);
        return s.Length > 0 && s[0] == (char)0xFEFF ? s[1..] : s;
    }
}
