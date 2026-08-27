using System.Runtime.InteropServices;
using System.Text;
using CodeNav.Core.Indexing;
using CodeNav.Portal;

namespace CodeNav.Tests;

public sealed class PortalPathGuardTests
{
    [Fact]
    public void UnixDirectoryEntryNameStopsAtTheFirstBoundedNul()
    {
        byte[] name = Encoding.UTF8.GetBytes("phoenix-arm64.jsonl");
        byte[] record = [.. name, 0, (byte)'.', 0, 0xff];
        IntPtr buffer = Marshal.AllocHGlobal(record.Length);
        try
        {
            Marshal.Copy(record, 0, buffer, record.Length);
            Assert.Equal(
                "phoenix-arm64.jsonl",
                PortalPathGuard.ReadNullTerminatedUtf8(buffer, record.Length));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [Fact]
    public void AnchoredDirectoryEnumerationReturnsExactEntryNames()
    {
        string root = Directory.CreateTempSubdirectory("codenav-portal-path-guard").FullName;
        try
        {
            string telemetryDirectory = Path.Combine(root, ".codenav", "telemetry");
            Directory.CreateDirectory(telemetryDirectory);
            File.WriteAllText(
                Path.Combine(telemetryDirectory, "phoenix-arm64.jsonl"),
                "{}\n");

            Assert.Equal(
                PortalPathGuard.EntryState.Safe,
                PortalPathGuard.OpenDirectory(
                    root,
                    Path.Combine(".codenav", "telemetry"),
                    out PortalRegularFile? directory));
            using (directory)
            {
                Assert.NotNull(directory);
                Assert.True(
                    PortalPathGuard.TryEnumerateDirectoryNames(
                        directory,
                        maximum: 16,
                        out string[] names));
                Assert.Equal(["phoenix-arm64.jsonl"], names);
            }

            Assert.Equal(
                PortalPathGuard.EntryState.Safe,
                PortalPathGuard.OpenRegularFile(
                    root,
                    Path.Combine(".codenav", "telemetry", "phoenix-arm64.jsonl"),
                    out PortalRegularFile? file));
            using (file)
            {
                Assert.NotNull(file);
                Assert.Equal(3, file.Metadata.Length);
                using var reader = new StreamReader(file.Stream);
                Assert.Equal("{}\n", reader.ReadToEnd());
            }
        }
        finally
        {
            TestWorkspaceCleanup.DeleteWorkspace(root);
        }
    }

    [Theory]
    [InlineData(Architecture.X86, 0x010000, 0x020000)]
    [InlineData(Architecture.X64, 0x010000, 0x020000)]
    [InlineData(Architecture.S390x, 0x010000, 0x020000)]
    [InlineData(Architecture.LoongArch64, 0x010000, 0x020000)]
    [InlineData(Architecture.RiscV64, 0x010000, 0x020000)]
    [InlineData(Architecture.Arm, 0x004000, 0x008000)]
    [InlineData(Architecture.Armv6, 0x004000, 0x008000)]
    [InlineData(Architecture.Arm64, 0x004000, 0x008000)]
    [InlineData(Architecture.Ppc64le, 0x004000, 0x008000)]
    public void LinuxOpenFlagsMatchCoreAndTheArchitectureAbi(Architecture architecture,
        int expectedDirectory, int expectedNoFollow)
    {
        int portalDirectory =
            PortalPathGuard.LinuxDirectoryForArchitecture(architecture);
        int portalNoFollow =
            PortalPathGuard.LinuxNoFollowForArchitecture(architecture);

        Assert.Equal(expectedDirectory, portalDirectory);
        Assert.Equal(expectedNoFollow, portalNoFollow);
        Assert.Equal(
            LinuxOpenFlags.DirectoryForArchitecture(architecture),
            portalDirectory);
        Assert.Equal(
            LinuxOpenFlags.NoFollowForArchitecture(architecture),
            portalNoFollow);
    }

    [Theory]
    [InlineData(Architecture.Wasm)]
    [InlineData((Architecture)int.MaxValue)]
    public void UnknownLinuxArchitectureFailsClosed(Architecture architecture)
    {
        Assert.Throws<PlatformNotSupportedException>(
            () => PortalPathGuard.LinuxDirectoryForArchitecture(architecture));
        Assert.Throws<PlatformNotSupportedException>(
            () => PortalPathGuard.LinuxNoFollowForArchitecture(architecture));
    }
}
