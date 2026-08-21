using System.Text;

namespace CodeNav.WorkspaceGen;

/// <summary>
/// Owns: turning type specs into C# source text (C# 7.3-compatible so net472 projects
/// stay compilable). Deterministic per project. Does not own: shape decisions
/// (WorkspacePlanner) or file-system writes and project files (ProjectEmitter).
/// </summary>
internal sealed class CodeEmitter
{
    private readonly WorkspaceSpec _ws;
    private readonly Dictionary<TypeRef, EnumSpec> _enums = new();
    private readonly Dictionary<TypeRef, DtoSpec> _dtos = new();

    public CodeEmitter(WorkspaceSpec ws)
    {
        _ws = ws;
        foreach (var p in ws.Projects)
        {
            foreach (var e in p.Enums) _enums[e.Ref] = e;
            foreach (var d in p.Dtos) _dtos[d.Ref] = d;
        }
    }

    /// <summary>Returns (path relative to project dir, content) pairs.</summary>
    public List<(string RelPath, string Content)> EmitProjectSources(ProjectSpec p)
    {
        var rng = new Random(BitConverter.ToInt32(p.ProjectGuid.ToByteArray(), 0));
        var files = new List<(string, string)>();

        if (p.Enums.Count > 0)
        {
            files.Add(("Enums.cs", EmitEnumsFile(p)));
        }

        for (int i = 0; i < p.Dtos.Count; i += 4)
        {
            var chunk = p.Dtos.Skip(i).Take(4).ToList();
            string name = i == 0 ? "Models.cs" : $"Models{i / 4 + 1}.cs";
            files.Add((name, EmitDtosFile(p, chunk, rng)));
        }

        foreach (var iface in p.Interfaces)
        {
            files.Add(($"{iface.Name}.cs", EmitInterfaceFile(iface, rng)));
        }

        foreach (var cls in p.Classes)
        {
            if (cls.Kind == ClassKind.WellKnown)
            {
                files.Add(($"{cls.Name}.cs", EmitWellKnown(cls)));
            }
            else if (cls.Kind == ClassKind.Test)
            {
                files.Add(($"{cls.Name}.cs", EmitTestFile(cls, rng)));
            }
            else if (cls.IsPartial)
            {
                files.Add(($"{cls.Name}.cs", EmitClassFile(cls, rng, PartialPart.Main)));
                files.Add(($"{cls.Name}.Operations.cs", EmitClassFile(cls, rng, PartialPart.Operations)));
            }
            else
            {
                files.Add(($"{cls.Name}.cs", EmitClassFile(cls, rng, PartialPart.None)));
            }
        }

        if (p.Layer == Layer.Api)
        {
            files.Add(($"{p.Subsystem}ApiClient.g.cs", EmitGeneratedClient(p, rng)));
        }
        if (p.Layer == Layer.Infrastructure)
        {
            files.Add(($"{p.Subsystem}Schema.Designer.cs", EmitDesignerSchema(p, rng)));
        }
        if (p.Style == ProjectStyle.Legacy)
        {
            files.Add(("Properties/AssemblyInfo.cs", EmitAssemblyInfo(p)));
        }

        return files;
    }

    private enum PartialPart { None, Main, Operations }

    // ---------------------------------------------------------------- files

    private string EmitEnumsFile(ProjectSpec p)
    {
        var w = new Writer();
        w.Header(new[] { "System" }, p.Ns);
        for (int i = 0; i < p.Enums.Count; i++)
        {
            var e = p.Enums[i];
            if (i > 0) w.Blank();
            w.Line($"public enum {e.Name}");
            w.Open();
            foreach (var m in e.Members) w.Line($"{m},");
            w.Close();
        }
        w.End();
        return w.ToString();
    }

    private string EmitDtosFile(ProjectSpec p, List<DtoSpec> dtos, Random rng)
    {
        var usings = new SortedSet<string>(StringComparer.Ordinal) { "System" };
        foreach (var d in dtos)
            foreach (var (t, _) in d.Props)
            {
                AddUsing(usings, t, p.Ns);
            }

        var w = new Writer();
        w.Header(usings, p.Ns);
        for (int i = 0; i < dtos.Count; i++)
        {
            var d = dtos[i];
            if (i > 0) w.Blank();
            if (rng.Next(100) < 35)
            {
                w.Line("/// <summary>");
                w.Line($"/// Transfer shape for {Humanize(d.Name)} exchanges.");
                w.Line("/// </summary>");
            }
            w.Line($"public class {d.Name}");
            w.Open();
            foreach (var (t, name) in d.Props)
            {
                w.Line($"public {t.Name} {name} {{ get; set; }}");
            }
            w.Close();
        }
        w.End();
        return w.ToString();
    }

    private string EmitInterfaceFile(InterfaceSpec iface, Random rng)
    {
        var usings = new SortedSet<string>(StringComparer.Ordinal) { "System" };
        foreach (var m in iface.Methods)
        {
            AddUsing(usings, m.Return, iface.Ns);
            foreach (var (t, _) in m.Params) AddUsing(usings, t, iface.Ns);
        }

        var w = new Writer();
        w.Header(usings, iface.Ns);
        if (rng.Next(100) < 40)
        {
            w.Line("/// <summary>");
            w.Line($"/// Contract for {Humanize(iface.Name[1..])} operations.");
            w.Line("/// </summary>");
        }
        w.Line($"public interface {iface.Name}");
        w.Open();
        foreach (var m in iface.Methods)
        {
            w.Line($"{m.Return.Name} {m.Name}({ParamList(m)});");
        }
        w.Close();
        w.End();
        return w.ToString();
    }

    private string EmitClassFile(ClassSpec cls, Random rng, PartialPart part)
    {
        var p = cls.Owner;
        var usings = new SortedSet<string>(StringComparer.Ordinal) { "System" };
        if (cls.BaseType is { } bt) AddUsing(usings, bt, p.Ns);
        if (cls.Implements is { } impl) AddUsing(usings, impl.Ref, p.Ns);
        foreach (var (iface, _) in cls.Deps)
        {
            AddUsing(usings, iface.Ref, p.Ns);
            // Dependency-call arguments may construct foreign DTOs/enums that never
            // appear in this class's own signatures — import their namespaces too.
            foreach (var m in iface.Methods)
            {
                AddUsing(usings, m.Return, p.Ns);
                foreach (var (t, _) in m.Params) AddUsing(usings, t, p.Ns);
            }
        }
        foreach (var m in AllMethods(cls))
        {
            AddUsing(usings, m.Return, p.Ns);
            foreach (var (t, _) in m.Params) AddUsing(usings, t, p.Ns);
        }
        foreach (var (t, _) in cls.Props) AddUsing(usings, t, p.Ns);
        if (p != _ws.PlatformCommon) usings.Add(_ws.PlatformCommon.Ns); // Guard / Result / AcmeException

        var w = new Writer();
        w.Header(usings, p.Ns);

        string partial = cls.IsPartial ? "partial " : "";
        string baseSpec = cls.BaseType is { } b ? $" : {b.Name}"
            : cls.Implements is { } i2 ? $" : {i2.Name}"
            : "";
        string keyword = cls.Kind == ClassKind.StaticHelper ? "static class" : "class";

        w.Line($"public {partial}{keyword} {cls.Name}{baseSpec}");
        w.Open();

        if (part != PartialPart.Operations)
        {
            foreach (var (t, name) in cls.Props)
            {
                w.Line($"public {t.Name} {name} {{ get; set; }}");
            }
            if (cls.Props.Count > 0 && cls.Deps.Count > 0) w.Blank();

            foreach (var (iface, field) in cls.Deps)
            {
                w.Line($"private readonly {iface.Name} {field};");
            }
            if (cls.Deps.Count > 0)
            {
                w.Blank();
                var pars = cls.Deps.Select(d => $"{d.Iface.Name} {d.Field.TrimStart('_')}");
                w.Line($"public {cls.Name}({string.Join(", ", pars)})");
                w.Open();
                foreach (var (_, field) in cls.Deps)
                {
                    w.Line($"Guard.NotNull({field.TrimStart('_')}, nameof({field.TrimStart('_')}));");
                }
                foreach (var (_, field) in cls.Deps)
                {
                    w.Line($"{field} = {field.TrimStart('_')};");
                }
                w.Close();
            }

            if (cls.Implements is { } iface3)
            {
                foreach (var m in iface3.Methods)
                {
                    w.Blank();
                    EmitMethod(w, cls, m, rng);
                }
            }
        }

        if (part != PartialPart.Main)
        {
            foreach (var m in cls.OwnMethods)
            {
                w.Blank();
                EmitMethod(w, cls, m, rng);
            }
            for (int i = 0; i < cls.ExtraMethodBurst; i++)
            {
                w.Blank();
                EmitBurstMethod(w, cls, rng, i);
            }
        }

        w.Close();
        w.End();
        return w.ToString();
    }

    private void EmitMethod(Writer w, ClassSpec cls, MethodSpec m, Random rng)
    {
        w.Line($"public {m.Return.Name} {m.Name}({ParamList(m)})");
        w.Open();

        bool isController = cls.Kind == ClassKind.Controller;
        foreach (var (t, name) in m.Params)
        {
            if (t.Name == "string") w.Line($"Guard.NotEmpty({name}, nameof({name}));");
            else if (!t.IsPrimitive && !_enums.ContainsKey(t)) w.Line($"Guard.NotNull({name}, nameof({name}));");
        }

        if (isController && cls.Deps.Count > 0)
        {
            // Controllers delegate 1:1 to the injected service (same signature).
            string call = $"{cls.Deps[0].Field}.{m.Name}({string.Join(", ", m.Params.Select(x => x.Name))})";
            w.Line(m.Return == TypeRef.Void ? $"{call};" : $"return {call};");
            w.Close();
            return;
        }

        // Entity mutators write through to their own property.
        if (cls.Kind == ClassKind.Entity && m.Name.StartsWith("Update", StringComparison.Ordinal) && m.Params.Count == 1)
        {
            w.Line($"this.{m.Name["Update".Length..]} = {m.Params[0].Name};");
            w.Close();
            return;
        }

        // Call up to two dependencies.
        int calls = cls.Deps.Count == 0 ? 0 : rng.Next(0, 3);
        for (int i = 0; i < calls; i++)
        {
            var (iface, field) = cls.Deps[rng.Next(cls.Deps.Count)];
            if (iface.Methods.Count == 0) continue;
            var dep = iface.Methods[rng.Next(iface.Methods.Count)];
            string args = string.Join(", ", dep.Params.Select(dp => ForwardOrDefault(m, dp.Type, rng)));
            string call = $"{field}.{dep.Name}({args})";

            if (dep.Return != TypeRef.Void && dep.Return == m.Return && rng.Next(100) < 40)
            {
                w.Line($"return {call};");
                w.Close();
                return;
            }
            w.Line(dep.Return == TypeRef.Void ? $"{call};" : $"_ = {call};");
        }

        var intParam = m.Params.FirstOrDefault(x => x.Type.Name == "int");
        if (intParam.Name is not null && rng.Next(100) < 25)
        {
            w.Line($"if ({intParam.Name} < 0)");
            w.Open();
            w.Line($"throw new AcmeException(\"{cls.Name}.{m.Name} rejected a negative {intParam.Name}.\");");
            w.Close();
        }

        if (m.Return != TypeRef.Void)
        {
            w.Line($"return {DefaultExpr(m.Return, rng)};");
        }
        w.Close();
    }

    private void EmitBurstMethod(Writer w, ClassSpec cls, Random rng, int index)
    {
        string noun = NameBank.Pick(rng, NameBank.Nouns);
        w.Line($"public int Compute{noun}Step{index}(int seed)");
        w.Open();
        w.Line("var accumulator = seed;");
        int steps = rng.Next(3, 8);
        for (int s = 0; s < steps; s++)
        {
            w.Line($"accumulator = unchecked(accumulator * 31 + {rng.Next(3, 9999)});");
        }
        w.Line("if (accumulator == int.MinValue)");
        w.Open();
        w.Line($"throw new AcmeException(\"{cls.Name} step {index} overflowed.\");");
        w.Close();
        w.Line("return accumulator;");
        w.Close();
    }

    private string EmitTestFile(ClassSpec test, Random rng)
    {
        var p = test.Owner;
        var target = test.TestTarget!;
        var usings = new SortedSet<string>(StringComparer.Ordinal) { "System" };
        usings.Add(target.Ns);
        foreach (var (iface, _) in target.Deps)
        {
            AddUsing(usings, iface.Ref, p.Ns);
            foreach (var m in iface.Methods)
            {
                AddUsing(usings, m.Return, p.Ns);
                foreach (var (t, _) in m.Params) AddUsing(usings, t, p.Ns);
            }
        }
        foreach (var m in AllMethods(target))
        {
            AddUsing(usings, m.Return, p.Ns);
            foreach (var (t, _) in m.Params) AddUsing(usings, t, p.Ns);
        }
        usings.Add(_ws.PlatformCommon.Ns);

        (string frameworkNs, string classAttr, string methodAttr) = p.TestFramework switch
        {
            "nunit" => ("NUnit.Framework", "[TestFixture]", "[Test]"),
            "mstest" => ("Microsoft.VisualStudio.TestTools.UnitTesting", "[TestClass]", "[TestMethod]"),
            _ => ("Xunit", "", "[Fact]"),
        };
        usings.Add(frameworkNs);

        var w = new Writer();
        w.Header(usings, p.Ns);
        if (classAttr.Length > 0) w.Line(classAttr);
        w.Line($"public class {test.Name}");
        w.Open();

        var targetMethods = AllMethods(target).ToList();
        for (int i = 0; i < test.OwnMethods.Count; i++)
        {
            if (i > 0) w.Blank();
            w.Line(methodAttr);
            w.Line($"public void {test.OwnMethods[i].Name}()");
            w.Open();
            for (int d = 0; d < target.Deps.Count; d++)
            {
                w.Line($"var stub{d} = new Stub{target.Deps[d].Iface.Name[1..]}();");
            }
            string args = string.Join(", ", Enumerable.Range(0, target.Deps.Count).Select(d => $"stub{d}"));
            w.Line($"var subject = new {target.Name}({args});");

            var m = targetMethods.Count > 0 ? targetMethods[i % targetMethods.Count] : null;
            if (m is not null)
            {
                string callArgs = string.Join(", ", m.Params.Select(x => DefaultExpr(x.Type, rng)));
                string call = $"subject.{m.Name}({callArgs})";
                EmitAssertion(w, m.Return, call);
            }
            w.Close();
        }

        foreach (var (iface, _) in target.Deps.DistinctBy(d => d.Iface))
        {
            w.Blank();
            w.Line($"private sealed class Stub{iface.Name[1..]} : {iface.Name}");
            w.Open();
            foreach (var m in iface.Methods)
            {
                if (m.Return == TypeRef.Void)
                {
                    w.Line($"public void {m.Name}({ParamList(m)}) {{ }}");
                }
                else
                {
                    w.Line($"public {m.Return.Name} {m.Name}({ParamList(m)}) {{ return {DefaultExpr(m.Return, rng)}; }}");
                }
            }
            w.Close();
        }

        w.Close();
        w.End();
        return w.ToString();
    }

    private void EmitAssertion(Writer w, TypeRef ret, string call)
    {
        if (ret == TypeRef.Void)
        {
            w.Line($"{call};");
            return;
        }
        w.Line($"var result = {call};");
        string? failCheck = ret.Name switch
        {
            "bool" => "!result",
            "int" or "decimal" => "result < 0",
            "string" => "result == null",
            "DateTime" or "Guid" => null,
            _ when _enums.ContainsKey(ret) => null,
            "Result" => "result == null || !result.IsSuccess",
            _ => "result == null",
        };
        if (failCheck is null)
        {
            w.Line("_ = result;");
            return;
        }
        w.Line($"if ({failCheck})");
        w.Open();
        w.Line("throw new InvalidOperationException(\"Assertion failed.\");");
        w.Close();
    }

    private string EmitGeneratedClient(ProjectSpec p, Random rng)
    {
        var w = new Writer();
        w.GeneratedHeader();
        w.Header(new[] { "System" }, p.Ns);
        w.Line($"public partial class {p.Subsystem}ApiClient");
        w.Open();
        w.Line("public string BaseAddress { get; set; }");
        int n = rng.Next(5, 11);
        for (int i = 0; i < n; i++)
        {
            string verb = NameBank.Pick(rng, NameBank.Verbs);
            string noun = NameBank.Pick(rng, NameBank.Nouns);
            w.Blank();
            w.Line($"public string {verb}{noun}Route()");
            w.Open();
            w.Line($"return BaseAddress + \"/api/{p.Subsystem.ToLowerInvariant()}/{noun.ToLowerInvariant()}/{verb.ToLowerInvariant()}\";");
            w.Close();
        }
        w.Close();
        w.End();
        return w.ToString();
    }

    private string EmitDesignerSchema(ProjectSpec p, Random rng)
    {
        var w = new Writer();
        w.GeneratedHeader();
        w.Header(new[] { "System" }, p.Ns);
        w.Line($"public static partial class {p.Subsystem}Schema");
        w.Open();
        int n = rng.Next(6, 16);
        for (int i = 0; i < n; i++)
        {
            string noun = NameBank.Pick(rng, NameBank.Nouns);
            w.Line($"public const string Table{noun}{i} = \"{p.Product.ToLowerInvariant()}_{noun.ToLowerInvariant()}\";");
        }
        w.Close();
        w.End();
        return w.ToString();
    }

    private string EmitAssemblyInfo(ProjectSpec p)
    {
        var sb = new StringBuilder();
        sb.AppendLine("using System.Reflection;");
        sb.AppendLine("using System.Runtime.InteropServices;");
        sb.AppendLine();
        sb.AppendLine($"[assembly: AssemblyTitle(\"{p.Name}\")]");
        sb.AppendLine($"[assembly: AssemblyProduct(\"Acme {p.Product}\")]");
        sb.AppendLine("[assembly: AssemblyCompany(\"Acme Corporation\")]");
        sb.AppendLine("[assembly: ComVisible(false)]");
        sb.AppendLine($"[assembly: Guid(\"{p.ProjectGuid:D}\")]");
        sb.AppendLine("[assembly: AssemblyVersion(\"1.0.0.0\")]");
        sb.AppendLine("[assembly: AssemblyFileVersion(\"1.0.0.0\")]");
        return sb.ToString();
    }

    private string EmitWellKnown(ClassSpec cls)
    {
        string ns = cls.Ns;
        string body = cls.Name switch
        {
            "Guard" => """
                public static class Guard
                {
                    public static void NotNull(object value, string name)
                    {
                        if (value == null)
                        {
                            throw new AcmeException("Argument '" + name + "' must not be null.");
                        }
                    }

                    public static void NotEmpty(string value, string name)
                    {
                        if (string.IsNullOrWhiteSpace(value))
                        {
                            throw new AcmeException("Argument '" + name + "' must not be empty.");
                        }
                    }
                }
                """,
            "Result" => """
                public class Result
                {
                    public bool IsSuccess { get; private set; }

                    public string Error { get; private set; }

                    public static Result Ok()
                    {
                        return new Result { IsSuccess = true, Error = null };
                    }

                    public static Result Fail(string error)
                    {
                        return new Result { IsSuccess = false, Error = error };
                    }
                }
                """,
            "EntityBase" => """
                public abstract class EntityBase
                {
                    protected EntityBase()
                    {
                        Id = Guid.NewGuid();
                        CreatedAtUtc = DateTime.UtcNow;
                    }

                    public Guid Id { get; protected set; }

                    public DateTime CreatedAtUtc { get; protected set; }
                }
                """,
            "AcmeException" => """
                public class AcmeException : Exception
                {
                    public AcmeException(string message)
                        : base(message)
                    {
                    }

                    public AcmeException(string message, Exception inner)
                        : base(message, inner)
                    {
                    }
                }
                """,
            _ => throw new InvalidOperationException($"Unknown well-known type {cls.Name}"),
        };

        var sb = new StringBuilder();
        sb.AppendLine("using System;");
        sb.AppendLine();
        sb.AppendLine($"namespace {ns}");
        sb.AppendLine("{");
        foreach (var line in body.Split('\n'))
        {
            string trimmed = line.TrimEnd('\r');
            sb.AppendLine(trimmed.Length == 0 ? "" : "    " + trimmed);
        }
        sb.AppendLine("}");
        return sb.ToString();
    }

    // ---------------------------------------------------------------- helpers

    private static IEnumerable<MethodSpec> AllMethods(ClassSpec cls)
    {
        if (cls.Implements is { } iface)
        {
            foreach (var m in iface.Methods) yield return m;
        }
        foreach (var m in cls.OwnMethods) yield return m;
    }

    private string ForwardOrDefault(MethodSpec caller, TypeRef wanted, Random rng)
    {
        var match = caller.Params.FirstOrDefault(x => x.Type == wanted);
        return match.Name is not null ? match.Name : DefaultExpr(wanted, rng);
    }

    private string DefaultExpr(TypeRef t, Random rng)
    {
        if (t.IsPrimitive)
        {
            return t.Name switch
            {
                "string" => $"\"{NameBank.Pick(rng, NameBank.Nouns).ToLowerInvariant()}-{rng.Next(1, 999)}\"",
                "int" => rng.Next(1, 500).ToString(),
                "decimal" => $"{rng.Next(1, 900)}.{rng.Next(10, 99)}m",
                "bool" => "true",
                "DateTime" => "DateTime.UtcNow",
                "Guid" => "Guid.NewGuid()",
                _ => "default",
            };
        }
        if (_enums.TryGetValue(t, out var e))
        {
            return $"{e.Name}.{e.Members[rng.Next(e.Members.Count)]}";
        }
        if (t.Name == "Result" && t.Ns == _ws.PlatformCommon.Ns)
        {
            return "Result.Ok()";
        }
        if (_dtos.TryGetValue(t, out var dto))
        {
            var prims = dto.Props.Where(x => x.Type.IsPrimitive || _enums.ContainsKey(x.Type)).Take(2).ToList();
            if (prims.Count == 0) return $"new {dto.Name}()";
            var inits = prims.Select(x => $"{x.Name} = {DefaultExpr(x.Type, rng)}");
            return $"new {dto.Name} {{ {string.Join(", ", inits)} }}";
        }
        return $"new {t.Name}()";
    }

    private static string ParamList(MethodSpec m) =>
        string.Join(", ", m.Params.Select(x => $"{x.Type.Name} {x.Name}"));

    private static void AddUsing(SortedSet<string> usings, TypeRef t, string ownNs)
    {
        if (t.Ns is { } ns && ns != ownNs) usings.Add(ns);
    }

    private static string Humanize(string identifier)
    {
        var sb = new StringBuilder();
        foreach (char c in identifier)
        {
            if (char.IsUpper(c) && sb.Length > 0) sb.Append(' ');
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    /// <summary>Minimal indented C# writer with a fixed one-namespace layout.</summary>
    private sealed class Writer
    {
        private readonly StringBuilder _sb = new();
        private int _indent;
        private bool _inNamespace;

        public void GeneratedHeader()
        {
            _sb.AppendLine("//------------------------------------------------------------------------------");
            _sb.AppendLine("// <auto-generated>");
            _sb.AppendLine("//     This code was generated by a tool.");
            _sb.AppendLine("//     Changes to this file may be lost if the code is regenerated.");
            _sb.AppendLine("// </auto-generated>");
            _sb.AppendLine("//------------------------------------------------------------------------------");
        }

        public void Header(IEnumerable<string> usings, string ns)
        {
            var ordered = usings.Distinct()
                .OrderBy(u => u.StartsWith("System", StringComparison.Ordinal) ? 0 : 1)
                .ThenBy(u => u, StringComparer.Ordinal);
            foreach (var u in ordered) _sb.AppendLine($"using {u};");
            _sb.AppendLine();
            _sb.AppendLine($"namespace {ns}");
            _sb.AppendLine("{");
            _indent = 1;
            _inNamespace = true;
        }

        public void Open()
        {
            Line("{");
            _indent++;
        }

        public void Close()
        {
            _indent--;
            Line("}");
        }

        public void Line(string text) => _sb.Append(new string(' ', _indent * 4)).AppendLine(text);

        public void Blank() => _sb.AppendLine();

        public void End()
        {
            if (_inNamespace)
            {
                _sb.AppendLine("}");
                _inNamespace = false;
            }
        }

        public override string ToString() => _sb.ToString();
    }
}
