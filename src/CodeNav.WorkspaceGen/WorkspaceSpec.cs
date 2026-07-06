namespace CodeNav.WorkspaceGen;

/// <summary>
/// Owns: the in-memory model of a planned synthetic workspace (projects, types, members, solutions).
/// Does not own: randomness/shape decisions (WorkspacePlanner) or file emission (CodeEmitter/ProjectEmitter).
/// </summary>
internal enum Layer { Contracts, Domain, Application, Infrastructure, Api, Tests }

internal enum ProjectStyle { Legacy, Sdk }

internal enum ClassKind { Entity, Service, Repository, Controller, StaticHelper, Test, WellKnown }

/// <summary>A type reference. Ns == null means a C# primitive/BCL keyword type (int, string, ...).</summary>
internal readonly record struct TypeRef(string Name, string? Ns)
{
    public bool IsPrimitive => Ns is null;
    public static TypeRef Prim(string name) => new(name, null);
    public static readonly TypeRef Void = Prim("void");
}

internal sealed record PackageSpec(string Id, string Version, string AssemblyName);

internal sealed class MethodSpec
{
    public required string Name { get; init; }
    public required TypeRef Return { get; init; }
    public required List<(TypeRef Type, string Name)> Params { get; init; }
}

internal sealed class EnumSpec
{
    public required string Name { get; init; }
    public required ProjectSpec Owner { get; init; }
    public required List<string> Members { get; init; }
    public string Ns => Owner.Ns;
    public TypeRef Ref => new(Name, Ns);
}

internal sealed class DtoSpec
{
    public required string Name { get; init; }
    public required ProjectSpec Owner { get; init; }
    public required List<(TypeRef Type, string Name)> Props { get; init; }
    public string Ns => Owner.Ns;
    public TypeRef Ref => new(Name, Ns);
}

internal sealed class InterfaceSpec
{
    public required string Name { get; init; }
    public required ProjectSpec Owner { get; init; }
    public required List<MethodSpec> Methods { get; init; }
    public string Ns => Owner.Ns;
    public TypeRef Ref => new(Name, Ns);
}

internal sealed class ClassSpec
{
    public required string Name { get; init; }
    public required ProjectSpec Owner { get; init; }
    public required ClassKind Kind { get; init; }

    /// <summary>Interface this class implements; its methods are emitted as public members.</summary>
    public InterfaceSpec? Implements { get; set; }

    /// <summary>Constructor-injected dependencies (interface + backing field name).</summary>
    public List<(InterfaceSpec Iface, string Field)> Deps { get; } = new();

    /// <summary>Methods beyond the implemented interface's.</summary>
    public List<MethodSpec> OwnMethods { get; } = new();

    /// <summary>Auto-properties for entities/DTO-like classes.</summary>
    public List<(TypeRef Type, string Name)> Props { get; } = new();

    /// <summary>Split emission across two partial files when true.</summary>
    public bool IsPartial { get; set; }

    /// <summary>Extra filler methods to force a large (1500+ line) file.</summary>
    public int ExtraMethodBurst { get; set; }

    /// <summary>Base type (e.g. EntityBase) or null.</summary>
    public TypeRef? BaseType { get; set; }

    /// <summary>For Kind == Test: the production class under test.</summary>
    public ClassSpec? TestTarget { get; set; }

    public string Ns => Owner.Ns;
    public TypeRef Ref => new(Name, Ns);
}

internal sealed class ProjectSpec
{
    public required string Name { get; init; }           // also assembly name + root namespace
    public required string Product { get; init; }
    public required string Subsystem { get; init; }
    public required Layer Layer { get; init; }
    public required ProjectStyle Style { get; init; }
    public required string RelDir { get; init; }         // e.g. src/Billing/Invoicing/Acme.Billing.Invoicing.Contracts
    public required Guid ProjectGuid { get; init; }

    public List<ProjectSpec> Refs { get; } = new();
    public List<PackageSpec> Packages { get; } = new();

    public List<EnumSpec> Enums { get; } = new();
    public List<DtoSpec> Dtos { get; } = new();
    public List<InterfaceSpec> Interfaces { get; } = new();
    public List<ClassSpec> Classes { get; } = new();

    /// <summary>"xunit" | "nunit" | "mstest" for test projects, else null.</summary>
    public string? TestFramework { get; set; }

    public bool IsTest => Layer == Layer.Tests;
    public string Ns => Name;
    public string CsprojRelPath => $"{RelDir}/{Name}.csproj";
}

internal sealed class SolutionSpec
{
    public required string Name { get; init; }
    public required string RelPath { get; init; }        // e.g. src/Billing/Billing.sln
    public required List<ProjectSpec> Projects { get; init; }
}

internal sealed class WorkspaceSpec
{
    public required int Seed { get; init; }
    public required string RootNs { get; init; }         // "Acme"
    public required List<ProjectSpec> Projects { get; init; }
    public required List<SolutionSpec> Solutions { get; init; }
    public required ProjectSpec PlatformCommon { get; init; }

    // Well-known hot types (defined in PlatformCommon, referenced everywhere).
    public TypeRef GuardType => new("Guard", PlatformCommon.Ns);
    public TypeRef ResultType => new("Result", PlatformCommon.Ns);
    public TypeRef EntityBaseType => new("EntityBase", PlatformCommon.Ns);
    public TypeRef ExceptionType => new("AcmeException", PlatformCommon.Ns);
}
