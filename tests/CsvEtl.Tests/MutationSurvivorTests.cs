using CsvEtl.Csv;
using CsvEtl.Pipeline;
using CsvEtl.Schema;
using CsvEtl.Validation;

namespace CsvEtl.Tests;

/// <summary>One test per Stryker.NET survivor that was a real gap (the others are documented as equivalent in the README).</summary>
public class MutationSurvivorTests
{
    private const string S = """
        {"name":"t","key":["id"],"columns":[
          {"name":"id","aliases":["Cust ID"],"required":true},
          {"name":"age","type":"int","min":0,"max":120,"onInvalid":"null"},
          {"name":"tier","type":"enum","allowed":["gold","silver"],"default":"silver","onInvalid":"default"}
        ]}
        """;

    private static EtlResult Run(string csv, string schema = S, IRejectSink? rejects = null)
    {
        return EtlRunner.Run(new StringReader(csv), T.Schema(schema), new EtlOptions(), new ICleanSink[] { new CleanCsvSink(new StringWriter()) }, rejects);
    }

    [Fact]
    public void NulCharactersAreDataNotEndOfInput()
    {
        Assert.Equal(new[] { new[] { "a\0b", "c" }, new[] { "\0", "d" } }, T.Parse("a\0b,c\n\0,d\n"));
        Assert.Equal(new[] { new[] { "x\0y", "z" } }, T.Parse("\"x\0y\",z\n"));
        Assert.Equal(new[] { new[] { "xq", "z" } }, T.Parse("\"x\"q,z\n").Select(r => r.Select(f => f).ToArray()).ToArray());
        Assert.Equal(new[] { new[] { "x\0", "z" } }, T.Parse("\"x\"\0,z\n"));
        Assert.Equal(new[] { new[] { "a", "b\0" } }, T.Parse("a,b\0"));
    }

    [Fact]
    public void RawTextCapIsExactlyTwiceTheRecordLimitPlusSixtyFour()
    {
        var big = T.Records("a\n" + new string('x', 5000) + "\n", new CsvOptions { MaxFieldSize = 10, MaxRecordSize = 10 })[1];
        Assert.Equal(2 * 10 + 64, big.Raw!.Length);
    }

    [Fact]
    public void FirstStructuralIssueInARecordWins()
    {
        var recs = T.Records("\"a\"x,\"b\n");
        Assert.Equal(CsvIssueKind.StrayQuote, recs[0].Issue!.Kind);
        var two = T.Records("\"a\nb\"x,\"c\nd\"y\n");
        Assert.Equal(2, two[0].Issue!.Line); // the first stray character is on line 2, the second on line 3
    }

    [Fact]
    public void RawTextIncludesStrayCharactersAfterAClosingQuote() =>
        Assert.Equal("\"x\"y,z", T.Records("\"x\"y,z\n")[0].Raw);

    [Fact]
    public void NullArgumentsAreRejectedUpFront()
    {
        Assert.Throws<ArgumentNullException>(() => CsvReader.Read(null!));
        Assert.Throws<ArgumentNullException>(() => new CsvWriter(new StringWriter()).WriteRow(null!));
    }

    [Fact]
    public void DuplicateIssueActionIsRejectedUnderTheRejectPolicy()
    {
        var r = Run("id\na\na\n");
        Assert.Equal(IssueAction.Rejected, Assert.Single(r.Report.Issues).Action);
    }

    [Fact]
    public void RepairedCountOnlyCountsRowsThatWereRepaired()
    {
        var r = Run("id,age,tier\na,5,gold\nb,200,gold\nc,6,gold\nd,7,silver\n");
        Assert.Equal(4, r.RowsClean);
        Assert.Equal(1, r.RowsRepaired); // only b (age nulled): a null-policy repair on its own must count
    }

    [Fact]
    public void DefaultPolicyAloneAlsoCountsAsRepaired()
    {
        var r = Run("id,age,tier\na,5,gold\nb,5,platinum\nc,5,gold\n");
        Assert.Equal(1, r.RowsRepaired);
    }

    [Fact]
    public void DuplicateHeaderWarningNamesBothFieldPositions()
    {
        var r = Run("id,Cust ID,age\na,b,1\n");
        Assert.Equal("header 'Cust ID' (field 2) also maps to column 'id'; the first match (field 1) is used", Assert.Single(r.HeaderWarnings));
    }

    [Fact]
    public void RowLevelIssuesAreCountedUnderRowInTheColumnBreakdown()
    {
        var r = Run("id,age,tier\na,5,gold\nb,5\n");
        Assert.Equal(1, r.Report.ByColumn["(row)"]);
        Assert.False(r.Report.ByColumn.ContainsKey(""));
    }
}
