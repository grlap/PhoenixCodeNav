using CodeNav.Core.Indexing;
using System.Runtime.InteropServices;

namespace CodeNav.Tests;

public sealed class LinuxOpenFlagsTests
{
    [Theory]
    [InlineData(Architecture.X86, 0x010000, 0x020000, 0x0B0000, 0x2A0000, 0x0A00C2)]
    [InlineData(Architecture.X64, 0x010000, 0x020000, 0x0B0000, 0x2A0000, 0x0A00C2)]
    [InlineData(Architecture.S390x, 0x010000, 0x020000, 0x0B0000, 0x2A0000, 0x0A00C2)]
    [InlineData(Architecture.LoongArch64, 0x010000, 0x020000, 0x0B0000, 0x2A0000, 0x0A00C2)]
    [InlineData(Architecture.RiscV64, 0x010000, 0x020000, 0x0B0000, 0x2A0000, 0x0A00C2)]
    [InlineData(Architecture.Arm, 0x004000, 0x008000, 0x08C000, 0x288000, 0x0880C2)]
    [InlineData(Architecture.Armv6, 0x004000, 0x008000, 0x08C000, 0x288000, 0x0880C2)]
    [InlineData(Architecture.Arm64, 0x004000, 0x008000, 0x08C000, 0x288000, 0x0880C2)]
    [InlineData(Architecture.Ppc64le, 0x004000, 0x008000, 0x08C000, 0x288000, 0x0880C2)]
    public void OpenFlagsMatchTheArchitectureAbi(
        Architecture architecture,
        int expectedDirectory,
        int expectedNoFollow,
        int expectedDirectoryOpen,
        int expectedPathInspect,
        int expectedExclusiveCreateReadWrite)
    {
        Assert.Equal(expectedDirectory,
            LinuxOpenFlags.DirectoryForArchitecture(architecture));
        Assert.Equal(expectedNoFollow,
            LinuxOpenFlags.NoFollowForArchitecture(architecture));
        Assert.Equal(expectedDirectoryOpen,
            LinuxOpenFlags.DirectoryOpenForArchitecture(architecture));
        Assert.Equal(expectedPathInspect,
            LinuxOpenFlags.PathInspectForArchitecture(architecture));
        Assert.Equal(expectedExclusiveCreateReadWrite,
            LinuxOpenFlags.ExclusiveCreateReadWriteForArchitecture(architecture));
        Assert.Equal(0x080800, LinuxOpenFlags.ReadNonBlocking);
    }

    [Theory]
    [InlineData(Architecture.Wasm)]
    [InlineData((Architecture)int.MaxValue)]
    public void UnknownLinuxArchitectureFailsClosed(Architecture architecture)
    {
        Assert.Throws<PlatformNotSupportedException>(
            () => LinuxOpenFlags.DirectoryForArchitecture(architecture));
        Assert.Throws<PlatformNotSupportedException>(
            () => LinuxOpenFlags.NoFollowForArchitecture(architecture));
    }
}
