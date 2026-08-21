using System.Runtime.InteropServices;

namespace CodeNav.Core.Indexing;

/// <summary>
/// Linux open(2) flag values are part of the architecture ABI. The asm-generic defaults match
/// x86/x64, while ARM, ARM64, and PowerPC override O_DIRECTORY/O_NOFOLLOW with the opposite
/// pair. Keep every relative no-follow open on one exhaustive, fail-closed mapping.
/// </summary>
internal static class LinuxOpenFlags
{
    internal const int ReadWrite = 0x0002;
    internal const int Create = 0x0040;
    internal const int Exclusive = 0x0080;
    internal const int NonBlocking = 0x0800;
    internal const int CloseOnExec = 0x080000;
    internal const int Path = 0x200000;

    internal static int Directory =>
        DirectoryForArchitecture(RuntimeInformation.ProcessArchitecture);

    internal static int NoFollow =>
        NoFollowForArchitecture(RuntimeInformation.ProcessArchitecture);

    internal static int DirectoryOpen =>
        DirectoryOpenForArchitecture(RuntimeInformation.ProcessArchitecture);

    internal static int PathInspect =>
        PathInspectForArchitecture(RuntimeInformation.ProcessArchitecture);

    internal static int ExclusiveCreateReadWrite =>
        ExclusiveCreateReadWriteForArchitecture(RuntimeInformation.ProcessArchitecture);

    internal static int ReadNonBlocking => NonBlocking | CloseOnExec;

    internal static int DirectoryForArchitecture(Architecture architecture) =>
        architecture switch
        {
            Architecture.X86 or
            Architecture.X64 or
            Architecture.S390x or
            Architecture.LoongArch64 or
            Architecture.RiscV64 => 0x010000,
            Architecture.Arm or
            Architecture.Armv6 or
            Architecture.Arm64 or
            Architecture.Ppc64le => 0x004000,
            _ => throw UnsupportedArchitecture(architecture)
        };

    internal static int NoFollowForArchitecture(Architecture architecture) =>
        architecture switch
        {
            Architecture.X86 or
            Architecture.X64 or
            Architecture.S390x or
            Architecture.LoongArch64 or
            Architecture.RiscV64 => 0x020000,
            Architecture.Arm or
            Architecture.Armv6 or
            Architecture.Arm64 or
            Architecture.Ppc64le => 0x008000,
            _ => throw UnsupportedArchitecture(architecture)
        };

    internal static int DirectoryOpenForArchitecture(Architecture architecture) =>
        DirectoryForArchitecture(architecture) |
        NoFollowForArchitecture(architecture) |
        CloseOnExec;

    internal static int PathInspectForArchitecture(Architecture architecture) =>
        Path |
        NoFollowForArchitecture(architecture) |
        CloseOnExec;

    internal static int ExclusiveCreateReadWriteForArchitecture(Architecture architecture) =>
        ReadWrite |
        Create |
        Exclusive |
        NoFollowForArchitecture(architecture) |
        CloseOnExec;

    private static PlatformNotSupportedException UnsupportedArchitecture(
        Architecture architecture) =>
        new($"Linux open(2) flag mapping is not defined for {architecture}.");
}
