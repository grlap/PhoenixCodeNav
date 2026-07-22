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
