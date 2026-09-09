using System.Text.Json;
using CodeNav.Core.Semantic;

namespace CodeNav.Tests;

public partial class FSharpSemanticStage2Tests
{
    [Fact]
    public void PackageReferenceCompileAssetEnablesFSharpSemanticResolution()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-semantic-package").FullName;
        try
        {
            const string packageId = "System.IO.Hashing";
            const string packageVersion = "10.0.10";
            string packagesRoot = Environment.GetEnvironmentVariable("NUGET_PACKAGES") is
            { Length: > 0 } configuredPackages
                ? configuredPackages
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".nuget", "packages");
            string packageFolder = Path.Combine(packagesRoot, packageId.ToLowerInvariant(),
                packageVersion);
            string packageDll = Path.Combine(packageFolder, "lib", "net10.0",
                "System.IO.Hashing.dll");
            Assert.True(File.Exists(packageDll),
                $"The solution restore must provide the package fixture: {packageDll}");

            WriteProject(root, "Core/Core.fsproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                  </PropertyGroup>
                  <ItemGroup>
                    <Compile Include="Use.fs" />
                    <PackageReference Include="System.IO.Hashing" Version="10.0.10" />
                  </ItemGroup>
                </Project>
                """);
            WriteProject(root, "Core/Use.fs", """
                namespace PackageConsumer
                open System.IO.Hashing
                module Use =
                    let hasher = XxHash64()
                """);
            WritePackageAssets(root, "Core/Core.fsproj", "net10.0", packageId,
                packageVersion, packagesRoot, "lib/net10.0/System.IO.Hashing.dll");

            using var fixture = Fixture.Create(root);
            string raw = CallSemantic(() => fixture.Tools.SymbolAt(
                "Core/Use.fs", 4, 19, timeoutMs: 60_000));
            JsonElement response = Parse(raw);
            Assert.False(response.TryGetProperty("error", out _), raw);
            Assert.True(response.GetProperty("found").GetBoolean(), raw);
            Assert.Equal("XxHash64",
                response.GetProperty("symbol").GetProperty("name").GetString());
            Assert.Equal("System.IO.Hashing",
                response.GetProperty("symbol").GetProperty("assembly").GetString());
            Assert.Contains("fsharp_package_references_snapshotted",
                response.GetProperty("partialReason").GetString());
            Assert.True(response.GetProperty("partial").GetBoolean());
            Assert.Equal("exact",
                response.GetProperty("meta").GetProperty("confidence").GetString());
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void PackageReferenceUsesTransitiveCompileAssetsFromSelectedTarget()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-semantic-package-transitive").FullName;
        try
        {
            const string directId = "Microsoft.Extensions.Hosting";
            const string transitiveId = "Microsoft.Extensions.DependencyInjection.Abstractions";
            const string version = "10.0.10";
            string packagesRoot = Environment.GetEnvironmentVariable("NUGET_PACKAGES") is
            { Length: > 0 } configuredPackages
                ? configuredPackages
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".nuget", "packages");
            Assert.True(File.Exists(Path.Combine(packagesRoot, directId.ToLowerInvariant(),
                version, "lib", "net10.0", "Microsoft.Extensions.Hosting.dll")));
            Assert.True(File.Exists(Path.Combine(packagesRoot, transitiveId.ToLowerInvariant(),
                version, "lib", "net10.0",
                "Microsoft.Extensions.DependencyInjection.Abstractions.dll")));

            WriteProject(root, "Core/Core.fsproj", $$"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                  </PropertyGroup>
                  <ItemGroup>
                    <Compile Include="Use.fs" />
                    <PackageReference Include="{{directId}}" Version="{{version}}" />
                  </ItemGroup>
                </Project>
                """);
            WriteProject(root, "Core/Use.fs", """
                namespace PackageConsumer
                open Microsoft.Extensions.DependencyInjection
                module Use =
                    let configure (services: IServiceCollection) = services
                """);
            WritePackageAssets(root, "Core/Core.fsproj", "net10.0", directId,
                version, packagesRoot, "lib/net10.0/Microsoft.Extensions.Hosting.dll",
                (transitiveId, version,
                    "lib/net10.0/Microsoft.Extensions.DependencyInjection.Abstractions.dll"));

            using var fixture = Fixture.Create(root);
            string raw = CallSemantic(() => fixture.Tools.SymbolAt(
                "Core/Use.fs", 4, 30, timeoutMs: 60_000));
            JsonElement response = Parse(raw);
            Assert.False(response.TryGetProperty("error", out _), raw);
            Assert.True(response.GetProperty("found").GetBoolean(), raw);
            Assert.Equal("IServiceCollection",
                response.GetProperty("symbol").GetProperty("name").GetString());
            Assert.Equal("Microsoft.Extensions.DependencyInjection.Abstractions",
                response.GetProperty("symbol").GetProperty("assembly").GetString());
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void PackageReferenceIgnoresUnreachableRestoredPackageCompileAssets()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-semantic-package-unreachable").FullName;
        try
        {
            const string packageId = "System.IO.Hashing";
            const string packageVersion = "10.0.10";
            string packagesRoot = Environment.GetEnvironmentVariable("NUGET_PACKAGES") is
            { Length: > 0 } configuredPackages
                ? configuredPackages
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".nuget", "packages");
            Assert.True(File.Exists(Path.Combine(packagesRoot, packageId.ToLowerInvariant(),
                packageVersion, "lib", "net10.0", "System.IO.Hashing.dll")));

            WriteProject(root, "Core/Core.fsproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                  </PropertyGroup>
                  <ItemGroup>
                    <Compile Include="Use.fs" />
                    <PackageReference Include="System.IO.Hashing" Version="10.0.10" />
                  </ItemGroup>
                </Project>
                """);
            WriteProject(root, "Core/Use.fs", """
                namespace PackageConsumer
                open System.IO.Hashing
                module Use =
                    let hasher = XxHash64()
                """);
            WritePackageAssetsWithUnreachablePackage(root, "Core/Core.fsproj", "net10.0",
                packageId, packageVersion, packagesRoot,
                "lib/net10.0/System.IO.Hashing.dll",
                ("Removed.Package", "1.0.0", "lib/net10.0/Missing.dll"));

            using var fixture = Fixture.Create(root);
            string raw = CallSemantic(() => fixture.Tools.SymbolAt(
                "Core/Use.fs", 4, 19, timeoutMs: 60_000));
            JsonElement response = Parse(raw);
            Assert.False(response.TryGetProperty("error", out _), raw);
            Assert.True(response.GetProperty("found").GetBoolean(), raw);
            Assert.Equal("XxHash64",
                response.GetProperty("symbol").GetProperty("name").GetString());
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void PackageReferenceRejectsExtraDirectPackageInAssets()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-semantic-package-extra-direct").FullName;
        try
        {
            const string packageId = "System.IO.Hashing";
            const string packageVersion = "10.0.10";
            string packagesRoot = Environment.GetEnvironmentVariable("NUGET_PACKAGES") is
            { Length: > 0 } configuredPackages
                ? configuredPackages
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".nuget", "packages");
            Assert.True(File.Exists(Path.Combine(packagesRoot, packageId.ToLowerInvariant(),
                packageVersion, "lib", "net10.0", "System.IO.Hashing.dll")));

            WriteProject(root, "Core/Core.fsproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                  </PropertyGroup>
                  <ItemGroup>
                    <Compile Include="Use.fs" />
                    <PackageReference Include="System.IO.Hashing" Version="10.0.10" />
                  </ItemGroup>
                </Project>
                """);
            WriteProject(root, "Core/Use.fs", """
                namespace PackageConsumer
                open System.IO.Hashing
                module Use =
                    let hasher = XxHash64()
                """);
            WritePackageAssetsWithExtraDirectPackage(root, "Core/Core.fsproj", "net10.0",
                packageId, packageVersion, packagesRoot,
                "lib/net10.0/System.IO.Hashing.dll",
                ("Stale.Package", "1.0.0", "lib/net10.0/Stale.Package.dll"));

            using var fixture = Fixture.Create(root);
            string raw = CallSemantic(() => fixture.Tools.SymbolAt(
                "Core/Use.fs", 4, 19, timeoutMs: 60_000));
            JsonElement response = Parse(raw);
            Assert.True(response.TryGetProperty("error", out JsonElement error), raw);
            Assert.Equal("fsharp_semantic_package_assets_stale",
                error.GetString());
            Assert.False(response.TryGetProperty("found", out JsonElement found) &&
                         found.ValueKind == JsonValueKind.True);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Theory]
    [InlineData("System.IO.Hashing")]
    [InlineData("system.io.hashing")]
    public void PackageReferenceRejectsDuplicateOrAmbiguousDirectPackageIdentityInAssets(
        string duplicateId)
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-semantic-package-duplicate-direct").FullName;
        try
        {
            const string packageId = "System.IO.Hashing";
            const string packageVersion = "10.0.10";
            string packagesRoot = Environment.GetEnvironmentVariable("NUGET_PACKAGES") is
            { Length: > 0 } configuredPackages
                ? configuredPackages
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".nuget", "packages");
            Assert.True(File.Exists(Path.Combine(packagesRoot, packageId.ToLowerInvariant(),
                packageVersion, "lib", "net10.0", "System.IO.Hashing.dll")));

            WriteProject(root, "Core/Core.fsproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                  </PropertyGroup>
                  <ItemGroup>
                    <Compile Include="Use.fs" />
                    <PackageReference Include="System.IO.Hashing" Version="10.0.10" />
                  </ItemGroup>
                </Project>
                """);
            WriteProject(root, "Core/Use.fs", "module Use\nlet value = 1\n");
            WritePackageAssetsWithAdditionalDirectDependencyEntry(root,
                "Core/Core.fsproj", "net10.0", packageId, packageVersion, packagesRoot,
                "lib/net10.0/System.IO.Hashing.dll", duplicateId);

            using var fixture = Fixture.Create(root);
            string raw = CallSemantic(() => fixture.Tools.SymbolAt(
                "Core/Use.fs", 2, 5, timeoutMs: 60_000));
            JsonElement response = Parse(raw);
            Assert.True(response.TryGetProperty("error", out JsonElement error), raw);
            Assert.Equal("fsharp_semantic_package_assets_stale", error.GetString());
            Assert.False(response.TryGetProperty("found", out JsonElement found) &&
                         found.ValueKind == JsonValueKind.True);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void PackageReferenceMatchesDirectAssetsIdentityCaseInsensitively()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-semantic-package-direct-case").FullName;
        try
        {
            const string packageId = "System.IO.Hashing";
            const string packageVersion = "10.0.10";
            string packagesRoot = Environment.GetEnvironmentVariable("NUGET_PACKAGES") is
            { Length: > 0 } configuredPackages
                ? configuredPackages
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".nuget", "packages");
            Assert.True(File.Exists(Path.Combine(packagesRoot, packageId.ToLowerInvariant(),
                packageVersion, "lib", "net10.0", "System.IO.Hashing.dll")));

            WriteProject(root, "Core/Core.fsproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                  </PropertyGroup>
                  <ItemGroup>
                    <Compile Include="Use.fs" />
                    <PackageReference Include="System.IO.Hashing" Version="10.0.10" />
                  </ItemGroup>
                </Project>
                """);
            WriteProject(root, "Core/Use.fs", """
                namespace PackageConsumer
                open System.IO.Hashing
                module Use =
                    let hasher = XxHash64()
                """);
            WritePackageAssetsWithDirectDependencyIdentity(root, "Core/Core.fsproj",
                "net10.0", packageId, packageVersion, packagesRoot,
                "lib/net10.0/System.IO.Hashing.dll", "system.io.hashing");

            using var fixture = Fixture.Create(root);
            string raw = CallSemantic(() => fixture.Tools.SymbolAt(
                "Core/Use.fs", 4, 19, timeoutMs: 60_000));
            JsonElement response = Parse(raw);
            Assert.False(response.TryGetProperty("error", out _), raw);
            Assert.True(response.GetProperty("found").GetBoolean(), raw);
            Assert.Equal("XxHash64",
                response.GetProperty("symbol").GetProperty("name").GetString());
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void PackageReferenceAcceptsSdkAutoReferencedDirectPackageInAssets()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-semantic-package-auto-referenced").FullName;
        try
        {
            const string packageId = "System.IO.Hashing";
            const string packageVersion = "10.0.10";
            const string fsharpCoreVersion = "10.1.204";
            string packagesRoot = Environment.GetEnvironmentVariable("NUGET_PACKAGES") is
            { Length: > 0 } configuredPackages
                ? configuredPackages
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".nuget", "packages");
            Assert.True(File.Exists(Path.Combine(packagesRoot, packageId.ToLowerInvariant(),
                packageVersion, "lib", "net10.0", "System.IO.Hashing.dll")));
            Assert.True(File.Exists(Path.Combine(packagesRoot, "fsharp.core",
                fsharpCoreVersion, "lib", "netstandard2.0", "FSharp.Core.dll")));

            WriteProject(root, "Core/Core.fsproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                  </PropertyGroup>
                  <ItemGroup>
                    <Compile Include="Use.fs" />
                    <PackageReference Include="System.IO.Hashing" Version="10.0.10" />
                  </ItemGroup>
                </Project>
                """);
            WriteProject(root, "Core/Use.fs", """
                namespace PackageConsumer
                open System.IO.Hashing
                module Use =
                    let hasher = XxHash64()
                """);
            WritePackageAssetsWithAutoReferencedPackage(root, "Core/Core.fsproj", "net10.0",
                packageId, packageVersion, packagesRoot,
                "lib/net10.0/System.IO.Hashing.dll",
                ("FSharp.Core", fsharpCoreVersion,
                    "lib/netstandard2.0/FSharp.Core.dll"));

            using var fixture = Fixture.Create(root);
            string raw = CallSemantic(() => fixture.Tools.SymbolAt(
                "Core/Use.fs", 4, 19, timeoutMs: 60_000));
            JsonElement response = Parse(raw);
            Assert.False(response.TryGetProperty("error", out _), raw);
            Assert.True(response.GetProperty("found").GetBoolean(), raw);
            Assert.Equal("XxHash64",
                response.GetProperty("symbol").GetProperty("name").GetString());
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Theory]
    [InlineData("non-Boolean marker", "[10.1.204, )", "\"true\"", true, true,
        "10.1.204")]
    [InlineData("missing version", null, "true", false, true, "10.1.204")]
    [InlineData("invalid version", "[invalid", "true", false, true, "10.1.204")]
    [InlineData("missing selected target", "[10.1.204, )", "true", false, false,
        "10.1.204")]
    [InlineData("mismatched selected target", "[10.1.205, )", "true", false, true,
        "10.1.204")]
    public void PackageReferenceRejectsMalformedOrUnsatisfiedSdkAutoReferencedPackageInAssets(
        string caseName, string? dependencyVersion, string autoReferencedJson,
        bool declareAutoReferencedPackage, bool includeSelectedTarget,
        string selectedVersion)
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-semantic-package-invalid-auto-referenced").FullName;
        try
        {
            const string packageId = "System.IO.Hashing";
            const string packageVersion = "10.0.10";
            const string autoReferencedPackageId = "FSharp.Core";
            string packagesRoot = Environment.GetEnvironmentVariable("NUGET_PACKAGES") is
            { Length: > 0 } configuredPackages
                ? configuredPackages
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".nuget", "packages");
            Assert.True(File.Exists(Path.Combine(packagesRoot, packageId.ToLowerInvariant(),
                packageVersion, "lib", "net10.0", "System.IO.Hashing.dll")));
            Assert.True(File.Exists(Path.Combine(packagesRoot,
                autoReferencedPackageId.ToLowerInvariant(), selectedVersion, "lib",
                "netstandard2.0", "FSharp.Core.dll")), caseName);

            string explicitAutoReferencedPackage = declareAutoReferencedPackage
                ? $"<PackageReference Include=\"{autoReferencedPackageId}\" " +
                  $"Version=\"{selectedVersion}\" />"
                : "";
            WriteProject(root, "Core/Core.fsproj", $$"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                  </PropertyGroup>
                  <ItemGroup>
                    <Compile Include="Use.fs" />
                    <PackageReference Include="System.IO.Hashing" Version="10.0.10" />
                    {{explicitAutoReferencedPackage}}
                  </ItemGroup>
                </Project>
                """);
            WriteProject(root, "Core/Use.fs", "module Use\nlet value = 1\n");
            WritePackageAssetsWithRawAutoReferencedPackage(root, "Core/Core.fsproj",
                "net10.0", packageId, packageVersion, packagesRoot,
                "lib/net10.0/System.IO.Hashing.dll", autoReferencedPackageId,
                dependencyVersion, autoReferencedJson, includeSelectedTarget,
                selectedVersion, "lib/netstandard2.0/FSharp.Core.dll");

            using var fixture = Fixture.Create(root);
            string raw = CallSemantic(() => fixture.Tools.SymbolAt(
                "Core/Use.fs", 2, 5, timeoutMs: 60_000));
            JsonElement response = Parse(raw);
            Assert.True(response.TryGetProperty("error", out JsonElement error),
                $"{caseName}: {raw}");
            Assert.Equal("fsharp_semantic_package_assets_stale", error.GetString());
            Assert.False(response.TryGetProperty("found", out JsonElement found) &&
                         found.ValueKind == JsonValueKind.True);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void PackageReferenceRejectsLibraryPathRedirectedToAnotherPackage()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-semantic-package-library-redirect").FullName;
        try
        {
            const string declaredPackageId = "Expected.Package";
            const string declaredPackageVersion = "1.0.0";
            const string actualPackageId = "System.IO.Hashing";
            const string actualPackageVersion = "10.0.10";
            string packagesRoot = Environment.GetEnvironmentVariable("NUGET_PACKAGES") is
            { Length: > 0 } configuredPackages
                ? configuredPackages
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".nuget", "packages");
            Assert.True(File.Exists(Path.Combine(packagesRoot, actualPackageId.ToLowerInvariant(),
                actualPackageVersion, "lib", "net10.0", "System.IO.Hashing.dll")));

            WriteProject(root, "Core/Core.fsproj", $$"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                  </PropertyGroup>
                  <ItemGroup>
                    <Compile Include="Use.fs" />
                    <PackageReference Include="{{declaredPackageId}}" Version="{{declaredPackageVersion}}" />
                  </ItemGroup>
                </Project>
                """);
            WriteProject(root, "Core/Use.fs", """
                namespace PackageConsumer
                open System.IO.Hashing
                module Use =
                    let hasher = XxHash64()
                """);
            WritePackageAssetsWithLibraryPath(root, "Core/Core.fsproj", "net10.0",
                declaredPackageId, declaredPackageVersion, packagesRoot,
                "lib/net10.0/System.IO.Hashing.dll",
                $"{actualPackageId.ToLowerInvariant()}/{actualPackageVersion}");

            using var fixture = Fixture.Create(root);
            string raw = CallSemantic(() => fixture.Tools.SymbolAt(
                "Core/Use.fs", 4, 19, timeoutMs: 60_000));
            JsonElement response = Parse(raw);
            Assert.True(response.TryGetProperty("error", out JsonElement error), raw);
            Assert.Equal("fsharp_semantic_package_assets_unavailable",
                error.GetString());
            Assert.False(response.TryGetProperty("found", out JsonElement found) &&
                         found.ValueKind == JsonValueKind.True);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void PackageRootSearchStopsProbingAfterRequestCancellation()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-semantic-package-root-cancellation").FullName;
        try
        {
            string firstRoot = Path.Combine(root, "PackagesA");
            string secondRoot = Path.Combine(root, "PackagesB");
            Directory.CreateDirectory(firstRoot);
            Directory.CreateDirectory(secondRoot);
            WriteProject(root, "Core/Core.fsproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                  </PropertyGroup>
                  <ItemGroup>
                    <Compile Include="Use.fs" />
                    <PackageReference Include="Example.Package" Version="1.0.0" />
                  </ItemGroup>
                </Project>
                """);
            WriteProject(root, "Core/Use.fs", "module Use\nlet value = 1\n");
            WritePackageAssetsWithRoots(root, "Core/Core.fsproj", "net10.0",
                "Example.Package", "1.0.0", [firstRoot, secondRoot],
                "lib/net10.0/Example.Package.dll");

            using var fixture = Fixture.Create(root);
            int probes = 0;
            fixture.Semantic.BeforeFSharpPackageRootProbeForTest = _ =>
            {
                if (Interlocked.Increment(ref probes) == 1) Thread.Sleep(750);
            };
            JsonElement response = Parse(fixture.Tools.SymbolAt(
                "Core/Use.fs", 2, 5, timeoutMs: 500));

            Assert.Equal("fsharp_semantic_timeout",
                response.GetProperty("error").GetString());
            Assert.Equal("indexed",
                response.GetProperty("meta").GetProperty("confidence").GetString());
            Assert.Equal(1, Volatile.Read(ref probes));

            Volatile.Write(ref probes, 0);
            JsonElement references = Parse(fixture.Tools.References(
                path: "Core/Use.fs", line: 2, column: 5, mode: "semantic",
                timeoutMs: 500));
            Assert.Equal("fsharp_semantic_timeout",
                references.GetProperty("error").GetString());
            Assert.False(references.TryGetProperty("totalReferences", out _));
            Assert.Equal(1, Volatile.Read(ref probes));
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void PackageReferenceAcceptsNuGetNormalizedMinimumVersion()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-semantic-package-normalized-version").FullName;
        try
        {
            const string packageId = "System.IO.Hashing";
            const string restoredVersion = "10.0.10";
            string packagesRoot = Environment.GetEnvironmentVariable("NUGET_PACKAGES") is
            { Length: > 0 } configuredPackages
                ? configuredPackages
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".nuget", "packages");
            Assert.True(File.Exists(Path.Combine(packagesRoot, packageId.ToLowerInvariant(),
                restoredVersion, "lib", "net10.0", "System.IO.Hashing.dll")));

            WriteProject(root, "Core/Core.fsproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                  </PropertyGroup>
                  <ItemGroup>
                    <Compile Include="Use.fs" />
                    <PackageReference Include="System.IO.Hashing" Version="10.0" />
                  </ItemGroup>
                </Project>
                """);
            WriteProject(root, "Core/Use.fs", """
                namespace PackageConsumer
                open System.IO.Hashing
                module Use =
                    let hasher = XxHash64()
                """);
            WritePackageAssets(root, "Core/Core.fsproj", "net10.0", packageId,
                restoredVersion, packagesRoot, "lib/net10.0/System.IO.Hashing.dll",
                requestedMinimumVersion: "10.0.0");

            using var fixture = Fixture.Create(root);
            string raw = CallSemantic(() => fixture.Tools.SymbolAt(
                "Core/Use.fs", 4, 19, timeoutMs: 60_000));
            JsonElement response = Parse(raw);
            Assert.False(response.TryGetProperty("error", out _), raw);
            Assert.True(response.GetProperty("found").GetBoolean(), raw);
            Assert.Equal("XxHash64",
                response.GetProperty("symbol").GetProperty("name").GetString());
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void PackageReferenceAcceptsFullNuGetTargetKeyForNet472()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-semantic-package-net472").FullName;
        try
        {
            const string packageId = "System.IO.Hashing";
            const string packageVersion = "10.0.10";
            string packagesRoot = Environment.GetEnvironmentVariable("NUGET_PACKAGES") is
            { Length: > 0 } configuredPackages
                ? configuredPackages
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".nuget", "packages");
            Assert.True(File.Exists(Path.Combine(packagesRoot, packageId.ToLowerInvariant(),
                packageVersion, "lib", "net462", "System.IO.Hashing.dll")));

            WriteProject(root, "Core/Core.fsproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net472</TargetFramework>
                    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                  </PropertyGroup>
                  <ItemGroup>
                    <Compile Include="Use.fs" />
                    <PackageReference Include="System.IO.Hashing" Version="10.0.10" />
                  </ItemGroup>
                </Project>
                """);
            WriteProject(root, "Core/Use.fs", """
                namespace PackageConsumer
                open System.IO.Hashing
                module Use =
                    let hasher = XxHash64()
                """);
            WritePackageAssetsForConstraint(root, "Core/Core.fsproj", "net472",
                ".NETFramework,Version=v4.7.2", packageId, packageVersion, packagesRoot,
                "lib/net462/System.IO.Hashing.dll", "[10.0.10, )");

            using var fixture = Fixture.Create(root);
            string raw = CallSemantic(() => fixture.Tools.SymbolAt(
                "Core/Use.fs", 4, 19, timeoutMs: 60_000));
            JsonElement response = Parse(raw);
            if (response.TryGetProperty("error", out JsonElement error))
            {
                Assert.Equal("fsharp_framework_references_unavailable", error.GetString());
                Assert.Empty(ReferenceAssemblyLocator.FrameworkReferencePaths("net472", out _));
                return;
            }
            Assert.False(response.TryGetProperty("error", out _), raw);
            Assert.True(response.GetProperty("found").GetBoolean(), raw);
            Assert.Equal("XxHash64",
                response.GetProperty("symbol").GetProperty("name").GetString());
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void PackageReferenceRangesAndFloatingVersionsAreValidatedAgainstAssets()
    {
        const string packageId = "System.IO.Hashing";
        const string packageVersion = "10.0.10";
        string packagesRoot = Environment.GetEnvironmentVariable("NUGET_PACKAGES") is
        { Length: > 0 } configuredPackages
            ? configuredPackages
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".nuget", "packages");
        Assert.True(File.Exists(Path.Combine(packagesRoot, packageId.ToLowerInvariant(),
            packageVersion, "lib", "net10.0", "System.IO.Hashing.dll")));

        foreach ((string name, string requested, string restoredConstraint,
                     bool shouldSucceed) in new[]
                 {
                     ("range-match", "[10.0,11.0)", "[10.0.0, 11.0.0)", true),
                     ("range-mismatch", "[9.0,10.0)", "[10.0.0, 11.0.0)", false),
                     ("floating-match", "10.*", "[10.*, )", true),
                     ("floating-mismatch", "9.*", "[10.*, )", false),
                 })
        {
            string root = Directory.CreateTempSubdirectory(
                $"codenav-fsharp-semantic-package-{name}").FullName;
            try
            {
                WriteProject(root, "Core/Core.fsproj", $$"""
                    <Project Sdk="Microsoft.NET.Sdk">
                      <PropertyGroup>
                        <TargetFramework>net10.0</TargetFramework>
                        <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                      </PropertyGroup>
                      <ItemGroup>
                        <Compile Include="Use.fs" />
                        <PackageReference Include="System.IO.Hashing" Version="{{requested}}" />
                      </ItemGroup>
                    </Project>
                    """);
                WriteProject(root, "Core/Use.fs", """
                    namespace PackageConsumer
                    open System.IO.Hashing
                    module Use =
                        let hasher = XxHash64()
                    """);
                WritePackageAssetsForConstraint(root, "Core/Core.fsproj", "net10.0",
                    "net10.0", packageId, packageVersion, packagesRoot,
                    "lib/net10.0/System.IO.Hashing.dll", restoredConstraint);

                using var fixture = Fixture.Create(root);
                string raw = CallSemantic(() => fixture.Tools.SymbolAt(
                    "Core/Use.fs", 4, 19, timeoutMs: 60_000));
                JsonElement response = Parse(raw);
                if (shouldSucceed)
                {
                    Assert.False(response.TryGetProperty("error", out _), raw);
                    Assert.True(response.GetProperty("found").GetBoolean(), raw);
                }
                else
                {
                    Assert.Equal("fsharp_semantic_package_assets_stale",
                        response.GetProperty("error").GetString());
                    Assert.False(response.TryGetProperty("found", out JsonElement found) &&
                                 found.ValueKind == JsonValueKind.True);
                }
            }
            finally
            {
                Cleanup(root);
            }
        }
    }

    [Fact]
    public void CentralPackageManagementRejectsAssetsFromAnotherCentralVersion()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-semantic-central-package-stale").FullName;
        try
        {
            const string packageId = "System.IO.Hashing";
            const string restoredVersion = "10.0.10";
            string packagesRoot = Environment.GetEnvironmentVariable("NUGET_PACKAGES") is
            { Length: > 0 } configuredPackages
                ? configuredPackages
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".nuget", "packages");
            Assert.True(File.Exists(Path.Combine(packagesRoot, packageId.ToLowerInvariant(),
                restoredVersion, "lib", "net10.0", "System.IO.Hashing.dll")));

            WriteProject(root, "Directory.Packages.props", """
                <Project>
                  <PropertyGroup>
                    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
                  </PropertyGroup>
                  <ItemGroup>
                    <PackageVersion Include="System.IO.Hashing" Version="10.0.10" />
                  </ItemGroup>
                </Project>
                """);
            WriteProject(root, "Core/Core.fsproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                  </PropertyGroup>
                  <ItemGroup>
                    <Compile Include="Use.fs" />
                    <PackageReference Include="System.IO.Hashing" />
                  </ItemGroup>
                </Project>
                """);
            WriteProject(root, "Core/Use.fs", """
                namespace PackageConsumer
                open System.IO.Hashing
                module Use =
                    let hasher = XxHash64()
                """);
            WritePackageAssets(root, "Core/Core.fsproj", "net10.0", packageId,
                restoredVersion, packagesRoot, "lib/net10.0/System.IO.Hashing.dll",
                requestedMinimumVersion: "9.9.9");

            using var fixture = Fixture.Create(root);
            JsonElement response = Parse(CallSemantic(() => fixture.Tools.SymbolAt(
                "Core/Use.fs", 4, 19, timeoutMs: 60_000)));
            Assert.Equal("fsharp_semantic_package_assets_stale",
                response.GetProperty("error").GetString());
            Assert.False(response.TryGetProperty("found", out JsonElement found) &&
                         found.ValueKind == JsonValueKind.True);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Theory]
    [InlineData(false, "none", "none")]
    [InlineData(true, "none", "none")]
    [InlineData(true, "global", "none")]
    [InlineData(true, "stale-mask", "none")]
    [InlineData(false, "none", "append")]
    [InlineData(false, "none", "remove")]
    [InlineData(false, "none", "paired-remove")]
    public void CentralPackageManagementEnablesFSharpSemanticResolution(
        bool centralReference, string globalScenario, string mutation)
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-semantic-central-package").FullName;
        try
        {
            const string packageId = "System.IO.Hashing";
            const string packageVersion = "10.0.10";
            string packagesRoot = Environment.GetEnvironmentVariable("NUGET_PACKAGES") is
            { Length: > 0 } configuredPackages
                ? configuredPackages
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".nuget", "packages");
            Assert.True(File.Exists(Path.Combine(packagesRoot, packageId.ToLowerInvariant(),
                packageVersion, "lib", "net10.0", "System.IO.Hashing.dll")));

            WriteProject(root, "Directory.Packages.props", $$"""
                <Project>
                  <PropertyGroup>
                    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
                  </PropertyGroup>
                  <ItemGroup>
                    <PackageVersion Include="System.IO.Hashing" Version="10.0.10" />
                    {{(mutation != "none" ? "<PackageVersion Include=\"Later.Package\" Version=\"1.2.3\" />" : "")}}
                    {{(centralReference ? "<PackageReference Include=\"System.IO.Hashing\" />" : "")}}
                    {{(globalScenario != "none" ? "<GlobalPackageReference Include=\"Build.Tool\" Version=\"3.0.0\" />" : "")}}
                  </ItemGroup>
                </Project>
                """);
            string references = centralReference ? "" : "<PackageReference Include=\"System.IO.Hashing\" />";
            if (mutation is "append" or "remove")
            {
                references = "<Ids Include=\"System.IO.Hashing\" /><PackageReference Include=\"@(Ids)\" />" +
                    (mutation == "append" ? "<Ids Include=\"Later.Package\" />" : "<Ids Remove=\"System.IO.Hashing\" />");
            }
            else if (mutation == "paired-remove")
            {
                references += "<PackageReference Include=\"Later.Package\" /><PackageVersion Remove=\"Later.Package\" /><PackageReference Remove=\"Later.Package\" />";
            }
            WriteProject(root, "Core/Core.fsproj", $$"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                  </PropertyGroup>
                  <ItemGroup>
                    {{references}}
                    <Compile Include="Use.fs" />
                  </ItemGroup>
                </Project>
                """);
            WriteProject(root, "Core/Use.fs", """
                namespace PackageConsumer
                open System.IO.Hashing
                module Use =
                    let hasher = XxHash64()
                """);
            WritePackageAssets(root, "Core/Core.fsproj", "net10.0", packageId,
                packageVersion, packagesRoot, "lib/net10.0/System.IO.Hashing.dll");

            if (globalScenario != "none")
            {
                // A fake compile DLL is deliberately absent: global references must never try to read it.
                WritePackageAssetsCore(root, "Core/Core.fsproj", "net10.0", packageId,
                    packageVersion, [packagesRoot], "lib/net10.0/System.IO.Hashing.dll", "net10.0",
                    "[10.0.10, )", [("Build.Tool", "3.0.0", "lib/net10.0/MustNotCompile.dll")], [],
                    extraDirectPackages: [("Build.Tool", "[3.0.0, )", null)]);
                string assetsPath = Path.Combine(root, "Core", "obj", "project.assets.json");
                var assets = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(assetsPath))!;
                assets["project"]!["frameworks"]!["net10.0"]!["dependencies"]!["Build.Tool"]!["include"] =
                    globalScenario == "stale-mask" ? "All" : "Runtime, Build, Native, ContentFiles, Analyzers";
                File.WriteAllText(assetsPath, assets.ToJsonString());
            }

            using var fixture = Fixture.Create(root);
            string raw = CallSemantic(() => fixture.Tools.SymbolAt(
                "Core/Use.fs", 4, 19, timeoutMs: 60_000));
            JsonElement response = Parse(raw);
            if (globalScenario == "stale-mask")
            {
                Assert.Equal("fsharp_semantic_package_assets_stale", response.GetProperty("error").GetString());
                Assert.False(response.TryGetProperty("found", out var found) && found.GetBoolean());
                return;
            }
            Assert.False(response.TryGetProperty("error", out _), raw);
            Assert.True(response.GetProperty("found").GetBoolean(), raw);
            Assert.Equal("XxHash64",
                response.GetProperty("symbol").GetProperty("name").GetString());
            Assert.Contains("fsharp_package_references_snapshotted",
                response.GetProperty("partialReason").GetString());
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void PackageAssetsFailClosedWhenEvaluatedCentralAuthorityChangesAfterRestore()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-semantic-central-package-authority-stale").FullName;
        try
        {
            const string packageId = "System.IO.Hashing";
            const string packageVersion = "10.0.10";
            string packagesRoot = Environment.GetEnvironmentVariable("NUGET_PACKAGES") is
            { Length: > 0 } configuredPackages
                ? configuredPackages
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".nuget", "packages");
            string directoryPackagesPath = Path.Combine(root, "Directory.Packages.props");
            WriteProject(root, "Directory.Packages.props", """
                <Project>
                  <PropertyGroup>
                    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
                  </PropertyGroup>
                  <ItemGroup>
                    <PackageVersion Include="System.IO.Hashing" Version="10.0.10" />
                  </ItemGroup>
                </Project>
                """);
            WriteProject(root, "Core/Core.fsproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                  </PropertyGroup>
                  <ItemGroup>
                    <Compile Include="Use.fs" />
                    <PackageReference Include="System.IO.Hashing" />
                  </ItemGroup>
                </Project>
                """);
            WriteProject(root, "Core/Use.fs", "module Use\nlet value = 1\n");
            WritePackageAssets(root, "Core/Core.fsproj", "net10.0", packageId,
                packageVersion, packagesRoot, "lib/net10.0/System.IO.Hashing.dll");

            using var fixture = Fixture.Create(root);
            WriteProject(root, "Directory.Packages.props", """
                <Project>
                  <PropertyGroup>
                    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
                  </PropertyGroup>
                  <ItemGroup>
                    <PackageVersion Include="System.IO.Hashing" Version="10.0.10" />
                    <PackageVersion Include="Transitive.Pin" Version="2.0.0" />
                  </ItemGroup>
                </Project>
                """);
            string assetsPath = Path.Combine(root, "Core", "obj", "project.assets.json");
            File.SetLastWriteTimeUtc(directoryPackagesPath,
                File.GetLastWriteTimeUtc(assetsPath).AddMinutes(1));

            JsonElement response = Parse(CallSemantic(() => fixture.Tools.SymbolAt(
                "Core/Use.fs", 2, 5, timeoutMs: 60_000)));
            Assert.Equal("fsharp_semantic_package_assets_stale",
                response.GetProperty("error").GetString());
            Assert.False(response.TryGetProperty("found", out JsonElement found) &&
                         found.ValueKind == JsonValueKind.True);
        }
        finally
        {
            Cleanup(root);
        }
    }

    [Fact]
    public void PackageReferenceFailsClosedWhenAssetsAreMissingStaleOrMismatched()
    {
        static void WriteConsumer(string root)
        {
            WriteProject(root, "Core/Core.fsproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                  </PropertyGroup>
                  <ItemGroup>
                    <Compile Include="Use.fs" />
                    <PackageReference Include="System.IO.Hashing" Version="10.0.10" />
                  </ItemGroup>
                </Project>
                """);
            WriteProject(root, "Core/Use.fs", "module Use\nlet value = 1\n");
        }

        string packagesRoot = Environment.GetEnvironmentVariable("NUGET_PACKAGES") is
        { Length: > 0 } configuredPackages
            ? configuredPackages
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".nuget", "packages");
        foreach ((string scenario, Action<string> arrange, string expected) in new[]
                 {
                     ("missing", (Action<string>)(_ => { }),
                         "fsharp_semantic_package_assets_unavailable"),
                     ("stale", root =>
                     {
                         WritePackageAssets(root, "Core/Core.fsproj", "net10.0",
                             "System.IO.Hashing", "10.0.10", packagesRoot,
                             "lib/net10.0/System.IO.Hashing.dll");
                         File.SetLastWriteTimeUtc(Path.Combine(root, "Core", "obj",
                                 "project.assets.json"),
                             File.GetLastWriteTimeUtc(Path.Combine(root, "Core", "Core.fsproj"))
                                 .AddMinutes(-1));
                     }, "fsharp_semantic_package_assets_stale"),
                     ("equal-mtime", root =>
                     {
                         WritePackageAssets(root, "Core/Core.fsproj", "net10.0",
                             "System.IO.Hashing", "10.0.10", packagesRoot,
                             "lib/net10.0/System.IO.Hashing.dll");
                         File.SetLastWriteTimeUtc(Path.Combine(root, "Core", "obj",
                                 "project.assets.json"),
                             File.GetLastWriteTimeUtc(Path.Combine(root, "Core", "Core.fsproj")));
                     }, "fsharp_semantic_package_assets_stale"),
                     ("mismatched", root => WritePackageAssets(root, "Core/Core.fsproj",
                             "net10.0", "Different.Package", "10.0.10", packagesRoot,
                             "lib/net10.0/System.IO.Hashing.dll"),
                         "fsharp_semantic_package_assets_stale"),
                     ("mismatched-version", root => WritePackageAssets(root,
                             "Core/Core.fsproj", "net10.0", "System.IO.Hashing", "9.9.9",
                             packagesRoot, "lib/net10.0/System.IO.Hashing.dll"),
                         "fsharp_semantic_package_assets_stale"),
                     ("unsafe-path", root => WritePackageAssets(root, "Core/Core.fsproj",
                             "net10.0", "System.IO.Hashing", "10.0.10", packagesRoot,
                             "../../outside/System.IO.Hashing.dll"),
                         "fsharp_semantic_package_assets_unavailable"),
                     ("untrusted-root", root => WritePackageAssets(root, "Core/Core.fsproj",
                             "net10.0", "System.IO.Hashing", "10.0.10",
                             Path.GetPathRoot(root)!, "System.IO.Hashing.dll"),
                         "fsharp_semantic_package_assets_unavailable"),
                 })
        {
            string root = Directory.CreateTempSubdirectory(
                $"codenav-fsharp-semantic-package-{scenario}").FullName;
            try
            {
                WriteConsumer(root);
                arrange(root);
                using var fixture = Fixture.Create(root);
                JsonElement response = Parse(CallSemantic(() => fixture.Tools.SymbolAt(
                    "Core/Use.fs", 2, 5, timeoutMs: 60_000)));
                Assert.Equal(expected, response.GetProperty("error").GetString());
                Assert.False(response.TryGetProperty("found", out JsonElement found) &&
                             found.ValueKind == JsonValueKind.True);
            }
            finally
            {
                Cleanup(root);
            }
        }
    }

    [Fact]
    public void ChangedPackageCompileAssetInvalidatesCompletedFSharpCheck()
    {
        string root = Directory.CreateTempSubdirectory(
            "codenav-fsharp-semantic-package-race").FullName;
        try
        {
            const string packageId = "System.IO.Hashing";
            const string packageVersion = "10.0.10";
            string restoredPackages = Environment.GetEnvironmentVariable("NUGET_PACKAGES") is
            { Length: > 0 } configuredPackages
                ? configuredPackages
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".nuget", "packages");
            string restoredDll = Path.Combine(restoredPackages, packageId.ToLowerInvariant(),
                packageVersion, "lib", "net10.0", "System.IO.Hashing.dll");
            string privatePackages = Path.Combine(root, "Packages");
            string packageDll = Path.Combine(privatePackages, packageId.ToLowerInvariant(),
                packageVersion, "lib", "net10.0", "System.IO.Hashing.dll");
            Directory.CreateDirectory(Path.GetDirectoryName(packageDll)!);
            File.Copy(restoredDll, packageDll);

            WriteProject(root, "Core/Core.fsproj", """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
                  </PropertyGroup>
                  <ItemGroup>
                    <Compile Include="Use.fs" />
                    <PackageReference Include="System.IO.Hashing" Version="10.0.10" />
                  </ItemGroup>
                </Project>
                """);
            WriteProject(root, "Core/Use.fs", """
                namespace PackageConsumer
                open System.IO.Hashing
                module Use =
                    let hasher = XxHash64()
                """);
            WritePackageAssets(root, "Core/Core.fsproj", "net10.0", packageId,
                packageVersion, privatePackages, "lib/net10.0/System.IO.Hashing.dll");

            using var fixture = Fixture.Create(root);
            fixture.Semantic.FSharpSemanticSnapshotCapturedForTest = () =>
            {
                using var stream = new FileStream(packageDll, FileMode.Open,
                    FileAccess.ReadWrite, FileShare.Read);
                stream.Position = 32;
                int original = stream.ReadByte();
                Assert.True(original >= 0);
                stream.Position = 32;
                stream.WriteByte((byte)(original ^ 1));
            };
            string raw = CallSemantic(() => fixture.Tools.References(
                path: "Core/Use.fs", line: 4, column: 19, mode: "semantic",
                timeoutMs: 60_000));
            JsonElement response = Parse(raw);
            Assert.Equal("fsharp_semantic_reference_changed",
                response.GetProperty("error").GetString());
            Assert.False(response.TryGetProperty("totalReferences", out _));
            Assert.Contains("fsharp_package_references_snapshotted",
                response.GetProperty("partialReason").GetString());
        }
        finally
        {
            Cleanup(root);
        }
    }
}
