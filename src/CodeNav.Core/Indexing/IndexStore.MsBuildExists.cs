using Microsoft.Data.Sqlite;

namespace CodeNav.Core.Indexing;

public sealed partial class IndexStore
{
    internal void ClearMsBuildExistsInputs(SqliteTransaction tx) =>
        ExecTx(tx, "DELETE FROM msbuild_exists_inputs");

    internal List<(long Id, string Path, string Tfms, string Xml)> MsBuildExistsProjects(
        SqliteTransaction tx, HashSet<long>? owners)
    {
        var sources = new List<(long, string, string, string)>();
        void Read(long? owner)
        {
            using var cmd = _write.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                SELECT f.id, f.path, p.tfms, c.content
                FROM projects p JOIN files f ON f.path=p.path
                JOIN file_contents c ON c.file_id=f.id
                WHERE p.lang='fs'
                """ + (owner.HasValue ? " AND f.id=$id" : "");
            if (owner.HasValue) cmd.Parameters.AddWithValue("$id", owner.Value);
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) sources.Add((reader.GetInt64(0), reader.GetString(1),
                reader.GetString(2), reader.GetString(3)));
        }
        if (owners is null) Read(null);
        else foreach (long owner in owners) Read(owner);
        return sources;
    }

    internal List<long> MsBuildExistsOwners(SqliteTransaction tx, string path)
    {
        using var cmd = _write.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT source_file_id FROM msbuild_exists_inputs WHERE path=$p";
        cmd.Parameters.AddWithValue("$p", path);
        using var reader = cmd.ExecuteReader();
        var owners = new List<long>();
        while (reader.Read()) owners.Add(reader.GetInt64(0));
        return owners;
    }

    internal bool ReplaceMsBuildExistsPaths(SqliteTransaction tx, long sourceId,
        HashSet<string> paths)
    {
        using var cmd = _write.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT path FROM msbuild_exists_inputs WHERE source_file_id=$id";
        cmd.Parameters.AddWithValue("$id", sourceId);
        var previous = new HashSet<string>(StringComparer.Ordinal);
        using (var reader = cmd.ExecuteReader())
            while (reader.Read()) previous.Add(reader.GetString(0));
        if (previous.SetEquals(paths)) return false;
        ExecTx(tx, "DELETE FROM msbuild_exists_inputs WHERE source_file_id=$id", ("$id", sourceId));
        foreach (string path in paths)
            ExecTx(tx, "INSERT INTO msbuild_exists_inputs(source_file_id, path) VALUES($id, $p)",
                ("$id", sourceId), ("$p", path));
        return true;
    }

    internal List<string> MsBuildExistsPaths(SqliteTransaction tx)
    {
        using var cmd = _write.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT DISTINCT path FROM msbuild_exists_inputs";
        using var reader = cmd.ExecuteReader();
        var paths = new List<string>();
        while (reader.Read()) paths.Add(reader.GetString(0));
        return paths;
    }

    internal bool UpdateMsBuildExistsPresence(SqliteTransaction tx, string path, bool? presence)
    {
        using var cmd = _write.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE msbuild_exists_inputs SET presence=$state
            WHERE path=$p AND presence IS NOT $state
            """;
        cmd.Parameters.AddWithValue("$p", path);
        cmd.Parameters.AddWithValue("$state", presence.HasValue ? (object)(presence.Value ? 1 : 0) : DBNull.Value);
        return cmd.ExecuteNonQuery() > 0;
    }
}
