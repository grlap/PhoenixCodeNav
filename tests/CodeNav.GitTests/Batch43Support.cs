using CodeNav.Core.Indexing;
using System.Runtime.InteropServices;
using System.Text;

namespace CodeNav.Tests;

/// <summary>
/// Owns: the shared Batch 43 helpers — raw diff/batch-blob byte builders, bounded/tracking
/// test streams, repo creation and git shell wrappers, junction + fifo primitives — consumed
/// via 'using static' by the two Batch43 test slices.
/// Deliberately does not own: any tests.
/// Split out of: Batch43GitReviewSafetyTests.cs (PhoenixCodeNav-6zdy); bodies moved verbatim,
/// visibility private -> internal at class-member level only (nested type members untouched).
/// </summary>
internal static class Batch43Support
{
    internal const string OldOid = "1111111111111111111111111111111111111111";
    internal const string NewOid = "2222222222222222222222222222222222222222";

    internal sealed record RawEntry(string OldMode, string NewMode, char Status, string Path);

    internal static byte[] DiffOutput(RawEntry entry, string patch) =>
        DiffOutput(new[] { entry }, patch);

    internal static byte[] DiffOutput(IEnumerable<RawEntry> entries, string patch)
    {
        using var output = new MemoryStream();
        foreach (RawEntry entry in entries)
        {
            byte[] record = DiffOutput(entry.OldMode, entry.NewMode, entry.Status,
                Utf8(entry.Path), patch: []);
            output.Write(record, 0, record.Length - 1); // Keep each path terminator; omit separator.
        }
        output.WriteByte(0); // Empty raw record separates the manifest from the patch.
        byte[] patchBytes = Utf8(patch);
        output.Write(patchBytes);
        return output.ToArray();
    }

    internal static byte[] DiffOutput(
        string oldMode, string newMode, char status, byte[] path, byte[] patch)
    {
        using var output = new MemoryStream();
        byte[] header = Ascii($":{oldMode} {newMode} {OldOid} {NewOid} {status}");
        output.Write(header);
        output.WriteByte(0);
        output.Write(path);
        output.WriteByte(0);
        output.WriteByte(0);
        output.Write(patch);
        return output.ToArray();
    }

    internal static byte[] BatchBlob(string oid, byte[] content)
    {
        using var output = new MemoryStream();
        output.Write(Ascii($"{oid} blob {content.Length}\n"));
        output.Write(content);
        output.WriteByte((byte)'\n');
        return output.ToArray();
    }

    internal static byte[] Ascii(string value) => Encoding.ASCII.GetBytes(value);
    internal static byte[] Utf8(string value) => Encoding.UTF8.GetBytes(value);
    internal static string GitQuote(string value) =>
        "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    internal static int FindSequence(byte[] haystack, byte[] needle)
    {
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle)) return i;
        }
        return -1;
    }

    internal sealed class RepeatedThenTailStream(
        byte[] repeated, int repetitions, byte[] tail) : Stream
    {
        private long _position;
        private readonly long _repeatedLength = (long)repeated.Length * repetitions;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _repeatedLength + tail.Length;
        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadCore(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer) => ReadCore(buffer);

        private int ReadCore(Span<byte> destination)
        {
            int written = 0;
            while (written < destination.Length && _position < Length)
            {
                if (_position < _repeatedLength)
                {
                    int sourceOffset = (int)(_position % repeated.Length);
                    int take = Math.Min(destination.Length - written,
                        repeated.Length - sourceOffset);
                    repeated.AsSpan(sourceOffset, take).CopyTo(destination[written..]);
                    written += take;
                    _position += take;
                }
                else
                {
                    int sourceOffset = (int)(_position - _repeatedLength);
                    int take = Math.Min(destination.Length - written, tail.Length - sourceOffset);
                    tail.AsSpan(sourceOffset, take).CopyTo(destination[written..]);
                    written += take;
                    _position += take;
                }
            }
            return written;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    internal sealed class CountingWriteStream : Stream
    {
        public long BytesWritten { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => BytesWritten;
        public override long Position
        {
            get => BytesWritten;
            set => throw new NotSupportedException();
        }
        public override void Flush() { }
        public override void Write(byte[] buffer, int offset, int count) => BytesWritten += count;
        public override void Write(ReadOnlySpan<byte> buffer) => BytesWritten += buffer.Length;
        public override void WriteByte(byte value) => BytesWritten++;
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    internal sealed class TrackingReadStream(byte[] bytes, int maxChunk) : Stream
    {
        public int BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => bytes.Length;
        public override long Position
        {
            get => BytesRead;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadCore(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer) => ReadCore(buffer);

        public override Task<int> ReadAsync(
            byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            Task.FromResult(ReadCore(buffer.AsSpan(offset, count)));

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ReadCore(buffer.Span));

        private int ReadCore(Span<byte> destination)
        {
            int count = Math.Min(Math.Min(destination.Length, maxChunk), bytes.Length - BytesRead);
            if (count <= 0) return 0;
            bytes.AsSpan(BytesRead, count).CopyTo(destination);
            BytesRead += count;
            return count;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    internal static string CreateRepo(string root, string gitExe, string? attributes = null)
    {
        File.WriteAllText(Path.Combine(root, "Source.cs"),
            "namespace Demo\n{\n    class Source\n    {\n        int Value() => 1;\n    }\n}\n");
        if (attributes is not null) File.WriteAllText(Path.Combine(root, ".gitattributes"), attributes);
        InitRepo(root, gitExe);
        Git(root, gitExe, "add -A");
        Git(root, gitExe, "commit -q -m initial");
        return GitOutput(root, gitExe, "rev-parse HEAD").Trim();
    }

    internal static void InitRepo(string root, string gitExe)
    {
        Git(root, gitExe, "init -q -b main");
        Git(root, gitExe, "config user.email test@example.com");
        Git(root, gitExe, "config user.name CodeNavTest");
        Git(root, gitExe, "config commit.gpgsign false");
    }

    internal static void EditSource(string root)
    {
        string path = Path.Combine(root, "Source.cs");
        File.WriteAllText(path, File.ReadAllText(path).Replace("Value() => 1", "Value() => 2"));
    }

    internal static string? FindRealGitExe()
    {
        if (!OperatingSystem.IsWindows()) return GitInfo.ResolveGitExeFrom(null,
            Environment.GetEnvironmentVariable("PATH"));
        foreach (string entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(
                     Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                string candidate = Path.Combine(entry.Trim('"'), "git.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* malformed PATH entry */ }
        }
        return null;
    }

    internal static void Git(string root, string gitExe, string args)
    {
        string? output = GitInfo.RunProcess(gitExe, root,
            "-c core.fsmonitor=false -c core.useBuiltinFSMonitor=false " + args, waitMs: 20000);
        Assert.NotNull(output);
    }

    internal static string GitOutput(string root, string gitExe, string args)
    {
        string? output = GitInfo.RunProcess(gitExe, root,
            "-c core.fsmonitor=false -c core.useBuiltinFSMonitor=false " + args, waitMs: 20000);
        Assert.NotNull(output);
        return output!;
    }

    internal static void Cleanup(string root)
    {
        try { Directory.Delete(root, recursive: true); } catch { /* Windows process handles */ }
    }

    internal static void CreateDirectoryLink(string link, string target, string workingDirectory)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(link, target);
            return;
        }
        string cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        var junction = GitInfo.RunProcessEx(cmd, workingDirectory,
            $"/d /c mklink /J \"{link}\" \"{target}\"", waitMs: 5_000);
        Assert.Equal("ok", junction.Status);
    }

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    internal static extern int MakeFifo(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);
}
