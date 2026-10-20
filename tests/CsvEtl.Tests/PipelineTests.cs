using System.Text;
using System.Text.Json;
using CsvEtl.Csv;
using CsvEtl.Pipeline;
using CsvEtl.Validation;

namespace CsvEtl.Tests;

public class PipelineTests
{
    private const string Schema = """
        {"name":"t","key":["id"],"columns":[
          {"name":"id","aliases":["Cust ID","customer_id"],"required":true,"case":"upper"},
          {"name":"age","type":"int","min":0,"max":120,"onInvalid":"null"},
          {"name":"tier","type":"enum","allowed":["gold","silver"],"default":"silver","onInvalid":"default"},
          {"name":"score","type":"decimal","onInvalid":"reject"}
        ]}
        """;

    private sealed record Out(EtlResult R, string Clean, string Rejects, string Jsonl);

    private static Out Run(string csv, string schemaJson = Schema, EtlOptions? options = null)
    {
        var schema = T.Schema(schemaJson);
        var clean = new StringWriter();
        var rej = new StringWriter();
        var jsonl = new StringWriter();
        var r = EtlRunner.Run(new StringReader(csv), schema, options ?? new EtlOptions(),
            new ICleanSink[] { new CleanCsvSink(clean), new JsonlSink(jsonl) }, new RejectsCsvSink(rej));
        return new Out(r, clean.ToString(), rej.ToString(), jsonl.ToString());
    }

    [Theory]
    [InlineData("Cust ID,Age,Tier,Score")]
    [InlineData("customer_id,age,tier,score")]
    [InlineData("CUSTOMER ID,AGE,TIER,SCORE")]
    [InlineData("Customer-Id,Age,Tier,Score")]
    [InlineData("id,age,tier,score")]
    public void HeaderAliasesMapToOneColumn(string header)
    {
        var o = Run(header + "\na1,30,gold,1.5\n");
        Assert.False(o.R.HeaderFatal);
        Assert.Equal("id,age,tier,score\nA1,30,gold,1.5\n", o.Clean);
    }

    [Fact]
    public void ColumnOrderIsFreeAndExtraColumnsAreIgnoredButReported()
    {
        var o = Run("score,junk,Tier,id,age\n2.5,zzz,GOLD,x9,5\n");
        Assert.Equal("id,age,tier,score\nX9,5,gold,2.5\n", o.Clean);
        Assert.Equal(new[] { "junk" }, o.R.UnmappedHeaders);
    }

    [Fact]
    public void MissingRequiredColumnIsFatalForTheFile()
    {
        var o = Run("age,tier,score\n1,gold,2\n");
        Assert.True(o.R.HeaderFatal);
        Assert.Equal(0, o.R.RowsRead);
        Assert.Contains("required column 'id' not found", o.R.HeaderErrors[0]);
        Assert.Contains("Cust ID", o.R.HeaderErrors[0]);
        Assert.Equal("header", o.R.Report.Issues[0].Rule);
    }

    [Fact]
    public void MissingOptionalColumnGetsDefaultOrNull()
    {
        var o = Run("id,score\na,1\n");
        Assert.Equal("id,age,tier,score\nA,,silver,1\n", o.Clean);
    }

    [Fact]
    public void DuplicateHeaderMappingWarnsAndFirstWins()
    {
        var o = Run("id,Cust ID,score\na,b,1\n");
        Assert.Single(o.R.HeaderWarnings);
        Assert.Equal("id,age,tier,score\nA,,silver,1\n", o.Clean);
    }

    [Fact]
    public void EmptyInputIsAHeaderProblem()
    {
        var o = Run("");
        Assert.True(o.R.HeaderFatal);
        Assert.Contains("empty", o.R.HeaderErrors[0]);
    }

    [Fact]
    public void BlankLinesBeforeHeaderAreSkipped() => Assert.Equal(1, Run("\n\nid,score\na,1\n").R.RowsClean);

    [Fact]
    public void MalformedHeaderIsFatal()
    {
        var o = Run("id,\"score\nx,1\n");
        Assert.True(o.R.HeaderFatal);
        Assert.Contains("malformed", o.R.HeaderErrors[0]);
    }

    [Fact]
    public void PoliciesRejectNullAndDefault()
    {
        var o = Run("id,age,tier,score\na,200,platinum,1\nb,5,gold,abc\nc,,,\n");
        // a: age 200 -> null (policy null), tier platinum -> silver (policy default): kept and repaired
        // b: score abc -> reject
        // c: blanks: age null, tier default silver, score null
        Assert.Equal("id,age,tier,score\nA,,silver,1\nC,,silver,\n", o.Clean);
        Assert.Equal(2, o.R.RowsClean);
        Assert.Equal(1, o.R.RowsRejected);
        Assert.Equal(1, o.R.RowsRepaired);
        Assert.Contains(o.R.Report.Issues, i => i.Row == 1 && i.Column == "age" && i.Rule == "max" && i.Action == IssueAction.Nulled);
        Assert.Contains(o.R.Report.Issues, i => i.Row == 1 && i.Column == "tier" && i.Rule == "allowed" && i.Action == IssueAction.Defaulted);
        Assert.Contains(o.R.Report.Issues, i => i.Row == 2 && i.Column == "score" && i.Rule == "type" && i.Action == IssueAction.Rejected && i.RawValue == "abc");
    }

    [Fact]
    public void AllProblemsInARowAreReportedNotJustTheFirst()
    {
        var o = Run("id,age,tier,score\n,-5,bronze,xx\n");
        Assert.Equal(1, o.R.RowsRejected);
        Assert.Equal(new[] { "id", "age", "tier", "score" }, o.R.Report.Issues.Select(i => i.Column).ToArray());
        Assert.Equal(new[] { "required", "min", "allowed", "type" }, o.R.Report.Issues.Select(i => i.Rule).ToArray());
    }

    [Fact]
    public void IssueCarriesRowLineColumnRuleRawValueAndMessage()
    {
        var o = Run("id,age,tier,score\na,1,gold,1\nb,2,gold,\"multi\nline\"\n");
        var i = Assert.Single(o.R.Report.Issues);
        Assert.Equal(2, i.Row);
        Assert.Equal(3, i.Line);
        Assert.Equal("score", i.Column);
        Assert.Equal("type", i.Rule);
        Assert.Equal("multi\nline", i.RawValue);
        Assert.Contains("not a decimal", i.Message);
    }

    [Fact]
    public void RaggedRowsAreRejectedWithTheirRawText()
    {
        var o = Run("id,age,tier,score\na,1,gold,1\nb,2\nc,3,gold,3,extra\n");
        Assert.Equal(1, o.R.RowsClean);
        Assert.Equal(2, o.R.RowsRejected);
        Assert.All(o.R.Report.Issues, i => Assert.Equal("ragged-row", i.Rule));
        Assert.Contains("b,2", o.Rejects);
        Assert.Contains("c,3,gold,3,extra", o.Rejects);
    }

    [Fact]
    public void UnterminatedQuoteRejectsTheRestAsOneRecordWithTheOpeningLine()
    {
        var o = Run("id,age,tier,score\na,1,gold,1\nb,\"oops,2,gold,2\nc,3,gold,3\n");
        Assert.Equal(1, o.R.RowsClean);
        var i = Assert.Single(o.R.Report.Issues);
        Assert.Equal("unterminated-quote", i.Rule);
        Assert.Equal(3, i.Line);
        Assert.Equal(1, o.R.RowsRejected);
    }

    [Fact]
    public void ReportIsCappedButTotalsStayExact()
    {
        var sb = new StringBuilder("id,score\n");
        for (int i = 0; i < 50_000; i++) sb.Append('r').Append(i).Append(",bad\n");
        var o = Run(sb.ToString(), Schema, new EtlOptions { MaxErrors = 10 });
        Assert.Equal(50_000, o.R.RowsRejected);
        Assert.Equal(10, o.R.Report.Issues.Count);
        Assert.Equal(50_000, o.R.Report.TotalIssues);
        Assert.Equal(49_990, o.R.Report.DroppedIssues);
        Assert.Equal(50_000, o.R.Report.ByRule["type"]);
        Assert.Equal(50_000, o.R.Report.ByColumn["score"]);
        // the rejects file is not capped: every rejected record is preserved
        Assert.Equal(50_001, CsvReader.ReadString(o.Rejects, T.Loose).Count());
    }

    [Fact]
    public void MaxErrorsZeroStoresNothingButCounts()
    {
        var o = Run("id,score\na,x\nb,y\n", Schema, new EtlOptions { MaxErrors = 0 });
        Assert.Empty(o.R.Report.Issues);
        Assert.Equal(2, o.R.Report.TotalIssues);
    }

    [Fact]
    public void HugeRawValuesAreClippedInTheReport()
    {
        var o = Run("id,score\na," + new string('9', 100_000) + "x\n");
        Assert.True(o.R.Report.Issues[0].RawValue.Length <= 200 + 3);
    }

    [Theory]
    [InlineData(DedupeMode.Exact)]
    [InlineData(DedupeMode.Hash)]
    public void DuplicatesOnNormalisedKeyFirstOccurrenceWins(DedupeMode mode)
    {
        var o = Run("id,score\na1,1\nB2,2\nA1,3\nb2,4\nc3,5\n", Schema, new EtlOptions { Dedupe = mode });
        Assert.Equal("id,age,tier,score\nA1,,silver,1\nB2,,silver,2\nC3,,silver,5\n", o.Clean);
        Assert.Equal(2, o.R.DuplicatesFound);
        Assert.Equal(2, o.R.RowsRejected);
        Assert.Contains(o.R.Report.Issues, i => i.Rule == "duplicate" && i.Row == 3 && i.RawValue == "A1");
    }

    [Fact]
    public void RowsRejectedForOtherReasonsDoNotRegisterTheirKey()
    {
        var o = Run("id,score\na,bad\na,2\n");
        Assert.Equal("id,age,tier,score\nA,,silver,2\n", o.Clean);
        Assert.Equal(0, o.R.DuplicatesFound);
    }

    [Fact]
    public void DedupeOffKeepsDuplicates()
    {
        var o = Run("id,score\na,1\na,2\n", Schema, new EtlOptions { Dedupe = DedupeMode.Off });
        Assert.Equal(2, o.R.RowsClean);
    }

    [Fact]
    public void OnDuplicateKeepFlagsButDoesNotReject()
    {
        string schema = Schema.Replace("\"key\":[\"id\"]", "\"key\":[\"id\"],\"onDuplicate\":\"keep\"");
        var o = Run("id,score\na,1\na,2\n", schema);
        Assert.Equal(2, o.R.RowsClean);
        Assert.Equal(1, o.R.DuplicatesFound);
        Assert.Equal(IssueAction.Kept, Assert.Single(o.R.Report.Issues).Action);
    }

    [Fact]
    public void RowsWithEmptyKeyAreNeverDuplicates()
    {
        string schema = """{"key":["k"],"columns":[{"name":"k"},{"name":"v"}]}""";
        var o = Run("k,v\n,1\n,2\nx,3\nx,4\n", schema);
        Assert.Equal(3, o.R.RowsClean);
        Assert.Equal(1, o.R.DuplicatesFound);
    }

    [Fact]
    public void CompositeKeysAreNotAmbiguous()
    {
        string schema = """{"key":["a","b"],"columns":[{"name":"a"},{"name":"b"}]}""";
        // ("ab","c") and ("a","bc") would collide if the key were a plain concatenation
        var o = Run("a,b\nab,c\na,bc\nab,c\n", schema);
        Assert.Equal(2, o.R.RowsClean);
        Assert.Equal(1, o.R.DuplicatesFound);
    }

    [Theory]
    [InlineData(DedupeMode.Exact)]
    [InlineData(DedupeMode.Hash)]
    public void DedupeTableIsBoundedAndSaturationIsReported(DedupeMode mode)
    {
        var sb = new StringBuilder("id,score\n");
        foreach (var id in new[] { "a", "b", "c", "d", "a", "c", "e", "a" }) sb.Append(id).Append(",1\n");
        var o = Run(sb.ToString(), Schema, new EtlOptions { Dedupe = mode, MaxKeys = 2 });
        Assert.True(o.R.DedupeSaturated);
        // a and b were remembered: both later 'a' rows are caught. c, d, e were never remembered, so the second c is missed.
        Assert.Equal(2, o.R.DuplicatesFound);
        Assert.Equal(4, o.R.DedupeUncheckedRows); // c, d, second c, e were not in the table and could not be added
    }

    [Fact]
    public void KeyTrackerCapsMemoryAndCountsUncheckedKeys()
    {
        var t = new KeyTracker(DedupeMode.Hash, 100);
        for (int i = 0; i < 1000; i++) Assert.True(t.TryAdd("k" + i));
        Assert.Equal(100, t.Count);
        Assert.True(t.Saturated);
        Assert.Equal(900, t.UncheckedKeys);
        Assert.False(t.TryAdd("k5"));
        Assert.True(t.TryAdd("k999"));
    }

    [Fact]
    public void CleanCsvAndJsonlAreTypedAndCanonical()
    {
        var o = Run("Cust ID,Age,Tier,Score\n a1 ,\"30\",GOLD,\"1,5\"\nb2,7,silver,1.50\n");
        // row 1 score "1,5" is invalid with the '.' separator -> rejected
        Assert.Equal("id,age,tier,score\nB2,7,silver,1.50\n", o.Clean);
        Assert.Equal("{\"id\":\"B2\",\"age\":7,\"tier\":\"silver\",\"score\":1.50}\n", o.Jsonl);
    }

    [Fact]
    public void JsonlUsesNullForEmptyAndEscapesStrings()
    {
        string schema = """{"columns":[{"name":"n"},{"name":"d","type":"date"},{"name":"b","type":"bool"},{"name":"i","type":"int"}]}""";
        var o = Run("n,d,b,i\n\"say \"\"hi\"\"\nnow é😀\",2024-03-05,yes,\n", schema);
        Assert.Equal("{\"n\":\"say \\\"hi\\\"\\nnow é\\uD83D\\uDE00\",\"d\":\"2024-03-05\",\"b\":true,\"i\":null}\n", o.Jsonl);
        foreach (var line in o.Jsonl.Split('\n', StringSplitOptions.RemoveEmptyEntries)) JsonDocument.Parse(line).Dispose();
    }

    [Fact]
    public void RejectsFilePreservesRawRecordAndReasons()
    {
        string badRecord = "x1,\"line one\nline two\",gold,notanumber";
        var o = Run("id,age,tier,score\na,1,gold,1\n" + badRecord + "\n");
        // the 2nd field is the age column: "line one\nline two" is an invalid int -> nulled; score notanumber -> rejected
        var rows = CsvReader.ReadString(o.Rejects, T.Loose).ToList();
        Assert.Equal(new[] { "row", "line", "reasons", "raw" }, rows[0].Fields);
        Assert.Equal("2", rows[1].Fields[0]);
        Assert.Equal("3", rows[1].Fields[1]);
        Assert.Contains("score: type:", rows[1].Fields[2]);
        Assert.Contains("age: type:", rows[1].Fields[2]);
        Assert.Equal(badRecord, rows[1].Fields[3]);
        // and the raw text can be re-submitted: it parses to the original record
        Assert.Equal(new[] { "x1", "line one\nline two", "gold", "notanumber" }, T.Parse(rows[1].Fields[3])[0]);
    }

    [Fact]
    public void RejectsFileHasHeaderEvenWhenNothingIsRejected()
    {
        var o = Run("id,score\na,1\n");
        Assert.Equal("row,line,reasons,raw\n", o.Rejects);
    }

    [Fact]
    public void ProgressCallbackFiresEveryNRows()
    {
        var seen = new List<long>();
        var sb = new StringBuilder("id,score\n");
        for (int i = 0; i < 25; i++) sb.Append('k').Append(i).Append(",1\n");
        Run(sb.ToString(), Schema, new EtlOptions { ProgressEvery = 10, OnProgress = p => seen.Add(p.RowsRead) });
        Assert.Equal(new long[] { 10, 20 }, seen);
    }

    [Fact]
    public void JsonReportIsParseableAndConsistent()
    {
        var o = Run("id,score\na,x\nb,1\n");
        using var doc = JsonDocument.Parse(ReportWriter.ToJson(o.R, "t", "in.csv"));
        var s = doc.RootElement.GetProperty("summary");
        Assert.Equal(2, s.GetProperty("rowsRead").GetInt64());
        Assert.Equal(1, s.GetProperty("rowsRejected").GetInt64());
        Assert.Equal(1, doc.RootElement.GetProperty("byRule").GetProperty("type").GetInt64());
        Assert.Equal("score", doc.RootElement.GetProperty("issues")[0].GetProperty("column").GetString());
    }

    [Fact]
    public void TextReportMentionsTheCapWhenIssuesWereDropped()
    {
        var o = Run("id,score\na,x\nb,y\nc,z\n", Schema, new EtlOptions { MaxErrors = 1 });
        var sw = new StringWriter();
        ReportWriter.WriteText(sw, o.R, "t", "in.csv");
        Assert.Contains("2 more issue(s) not listed", sw.ToString());
        Assert.Contains("3 total, 1 listed, 2 not listed (cap 1)", sw.ToString());
    }

    [Fact]
    public void SampleFileEndToEndCountsAreStable()
    {
        using var reader = CsvReader.OpenFile(T.SamplePath("customers_messy.csv"));
        var r = EtlRunner.Run(reader, T.SampleSchema(), new EtlOptions());
        Assert.Equal(200, r.RowsRead);
        Assert.Equal(r.RowsRead, r.RowsClean + r.RowsRejected);
        Assert.True(r.RowsRejected > 0 && r.RowsClean > 0);
        Assert.True(r.RowsRepaired > 0);
        Assert.True(r.DuplicatesFound >= 1);
    }
}
