using System.Text;
using CodeNav.Core.Indexing;

namespace CodeNav.Tests;

public sealed class Batch62StackSafeSyntaxIndexerTests
{
    [Fact]
    public void FiveHundredNestedGenericTypesIndexCompletelyOnOneMegabyteStack()
    {
        const int depth = 500;
        var source = new StringBuilder();
        for (int i = 0; i < depth; i++)
            source.Append("public struct MyStruct").Append(i).AppendLine("<T> {");
        for (int i = 0; i < depth; i++)
            source.AppendLine("}");

        ParsedCsFile? parsed = null;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                parsed = SyntaxIndexer.Parse("NestedGenericStructs.cs", source.ToString());
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        }, maxStackSize: 1024 * 1024);

        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "syntax indexing did not complete");
        Assert.Null(failure);
        Assert.NotNull(parsed);
        Assert.Equal(depth, parsed.Symbols.Count);

        for (int i = 0; i < depth; i++)
        {
            SymbolRow symbol = parsed.Symbols[i];
            Assert.Equal(i, symbol.OrdinalInFile);
            Assert.Equal(i - 1, symbol.ParentOrdinal);
            Assert.Equal($"MyStruct{i}", symbol.Name);
            Assert.Equal("struct", symbol.Kind);
        }
    }
}
