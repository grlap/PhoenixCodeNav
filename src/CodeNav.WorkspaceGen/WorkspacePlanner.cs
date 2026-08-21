using System.Security.Cryptography;
using System.Text;

namespace CodeNav.WorkspaceGen;

/// <summary>
/// Owns: all shape decisions for a synthetic workspace — project DAG, type graph,
/// solution membership, style mix. Fully deterministic for a given (target, seed).
/// Does not own: text emission (CodeEmitter/ProjectEmitter).
/// </summary>
internal sealed class WorkspacePlanner
{
    private static readonly PackageSpec[] ProdPackages =
    {
        new("Newtonsoft.Json", "13.0.3", "Newtonsoft.Json"),
        new("log4net", "2.0.15", "log4net"),
        new("Dapper", "2.0.123", "Dapper"),
        new("EntityFramework", "6.4.4", "EntityFramework"),
        new("AutoMapper", "10.1.1", "AutoMapper"),
        new("Polly", "7.2.4", "Polly"),
        new("System.ValueTuple", "4.5.0", "System.ValueTuple"),
    };

    private static readonly PackageSpec Xunit = new("xunit", "2.4.2", "xunit.core");
    private static readonly PackageSpec Nunit = new("NUnit", "3.13.3", "nunit.framework");
    private static readonly PackageSpec MsTest = new("MSTest.TestFramework", "2.2.10", "Microsoft.VisualStudio.TestPlatform.TestFramework");
    private static readonly PackageSpec Moq = new("Moq", "4.18.4", "Moq");

    private readonly Random _rng;
    private readonly int _target;
    private readonly int _seed;
    private readonly double _density;
    private readonly List<ProjectSpec> _projects = new();
    private readonly List<SolutionSpec> _solutions = new();
    private readonly Dictionary<string, double> _productLegacyRatio = new();
    private readonly Dictionary<string, HashSet<string>> _productSubsystems = new();
    private readonly HashSet<ProjectSpec> _orphans = new();
    private ProjectSpec _common = null!;
    private InterfaceSpec _clock = null!;

    public WorkspacePlanner(int targetProjects, int seed, double density = 1.0)
    {
        _target = targetProjects;
        _seed = seed;
        _density = density;
        _rng = new Random(seed);
    }

    /// <summary>Scales a planned type count by workspace density (files per project).</summary>
    private int Dense(int min, int maxExclusive) => Math.Max(1, (int)(_rng.Next(min, maxExclusive) * _density));

    public WorkspaceSpec Plan()
    {
        CreatePlatformCommon();

        // Platform gets real subsystems too (Data, Web, Messaging, Testing feel).
        foreach (var sub in new[] { "Data", "Web", "Messaging" })
        {
            if (_projects.Count >= _target) break;
            CreateSubsystem("Platform", forcedSubsystem: sub);
        }

        int productIdx = 0;
        while (_projects.Count < _target)
        {
            var product = NameBank.Products[productIdx % NameBank.Products.Length];
            productIdx++;
            CreateSubsystem(product, forcedSubsystem: null);
        }

        MarkOrphans();
        PlanSolutions();

        return new WorkspaceSpec
        {
            Seed = _seed,
            RootNs = "Acme",
            Projects = _projects,
            Solutions = _solutions,
            PlatformCommon = _common,
        };
    }

    public IReadOnlySet<ProjectSpec> Orphans => _orphans;

    // ---------------------------------------------------------------- platform

    private void CreatePlatformCommon()
    {
        _common = NewProject("Platform", "Common", Layer.Contracts, ProjectStyle.Sdk, "Acme.Platform.Common");
        // Well-known hot types are emitted from fixed templates keyed by name.
        foreach (var wk in new[] { "Guard", "Result", "EntityBase", "AcmeException" })
        {
            _common.Classes.Add(new ClassSpec { Name = wk, Owner = _common, Kind = ClassKind.WellKnown });
        }

        _clock = new InterfaceSpec
        {
            Name = "IClock",
            Owner = _common,
            Methods = new List<MethodSpec>
            {
                new() { Name = "GetUtcNow", Return = TypeRef.Prim("DateTime"), Params = new() },
            },
        };
        _common.Interfaces.Add(_clock);

        var sysClock = new ClassSpec { Name = "SystemClock", Owner = _common, Kind = ClassKind.Service, Implements = _clock };
        _common.Classes.Add(sysClock);
        _projects.Add(_common);
    }

    // ---------------------------------------------------------------- subsystems

    private void CreateSubsystem(string product, string? forcedSubsystem)
    {
        var subs = _productSubsystems.TryGetValue(product, out var set)
            ? set
            : _productSubsystems[product] = new HashSet<string>(StringComparer.Ordinal);

        string subsystem = forcedSubsystem ?? NameBank.Pick(_rng, NameBank.Subsystems);
        while (!subs.Add(subsystem))
        {
            subsystem = NameBank.Pick(_rng, NameBank.Subsystems) + NameBank.Pick(_rng, NameBank.Nouns);
        }

        var style = () => PickStyle(product);
        string baseName = $"Acme.{product}.{subsystem}";

        var contracts = NewProject(product, subsystem, Layer.Contracts, style(), $"{baseName}.Contracts");
        contracts.Refs.Add(_common);
        PopulateContracts(contracts);
        _projects.Add(contracts);

        ProjectSpec? domain = null, application = null, infrastructure = null, api = null;

        if (Chance(85))
        {
            domain = NewProject(product, subsystem, Layer.Domain, style(), $"{baseName}.Domain");
            domain.Refs.Add(_common);
            domain.Refs.Add(contracts);
            PopulateDomain(domain);
            _projects.Add(domain);
        }

        if (Chance(90))
        {
            application = NewProject(product, subsystem, Layer.Application, style(), $"{baseName}.Application");
            application.Refs.Add(_common);
            application.Refs.Add(contracts);
            if (domain is not null && Chance(80)) application.Refs.Add(domain);
            AddCrossRefs(application, product);
            PopulateApplication(application, contracts);
            _projects.Add(application);
        }

        if (Chance(60))
        {
            infrastructure = NewProject(product, subsystem, Layer.Infrastructure, style(), $"{baseName}.Infrastructure");
            infrastructure.Refs.Add(_common);
            infrastructure.Refs.Add(contracts);
            if (domain is not null && Chance(70)) infrastructure.Refs.Add(domain);
            AddCrossRefs(infrastructure, product);
            PopulateInfrastructure(infrastructure);
            _projects.Add(infrastructure);
        }

        if (Chance(45))
        {
            api = NewProject(product, subsystem, Layer.Api, style(), $"{baseName}.Api");
            api.Refs.Add(_common);
            api.Refs.Add(contracts);
            if (application is not null) api.Refs.Add(application);
            PopulateApi(api, contracts, application);
            _projects.Add(api);
        }

        // Tests for up to two interesting production projects.
        foreach (var target in new[] { application, domain })
        {
            if (target is null || _projects.Count >= _target + 4) continue;
            int chance = target.Layer == Layer.Application ? 65 : 30;
            if (!Chance(chance)) continue;

            var tests = NewProject(product, subsystem, Layer.Tests, style(), $"{target.Name}.Tests");
            tests.Refs.Add(target);
            foreach (var r in target.Refs.Where(r => !tests.Refs.Contains(r))) tests.Refs.Add(r);
            PickTestFramework(tests);
            PopulateTests(tests, target);
            _projects.Add(tests);
        }
    }

    private void AddCrossRefs(ProjectSpec project, string product)
    {
        // Sibling-subsystem contracts within the product.
        var siblings = _projects.Where(p =>
            p.Product == product && p.Layer == Layer.Contracts && p.Subsystem != project.Subsystem).ToList();
        if (siblings.Count > 0 && Chance(30))
        {
            for (int i = 0, n = _rng.Next(1, 3); i < n && siblings.Count > 0; i++)
            {
                var pick = siblings[_rng.Next(siblings.Count)];
                siblings.Remove(pick);
                if (!project.Refs.Contains(pick)) project.Refs.Add(pick);
            }
        }

        // Cross-product contracts (the enterprise tangle).
        if (Chance(15))
        {
            var foreign = _projects.Where(p =>
                p.Product != product && p.Product != "Platform" && p.Layer == Layer.Contracts).ToList();
            if (foreign.Count > 0)
            {
                var pick = foreign[_rng.Next(foreign.Count)];
                if (!project.Refs.Contains(pick)) project.Refs.Add(pick);
            }
        }
    }

    // ---------------------------------------------------------------- population

    private void PopulateContracts(ProjectSpec p)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);

        for (int i = 0, n = Dense(1, 4); i < n; i++)
        {
            string name = Unique(used, () => NameBank.Pick(_rng, NameBank.Nouns) + NameBank.Pick(_rng, NameBank.EnumSuffixes));
            var members = new List<string> { "None" };
            for (int m = 0, k = _rng.Next(3, 8); m < k; m++)
            {
                string mem = NameBank.Pick(_rng, NameBank.EnumMembers);
                if (!members.Contains(mem)) members.Add(mem);
            }
            p.Enums.Add(new EnumSpec { Name = name, Owner = p, Members = members });
        }

        for (int i = 0, n = Dense(3, 10); i < n; i++)
        {
            string name = Unique(used, () => NameBank.Pick(_rng, NameBank.Nouns) + PickDtoSuffix());
            var props = new List<(TypeRef, string)>();
            var propNames = new HashSet<string>(StringComparer.Ordinal);
            for (int j = 0, k = _rng.Next(3, 10); j < k; j++)
            {
                string pn = Unique(propNames, () => NameBank.Pick(_rng, NameBank.Nouns) + PickPropSuffix());
                props.Add((PickPrimitiveOrEnum(p), pn));
            }
            p.Dtos.Add(new DtoSpec { Name = name, Owner = p, Props = props });
        }

        for (int i = 0, n = Dense(2, 7); i < n; i++)
        {
            string name = Unique(used, () => "I" + NameBank.Pick(_rng, NameBank.Nouns) + NameBank.Pick(_rng, NameBank.ServiceSuffixes));
            var methods = MakeMethods(p, _rng.Next(2, 7), allowOverloads: true);
            p.Interfaces.Add(new InterfaceSpec { Name = name, Owner = p, Methods = methods });
        }
    }

    private void PopulateDomain(ProjectSpec p)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0, n = Dense(3, 11); i < n; i++)
        {
            string name = Unique(used, () => NameBank.Pick(_rng, NameBank.Nouns) + (Chance(30) ? NameBank.Pick(_rng, NameBank.Nouns) : ""));
            var entity = new ClassSpec { Name = name, Owner = p, Kind = ClassKind.Entity, BaseType = new TypeRef("EntityBase", _common.Ns) };
            var propNames = new HashSet<string>(StringComparer.Ordinal);
            for (int j = 0, k = _rng.Next(3, 9); j < k; j++)
            {
                string pn = Unique(propNames, () => NameBank.Pick(_rng, NameBank.Nouns) + PickPropSuffix());
                entity.Props.Add((PickPrimitive(), pn));
            }
            // Mutator methods over its own props (type-consistent by construction).
            foreach (var (type, propName) in entity.Props.Take(_rng.Next(1, 4)))
            {
                entity.OwnMethods.Add(new MethodSpec
                {
                    Name = "Update" + propName,
                    Return = TypeRef.Void,
                    Params = new() { (type, "value") },
                });
            }
            p.Classes.Add(entity);
        }

        for (int i = 0, n = Dense(1, 4); i < n; i++)
        {
            string name = Unique(used, () => NameBank.Pick(_rng, NameBank.Nouns) + NameBank.Pick(_rng, NameBank.ServiceSuffixes));
            var svc = new ClassSpec { Name = name, Owner = p, Kind = ClassKind.Service };
            if (Chance(25)) svc.Deps.Add((_clock, "_clock"));
            svc.OwnMethods.AddRange(MakeMethods(p, _rng.Next(2, 6), allowOverloads: false));
            p.Classes.Add(svc);
        }
    }

    private void PopulateApplication(ProjectSpec p, ProjectSpec contracts)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        var ifacePool = ReferencedInterfaces(p);

        foreach (var iface in contracts.Interfaces.Where(_ => Chance(70)))
        {
            string name = Unique(used, () => iface.Name[1..] + NameBank.Pick(_rng, NameBank.Nouns), iface.Name[1..]);
            var impl = new ClassSpec { Name = name, Owner = p, Kind = ClassKind.Service, Implements = iface };
            var fieldNames = new HashSet<string>(StringComparer.Ordinal);
            for (int d = 0, n = _rng.Next(1, 4); d < n && ifacePool.Count > 0; d++)
            {
                var dep = ifacePool[_rng.Next(ifacePool.Count)];
                if (impl.Deps.Any(x => x.Iface == dep)) continue;
                string field = Unique(fieldNames, () => "_" + char.ToLowerInvariant(dep.Name[1]) + dep.Name[2..]);
                impl.Deps.Add((dep, field));
            }
            if (Chance(20)) impl.Deps.Add((_clock, "_clock"));
            impl.OwnMethods.AddRange(MakeMethods(p, _rng.Next(0, 4), allowOverloads: false));
            impl.IsPartial = Chance(6);
            if (Chance(1)) impl.ExtraMethodBurst = _rng.Next(80, 160);
            p.Classes.Add(impl);
        }
    }

    private void PopulateInfrastructure(ProjectSpec p)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        var ifacePool = ReferencedInterfaces(p);
        for (int i = 0, n = Dense(2, 7); i < n; i++)
        {
            string name = Unique(used, () => NameBank.Pick(_rng, NameBank.Nouns) + NameBank.Pick(_rng, NameBank.RepoSuffixes));
            var repo = new ClassSpec { Name = name, Owner = p, Kind = ClassKind.Repository };
            if (ifacePool.Count > 0 && Chance(40))
            {
                var dep = ifacePool[_rng.Next(ifacePool.Count)];
                repo.Deps.Add((dep, "_" + char.ToLowerInvariant(dep.Name[1]) + dep.Name[2..]));
            }
            repo.OwnMethods.AddRange(MakeMethods(p, _rng.Next(2, 7), allowOverloads: false));
            repo.IsPartial = Chance(4);
            if (Chance(1)) repo.ExtraMethodBurst = _rng.Next(80, 140);
            p.Classes.Add(repo);
        }
    }

    private void PopulateApi(ProjectSpec p, ProjectSpec contracts, ProjectSpec? application)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        var implemented = application is null
            ? new List<InterfaceSpec>()
            : application.Classes.Where(c => c.Implements is not null).Select(c => c.Implements!).ToList();

        foreach (var iface in implemented.Where(_ => Chance(70)))
        {
            string name = Unique(used, () => iface.Name[1..].Replace("Service", "") + "Controller");
            var ctrl = new ClassSpec { Name = name, Owner = p, Kind = ClassKind.Controller };
            ctrl.Deps.Add((iface, "_service"));
            // Delegating methods mirror a subset of the interface 1:1 (compilable by construction).
            foreach (var m in iface.Methods.Where(_ => Chance(75)))
            {
                ctrl.OwnMethods.Add(m);
            }
            p.Classes.Add(ctrl);
        }
    }

    private void PopulateTests(ProjectSpec p, ProjectSpec target)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var cls in target.Classes.Where(c => c.Kind is ClassKind.Service && Chance(70)))
        {
            string name = Unique(used, () => cls.Name + "Tests");
            var test = new ClassSpec { Name = name, Owner = p, Kind = ClassKind.Test, TestTarget = cls };
            int count = _rng.Next(2, 7);
            var methods = (cls.Implements?.Methods ?? cls.OwnMethods).Take(count).ToList();
            foreach (var m in methods)
            {
                test.OwnMethods.Add(new MethodSpec
                {
                    Name = "Should" + m.Name + NameBank.Pick(_rng, NameBank.Nouns),
                    Return = TypeRef.Void,
                    Params = new(),
                });
            }
            p.Classes.Add(test);
        }
    }

    // ---------------------------------------------------------------- helpers

    private List<InterfaceSpec> ReferencedInterfaces(ProjectSpec p) =>
        p.Refs.Where(r => r != _common).SelectMany(r => r.Interfaces)
            .Concat(p.Interfaces)
            .ToList();

    private List<MethodSpec> MakeMethods(ProjectSpec p, int count, bool allowOverloads)
    {
        var methods = new List<MethodSpec>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < count; i++)
        {
            string name = NameBank.Pick(_rng, NameBank.Verbs) + NameBank.Pick(_rng, NameBank.Nouns);
            bool overload = allowOverloads && names.Contains(name) && Chance(60);
            if (!overload) name = Unique(names, () => NameBank.Pick(_rng, NameBank.Verbs) + NameBank.Pick(_rng, NameBank.Nouns), name);

            var pars = new List<(TypeRef, string)>();
            var parNames = new HashSet<string>(StringComparer.Ordinal);
            int parCount = _rng.Next(0, 4) + (overload ? 1 : 0);
            for (int j = 0; j < parCount; j++)
            {
                string pn = Unique(parNames, () =>
                {
                    var noun = NameBank.Pick(_rng, NameBank.Nouns);
                    return char.ToLowerInvariant(noun[0]) + noun[1..];
                });
                pars.Add((PickParamType(p), pn));
            }

            TypeRef ret = _rng.Next(100) switch
            {
                < 22 => TypeRef.Void,
                < 34 => TypeRef.Prim("bool"),
                < 44 => TypeRef.Prim("int"),
                < 54 => TypeRef.Prim("string"),
                < 64 => new TypeRef("Result", _common.Ns),
                _ => PickDtoOrPrimitive(p),
            };
            methods.Add(new MethodSpec { Name = name, Return = ret, Params = pars });
        }
        return methods;
    }

    private TypeRef PickParamType(ProjectSpec p) => _rng.Next(100) switch
    {
        < 28 => TypeRef.Prim("string"),
        < 44 => TypeRef.Prim("int"),
        < 52 => TypeRef.Prim("decimal"),
        < 60 => TypeRef.Prim("bool"),
        < 68 => TypeRef.Prim("DateTime"),
        < 76 => TypeRef.Prim("Guid"),
        < 88 => PickDtoOrPrimitive(p),
        _ => PickEnumOrPrimitive(p),
    };

    private TypeRef PickDtoOrPrimitive(ProjectSpec p)
    {
        var dtos = OwnAndReferencedDtos(p);
        return dtos.Count > 0 ? dtos[_rng.Next(dtos.Count)].Ref : TypeRef.Prim("string");
    }

    private TypeRef PickEnumOrPrimitive(ProjectSpec p)
    {
        var enums = p.Enums.Concat(p.Refs.SelectMany(r => r.Enums)).ToList();
        return enums.Count > 0 ? enums[_rng.Next(enums.Count)].Ref : TypeRef.Prim("int");
    }

    private TypeRef PickPrimitiveOrEnum(ProjectSpec p)
    {
        if (p.Enums.Count > 0 && Chance(18)) return p.Enums[_rng.Next(p.Enums.Count)].Ref;
        return PickPrimitive();
    }

    private TypeRef PickPrimitive() => _rng.Next(100) switch
    {
        < 32 => TypeRef.Prim("string"),
        < 52 => TypeRef.Prim("int"),
        < 64 => TypeRef.Prim("decimal"),
        < 76 => TypeRef.Prim("bool"),
        < 88 => TypeRef.Prim("DateTime"),
        _ => TypeRef.Prim("Guid"),
    };

    private List<DtoSpec> OwnAndReferencedDtos(ProjectSpec p) =>
        p.Dtos.Concat(p.Refs.SelectMany(r => r.Dtos)).ToList();

    private string PickDtoSuffix() => _rng.Next(100) switch
    {
        < 40 => "Dto",
        < 60 => "Request",
        < 80 => "Response",
        _ => "Summary",
    };

    private string PickPropSuffix() => _rng.Next(100) switch
    {
        < 30 => "",
        < 45 => "Id",
        < 60 => "Name",
        < 70 => "Count",
        < 80 => "Amount",
        < 90 => "Date",
        _ => "Reference",
    };

    private void PickTestFramework(ProjectSpec p)
    {
        int roll = _rng.Next(100);
        if (roll < 50) { p.TestFramework = "xunit"; p.Packages.Add(Xunit); }
        else if (roll < 80) { p.TestFramework = "nunit"; p.Packages.Add(Nunit); }
        else { p.TestFramework = "mstest"; p.Packages.Add(MsTest); }
        if (Chance(50)) p.Packages.Add(Moq);
    }

    private ProjectStyle PickStyle(string product)
    {
        if (!_productLegacyRatio.TryGetValue(product, out double ratio))
        {
            ratio = product == "Platform" ? 0.4 : new[] { 0.9, 0.7, 0.4 }[_rng.Next(3)];
            _productLegacyRatio[product] = ratio;
        }
        return _rng.NextDouble() < ratio ? ProjectStyle.Legacy : ProjectStyle.Sdk;
    }

    private ProjectSpec NewProject(string product, string subsystem, Layer layer, ProjectStyle style, string name)
    {
        string relDir = layer == Layer.Tests
            ? $"tests/{product}/{subsystem}/{name}"
            : $"src/{product}/{subsystem}/{name}";
        var project = new ProjectSpec
        {
            Name = name,
            Product = product,
            Subsystem = subsystem,
            Layer = layer,
            Style = style,
            RelDir = relDir,
            ProjectGuid = GuidFromName(name),
        };
        if (style == ProjectStyle.Legacy && !project.IsTest)
        {
            foreach (var pkg in ProdPackages.Where(_ => Chance(30)).Take(4)) project.Packages.Add(pkg);
        }
        else if (style == ProjectStyle.Sdk && !project.IsTest)
        {
            foreach (var pkg in ProdPackages.Where(_ => Chance(20)).Take(3)) project.Packages.Add(pkg);
        }
        return project;
    }

    private void MarkOrphans()
    {
        foreach (var p in _projects.Where(p => p != _common && Chance(2)))
        {
            _orphans.Add(p);
        }
    }

    private void PlanSolutions()
    {
        var byProduct = _projects.Where(p => !_orphans.Contains(p)).GroupBy(p => p.Product).ToList();
        var platformProjects = _projects.Where(p => p.Product == "Platform" && !_orphans.Contains(p)).ToList();

        foreach (var group in byProduct)
        {
            if (group.Key == "Platform") continue;
            var members = group.Where(_ => !Chance(3)).ToList();
            members.Add(_common);
            members.AddRange(platformProjects.Where(pp => pp != _common).Take(5));
            _solutions.Add(new SolutionSpec
            {
                Name = $"Acme.{group.Key}",
                RelPath = $"src/{group.Key}/Acme.{group.Key}.sln",
                Projects = members.Distinct().ToList(),
            });
        }

        _solutions.Add(new SolutionSpec
        {
            Name = "Acme.Platform",
            RelPath = "src/Platform/Acme.Platform.sln",
            Projects = platformProjects,
        });

        // Team solutions mixing products (overlapping membership).
        string[] teams = { "TeamAtlas", "TeamOrion", "TeamHelios" };
        var productNames = byProduct.Select(g => g.Key).Where(k => k != "Platform").ToList();
        foreach (var team in teams)
        {
            var picked = new HashSet<string>();
            for (int i = 0, n = _rng.Next(2, 5); i < n && productNames.Count > 0; i++)
            {
                picked.Add(productNames[_rng.Next(productNames.Count)]);
            }
            var members = _projects.Where(p => picked.Contains(p.Product) && !_orphans.Contains(p)).ToList();
            members.Add(_common);
            _solutions.Add(new SolutionSpec
            {
                Name = team,
                RelPath = $"{team}.sln",
                Projects = members.Distinct().ToList(),
            });
        }

        _solutions.Add(new SolutionSpec
        {
            Name = "Acme.Enterprise",
            RelPath = "Acme.Enterprise.sln",
            Projects = _projects.Where(p => !_orphans.Contains(p)).ToList(),
        });
    }

    private bool Chance(int percent) => _rng.Next(100) < percent;

    private static string Unique(HashSet<string> used, Func<string> make, string? first = null)
    {
        string candidate = first ?? make();
        int guard = 0;
        while (!used.Add(candidate))
        {
            candidate = make();
            if (++guard > 20)
            {
                candidate += used.Count.ToString();
            }
        }
        return candidate;
    }

    private static Guid GuidFromName(string name) =>
        new(MD5.HashData(Encoding.UTF8.GetBytes(name)).AsSpan(0, 16));
}
