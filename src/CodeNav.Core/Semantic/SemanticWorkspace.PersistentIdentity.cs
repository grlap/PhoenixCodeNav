using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;

namespace CodeNav.Core.Semantic;

public sealed partial class SemanticWorkspace
{
    // Changing the identity algorithm must change this namespace so incompatible keys never
    // alias an older cache. Roslyn still validates source checksums and parse options per entry.
    private const string PersistentIdentityNamespace = "PhoenixCodeNav.Semantic.v1";
    private const string PersistentSolutionName = "phoenix-semantic-workspace.sln";

    private static SolutionId StableSolutionId(string workspaceRoot)
    {
        string root = CanonicalIdentityPath(workspaceRoot);
        return SolutionId.CreateFromSerialized(
            StableGuid("solution", root),
            debugName: PersistentSolutionName);
    }

    private ProjectId StableProjectId(string projectName)
        => ProjectId.CreateFromSerialized(
            StableGuid("project", CanonicalIdentityPath(_workspaceRoot),
                projectName.ToUpperInvariant()),
            debugName: projectName);

    private static DocumentId StableDocumentId(ProjectId projectId, string documentIdentity)
        => DocumentId.CreateFromSerialized(
            projectId,
            StableGuid("document", projectId.Id.ToString("N"),
                NormalizeIdentityComponent(documentIdentity)),
            debugName: documentIdentity);

    private static string PersistentSolutionIdentityPath(string workspaceRoot)
        => Path.Combine(CanonicalIdentityPath(workspaceRoot), ".codenav",
            PersistentSolutionName);

    private static string CanonicalIdentityPath(string path)
    {
        string canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path))
            .Replace(Path.DirectorySeparatorChar, '/');
        if (Path.AltDirectorySeparatorChar != Path.DirectorySeparatorChar)
            canonical = canonical.Replace(Path.AltDirectorySeparatorChar, '/');
        return OperatingSystem.IsWindows() ? canonical.ToUpperInvariant() : canonical;
    }

    private static string NormalizeIdentityComponent(string value)
        => value.Replace('\\', '/');

    private static Guid StableGuid(string kind, params string[] components)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Append(PersistentIdentityNamespace);
        Append(kind);
        foreach (string component in components) Append(component);

        Span<byte> digest = stackalloc byte[32];
        if (!hash.TryGetHashAndReset(digest, out int written) || written != digest.Length)
            throw new InvalidOperationException("Could not create a stable semantic identity.");

        Span<byte> guidBytes = digest[..16];
        // RFC 4122 variant with a version-5 marker. SHA-256, rather than SHA-1, supplies the bits.
        guidBytes[6] = (byte)((guidBytes[6] & 0x0f) | 0x50);
        guidBytes[8] = (byte)((guidBytes[8] & 0x3f) | 0x80);
        return new Guid(guidBytes);

        void Append(string value)
        {
            int byteCount = Encoding.UTF8.GetByteCount(value);
            Span<byte> length = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(length, byteCount);
            hash.AppendData(length);
            if (byteCount == 0) return;
            byte[] bytes = Encoding.UTF8.GetBytes(value);
            hash.AppendData(bytes);
        }
    }

    internal Solution TestOnlyCurrentSolution => _workspace.CurrentSolution;

    internal ProjectId TestOnlyStableProjectId(string projectName)
        => StableProjectId(projectName);

    internal static DocumentId TestOnlyStableDocumentId(ProjectId projectId,
        string documentIdentity)
        => StableDocumentId(projectId, documentIdentity);
}
