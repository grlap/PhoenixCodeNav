using CodeNav.Core.Indexing;

namespace CodeNav.Tests;

public sealed class SyntaxIndexerStructuralTests
{
    [Fact]
    public void MixedDeclarationTreePreservesExactDepthFirstRows()
    {
        ParsedCsFile parsed = ParseAndAssertStructure("""
            namespace Mixed
            {
                public class Outer<T>
                {
                    int before;

                    public class Inner
                    {
                        public struct LeafContainer
                        {
                            void Leaf() { }
                        }

                        void AfterLeaf() { }
                    }

                    event Action First, Second;
                    void AfterInner() { }

                    enum State
                    {
                        None,
                        Ready
                    }
                }

                public record Payload(int Value)
                {
                    public int ExplicitProperty { get; init; }
                }

                public record struct Coordinate(int X, int Y);
            }
            """);

        Assert.Equal(
        [
            "0|-1|namespace|Mixed|-|-|public",
            "1|0|class|Outer|Mixed|-|public",
            "2|1|field|before|Mixed|Outer|private",
            "3|1|class|Inner|Mixed|Outer|public",
            "4|3|struct|LeafContainer|Mixed|Outer.Inner|public",
            "5|4|method|Leaf|Mixed|Outer.Inner.LeafContainer|private",
            "6|3|method|AfterLeaf|Mixed|Outer.Inner|private",
            "7|1|event|First|Mixed|Outer|private",
            "8|1|event|Second|Mixed|Outer|private",
            "9|1|method|AfterInner|Mixed|Outer|private",
            "10|1|enum|State|Mixed|Outer|private",
            "11|10|enum_member|None|Mixed|Outer.State|public",
            "12|10|enum_member|Ready|Mixed|Outer.State|public",
            "13|0|record|Payload|Mixed|-|public",
            "14|13|property|ExplicitProperty|Mixed|Payload|public",
            "15|0|record_struct|Coordinate|Mixed|-|public",
        ], ProjectRows(parsed));

        Assert.DoesNotContain(parsed.Symbols, row => row.Name is "Value" or "X" or "Y");
    }

    [Fact]
    public void NamespaceAndContainerStateStaysWithinItsBranch()
    {
        ParsedCsFile blockScoped = ParseAndAssertStructure("""
            public class GlobalBefore { }

            namespace Alpha.Beta
            {
                public class Same { }

                namespace Gamma
                {
                    class Deep
                    {
                        class Nested { }
                    }
                }
            }

            namespace Other
            {
                public class Same { }
            }

            public class GlobalAfter { }
            """);

        Assert.Equal(
        [
            "GlobalBefore|-|-",
            "Alpha.Beta|-|-",
            "Same|Alpha.Beta|-",
            "Alpha.Beta.Gamma|Alpha.Beta|-",
            "Deep|Alpha.Beta.Gamma|-",
            "Nested|Alpha.Beta.Gamma|Deep",
            "Other|-|-",
            "Same|Other|-",
            "GlobalAfter|-|-",
        ], blockScoped.Symbols.Select(row =>
            $"{row.Name}|{row.Namespace ?? "-"}|{row.Container ?? "-"}").ToArray());

        ParsedCsFile fileScoped = ParseAndAssertStructure("""
            namespace File.Scope;

            class Outer<T>
            {
                class Inner
                {
                    void Run() { }
                }
            }
            """);

        Assert.Equal(
        [
            "File.Scope|-|-",
            "Outer|File.Scope|-",
            "Inner|File.Scope|Outer",
            "Run|File.Scope|Outer.Inner",
        ], fileScoped.Symbols.Select(row =>
            $"{row.Name}|{row.Namespace ?? "-"}|{row.Container ?? "-"}").ToArray());
    }

    [Fact]
    public void SupportedKindsDefaultsAndMultiDeclaratorsAreExplicit()
    {
        ParsedCsFile parsed = ParseAndAssertStructure("""
            namespace Kinds;

            interface IApi
            {
                void Run();
                int Value { get; }
                int this[int index] { get; }
                event Action Changed;
                class Nested { }
                delegate void Callback();
                private void Hidden() { }
            }

            class Widget
            {
                public Widget() { }
                public int this[int index] => index;

                public event Action Managed
                {
                    add { }
                    remove { }
                }

                event Action First, Second;
                const int Left = 1, Right = 2;

                public static Widget operator +(Widget left, Widget right) => left;

                void Host()
                {
                    void Local() { }
                }
            }

            struct State
            {
                int value;
            }

            record Model
            {
                int value;
            }

            record struct ValueModel
            {
                int value;
            }

            enum Level
            {
                Low
            }

            delegate TResult Factory<TResult>();
            """);

        Assert.Equal(
        [
            "namespace:Kinds:public",
            "interface:IApi:internal",
            "method:Run:public",
            "property:Value:public",
            "indexer:this[]:public",
            "event:Changed:public",
            "class:Nested:public",
            "delegate:Callback:public",
            "method:Hidden:private",
            "class:Widget:internal",
            "constructor:Widget:public",
            "indexer:this[]:public",
            "event:Managed:public",
            "event:First:private",
            "event:Second:private",
            "field:Left:private",
            "field:Right:private",
            "operator:operator +:public",
            "method:Host:private",
            "struct:State:internal",
            "field:value:private",
            "record:Model:internal",
            "field:value:private",
            "record_struct:ValueModel:internal",
            "field:value:private",
            "enum:Level:internal",
            "enum_member:Low:public",
            "delegate:Factory:internal",
        ], parsed.Symbols.Select(row => $"{row.Kind}:{row.Name}:{row.Accessibility}").ToArray());

        Assert.DoesNotContain(parsed.Symbols, row => row.Name == "Local");
        Assert.Equal(1, parsed.Symbols.Single(row => row.Name == "Factory").Arity);
    }

    [Fact]
    public void ConversionOperatorsPreserveExactRowsIdentityAndNesting()
    {
        ParsedCsFile parsed = ParseAndAssertStructure("""
            namespace Conversions;

            public readonly struct Scalar<T>
            {
                public static implicit operator Scalar<T>(int value) => default;
                public static explicit operator int(Scalar<T> value) => 0;
                public static explicit operator long(Scalar<T> value) => 0;

                public readonly struct Nested
                {
                    public static implicit operator Nested(string value) => default;
                }
            }
            """);

        SymbolRow[] conversions = parsed.Symbols
            .Where(row => row.Kind == "operator")
            .ToArray();
        Assert.Equal(
        [
            "2|1|implicit operator Scalar<T>|implicit operator Scalar<T>(int value)|" +
            "public|5|5|static|operator\u001e\u001eimplicit operator Scalar\u001d<\u001d$type0_0\u001d>\u001e0\u001eint|Scalar",
            "3|1|explicit operator int|explicit operator int(Scalar<T> value)|" +
            "public|6|6|static|operator\u001e\u001eexplicit operator int\u001e0\u001eScalar\u001d<\u001d$type0_0\u001d>|Scalar",
            "4|1|explicit operator long|explicit operator long(Scalar<T> value)|" +
            "public|7|7|static|operator\u001e\u001eexplicit operator long\u001e0\u001eScalar\u001d<\u001d$type0_0\u001d>|Scalar",
            "6|5|implicit operator Nested|implicit operator Nested(string value)|" +
            "public|11|11|static|operator\u001e\u001eimplicit operator Nested\u001e0\u001estring|Scalar.Nested",
        ], conversions.Select(row =>
            $"{row.OrdinalInFile}|{row.ParentOrdinal}|{row.Name}|{row.Signature}|" +
            $"{row.Accessibility}|{row.StartLine}|{row.EndLine}|{row.Modifiers}|" +
            $"{row.DeclarationKey}|{row.Container}").ToArray());

        Assert.Equal(4, conversions.Select(row => row.DeclarationKey).Distinct().Count());
        Assert.Equal(4, conversions.Select(row => row.ContextKey).Distinct().Count());
        IReadOnlyDictionary<string, List<(int Start, int End)>> identifierOffsets =
            SyntaxIndexer.DeclarationIdentifierOffsetMap(parsed.Content);
        Assert.All(conversions, row =>
        {
            (int Start, int End) = Assert.Single(identifierOffsets[row.Name]);
            Assert.Equal(row.Name, parsed.Content[Start..End]);
        });
        Assert.All(conversions, row =>
        {
            Assert.False(row.IsPartial);
            Assert.Equal(0, row.Arity);
            Assert.Null(row.AttrMarkers);
            Assert.Null(row.Accessors);
            Assert.Null(row.BaseTypes);
        });

        SymbolRow renamedTypeParameter = ParseAndAssertStructure("""
            namespace Conversions;
            public readonly struct Scalar<TRenamed>
            {
                public static implicit operator Scalar<TRenamed>(int value) => default;
            }
            """).Symbols.Single(row => row.Kind == "operator");
        Assert.Equal(conversions[0].DeclarationKey, renamedTypeParameter.DeclarationKey);
        Assert.Equal(conversions[0].ContextKey, renamedTypeParameter.ContextKey);
        Assert.NotEqual(conversions[0].Name, renamedTypeParameter.Name);
    }

    [Fact]
    public void ConversionOperatorDisplayNameIgnoresTargetTypeTrivia()
    {
        SymbolRow conversion = ParseAndAssertStructure("""
            namespace Conversions;
            public readonly struct Formatted
            {
                public static implicit operator System . Collections . Generic . List <
                    int /* layout-only marker */
                >(Formatted value) => new();
            }
            """).Symbols.Single(row => row.Kind == "operator");

        Assert.Equal("implicit operator System.Collections.Generic.List<int>", conversion.Name);
        Assert.Equal(
            "implicit operator System.Collections.Generic.List<int>(Formatted value)",
            conversion.Signature);
        Assert.DoesNotContain("marker", conversion.Name, StringComparison.Ordinal);
    }

    [Fact]
    public void CheckedAndExplicitInterfaceConversionsKeepIdentityAccessibilityAndOffsets()
    {
        ParsedCsFile parsed = ParseAndAssertStructure("""
            namespace Conversions;

            public interface IConvert<TSelf> where TSelf : IConvert<TSelf>
            {
                static abstract explicit operator int(TSelf value);
            }

            public readonly struct Value : IConvert<Value>
            {
                public static explicit operator int(Value value) => 0;
                public static explicit operator checked int(Value value) => 0;
                static explicit IConvert<Value>.operator int(Value value) => 0;
            }
            """);

        SymbolRow[] implementations = parsed.Symbols
            .Where(row => row.Kind == "operator" && row.Container == "Value")
            .ToArray();
        Assert.Equal(3, implementations.Length);
        Assert.Equal(
        [
            "explicit operator int(Value value)|public",
            "explicit operator checked int(Value value)|public",
            "explicit IConvert<Value>.operator int(Value value)|private",
        ], implementations.Select(row => $"{row.Signature}|{row.Accessibility}").ToArray());
        Assert.Equal(3, implementations.Select(row => row.DeclarationKey)
            .Distinct(StringComparer.Ordinal).Count());

        IReadOnlyDictionary<string, List<(int Start, int End)>> offsets =
            SyntaxIndexer.DeclarationIdentifierOffsetMap(parsed.Content);
        Assert.Equal(3, offsets["explicit operator int"].Count);
        Assert.Single(offsets["explicit operator checked int"]);
        (int Start, int End) explicitInterface = offsets["explicit operator int"]
            .Single(span => parsed.Content[span.Start..span.End]
                .Contains("IConvert<Value>.operator", StringComparison.Ordinal));
        Assert.Equal("explicit IConvert<Value>.operator int",
            parsed.Content[explicitInterface.Start..explicitInterface.End]);
    }

    public static TheoryData<string, string[]> RecoveredSources => new()
    {
        { "", [] },
        { "// trivia only", [] },
        { "#if false\nclass Hidden { }\n#endif", [] },
        {
            """
            namespace Broken
            {
                class Outer
                {
                    class Inner
                    {
                        void Run() { }
            """,
            ["Broken", "Outer", "Inner", "Run"]
        },
        {
            """
            class Before { }
            ???
            class After { }
            """,
            ["Before", "After"]
        },
        { "\uFEFFnamespace Bom;\nclass Visible { }", ["Bom", "Visible"] },
    };

    [Theory]
    [MemberData(nameof(RecoveredSources))]
    public void RecoveryTreesAreDeterministicAndStructurallyValid(string source, string[] expectedNames)
    {
        ParsedCsFile first = ParseAndAssertStructure(source);
        ParsedCsFile second = ParseAndAssertStructure(source);

        Assert.Equal(expectedNames, first.Symbols.Select(row => row.Name).ToArray());
        Assert.Equal(ProjectRows(first), ProjectRows(second));
    }

    private static ParsedCsFile ParseAndAssertStructure(string source)
    {
        ParsedCsFile parsed = SyntaxIndexer.Parse("Fixture.cs", source);
        AssertStructuralInvariants(parsed);
        return parsed;
    }

    private static void AssertStructuralInvariants(ParsedCsFile parsed)
    {
        for (int ordinal = 0; ordinal < parsed.Symbols.Count; ordinal++)
        {
            SymbolRow row = parsed.Symbols[ordinal];
            Assert.Equal(ordinal, row.OrdinalInFile);
            if (ordinal > 0)
            {
                Assert.True(row.StartLine >= parsed.Symbols[ordinal - 1].StartLine,
                    $"row {ordinal} starts before the preceding DFS row");
            }

            var ancestors = new List<SymbolRow>();
            int parentOrdinal = row.ParentOrdinal;
            while (parentOrdinal >= 0)
            {
                Assert.InRange(parentOrdinal, 0, ordinal - 1);
                SymbolRow parent = parsed.Symbols[parentOrdinal];
                Assert.True(parent.StartLine <= row.StartLine && parent.EndLine >= row.EndLine,
                    $"parent {parentOrdinal} does not contain child {ordinal}");
                ancestors.Add(parent);
                parentOrdinal = parent.ParentOrdinal;
            }
            ancestors.Reverse();

            string? expectedNamespace = ancestors.LastOrDefault(parent =>
                parent.Kind == "namespace")?.Name;
            Assert.Equal(expectedNamespace, row.Namespace);

            string[] typeContainers = ancestors
                .Where(parent => IsType(parent.Kind))
                .Select(parent => parent.Name)
                .ToArray();
            string? expectedContainer = typeContainers.Length == 0
                ? null
                : string.Join('.', typeContainers);
            Assert.Equal(expectedContainer, row.Container);
        }
    }

    private static bool IsType(string kind) =>
        kind is "class" or "interface" or "struct" or "record" or "record_struct" or "enum";

    private static string[] ProjectRows(ParsedCsFile parsed) => parsed.Symbols.Select(row =>
        $"{row.OrdinalInFile}|{row.ParentOrdinal}|{row.Kind}|{row.Name}|" +
        $"{row.Namespace ?? "-"}|{row.Container ?? "-"}|{row.Accessibility}").ToArray();
}
