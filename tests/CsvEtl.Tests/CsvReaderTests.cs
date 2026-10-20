using CsvEtl.Csv;

namespace CsvEtl.Tests;

public class CsvReaderCorpusTests
{
    public static IEnumerable<object[]> Cases()
    {
        static object[] C(string name, string input, params string[][] expected) => new object[] { name, input, expected };
        yield return C("simple LF", "a,b,c\n1,2,3\n", new[] { "a", "b", "c" }, new[] { "1", "2", "3" });
        yield return C("simple CRLF", "a,b\r\n1,2\r\n", new[] { "a", "b" }, new[] { "1", "2" });
        yield return C("mixed newlines", "a,b\r\n1,2\n3,4\r5,6", new[] { "a", "b" }, new[] { "1", "2" }, new[] { "3", "4" }, new[] { "5", "6" });
        yield return C("quoted delimiter", "\"a,b\",c\n", new[] { "a,b", "c" });
        yield return C("doubled quotes", "\"he said \"\"hi\"\"\",x\n", new[] { "he said \"hi\"", "x" });
        yield return C("embedded LF", "\"line1\nline2\",x\n", new[] { "line1\nline2", "x" });
        yield return C("embedded CRLF preserved", "\"line1\r\nline2\",x\r\ny,z\r\n", new[] { "line1\r\nline2", "x" }, new[] { "y", "z" });
        yield return C("embedded lone CR preserved", "\"a\rb\",x\n", new[] { "a\rb", "x" });
        yield return C("quote in middle of unquoted field is literal", "ab\"cd,e\n", new[] { "ab\"cd", "e" });
        yield return C("quote at start only after comma", "a,\"b\"\n", new[] { "a", "b" });
        yield return C("trailing delimiter", "a,b,\n", new[] { "a", "b", "" });
        yield return C("leading delimiter", ",a\n", new[] { "", "a" });
        yield return C("all empty fields", ",,\n", new[] { "", "", "" });
        yield return C("no trailing newline", "a,b\n1,2", new[] { "a", "b" }, new[] { "1", "2" });
        yield return C("empty last line ignored", "a,b\n1,2\n\n", new[] { "a", "b" }, new[] { "1", "2" });
        yield return C("blank lines in the middle skipped", "a\n\n\nb\n", new[] { "a" }, new[] { "b" });
        yield return C("BOM dropped", "﻿a,b\n1,2\n", new[] { "a", "b" }, new[] { "1", "2" });
        yield return C("only header, no newline", "a,b,c", new[] { "a", "b", "c" });
        yield return C("only header with newline", "a,b,c\n", new[] { "a", "b", "c" });
        yield return C("empty input", "");
        yield return C("only newlines", "\n\r\n\r");
        yield return C("single column: empty quoted value is a row, blank line is not", "h\n\"\"\n\nx\n", new[] { "h" }, new[] { "" }, new[] { "x" });
        yield return C("quoted empty then field", "\"\",x\n", new[] { "", "x" });
        yield return C("spaces are preserved", " a , b \n", new[] { " a ", " b " });
        yield return C("unicode and emoji", "é,中文,😀\n", new[] { "é", "中文", "😀" });
        yield return C("quoted field at EOF", "\"a\"", new[] { "a" });
        yield return C("quoted newline at EOF", "\"a\n\"", new[] { "a\n" });
        yield return C("four quotes is one quote", "\"\"\"\"\n", new[] { "\"" });
        yield return C("quoted field with only delimiter", "\",\"\n", new[] { "," });
        yield return C("BOM inside later field is data", "a,﻿b\n", new[] { "a", "﻿b" });
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Corpus(string name, string input, string[][] expected)
    {
        var got = T.Parse(input);
        Assert.True(expected.Length == got.Length, $"{name}: expected {expected.Length} rows, got {got.Length}");
        for (int i = 0; i < expected.Length; i++) Assert.Equal(expected[i], got[i]);
    }

    [Theory]
    [InlineData(';', "a;\"b;c\";d\n", new[] { "a", "b;c", "d" })]
    [InlineData('\t', "a\t\"b\tc\"\td,e\n", new[] { "a", "b\tc", "d,e" })]
    [InlineData('|', "a|b,c|\n", new[] { "a", "b,c", "" })]
    public void ConfigurableDelimiter(char delimiter, string input, string[] expected)
    {
        var rows = T.Parse(input, new CsvOptions { Delimiter = delimiter, ReportRagged = false });
        Assert.Single(rows);
        Assert.Equal(expected, rows[0]);
    }

    [Fact]
    public void BlankLinesCanBeKept()
    {
        var rows = T.Parse("a\n\nb\n", new CsvOptions { SkipBlankLines = false, ReportRagged = false });
        Assert.Equal(new[] { new[] { "a" }, new[] { "" }, new[] { "b" } }, rows);
    }

    [Theory]
    [InlineData('"')]
    [InlineData('\n')]
    [InlineData('\r')]
    public void InvalidDelimiterIsAConfigurationError(char d) =>
        Assert.Throws<ArgumentException>(() => CsvReader.ReadString("a", new CsvOptions { Delimiter = d }));

    [Fact]
    public void ReadsAcrossBufferBoundaries()
    {
        // tiny buffer forces every lookahead (doubled quote, CRLF) to straddle a refill
        string text = "\"a\"\"b\",\"c\r\nd\"\r\ne,f\r\n";
        var rows = T.Parse(text, new CsvOptions { BufferSize = 16, ReportRagged = false });
        Assert.Equal(new[] { new[] { "a\"b", "c\r\nd" }, new[] { "e", "f" } }, rows);
        for (int size = 16; size < 40; size++)
            Assert.Equal(rows, T.Parse(text, new CsvOptions { BufferSize = size, ReportRagged = false }));
    }

    [Fact]
    public void ReaderIsLazy()
    {
        var pulled = new List<string>();
        IEnumerable<string> Chunks()
        {
            for (int i = 0; i < 100_000; i++) { pulled.Add("x"); yield return "a,b\n"; }
        }

        var first = CsvReader.Read(new CsvEtl.Synthetic.EnumerableTextReader(Chunks()), new CsvOptions { BufferSize = 64 }).Take(2).ToList();
        Assert.Equal(2, first.Count);
        Assert.True(pulled.Count < 100, $"pulled {pulled.Count} chunks for 2 records");
    }
}

public class CsvReaderIssueTests
{
    [Fact]
    public void UnterminatedQuoteReportsOpeningLine()
    {
        var recs = T.Records("a,b\n1,2\n3,\"oops\n4,5\n6,7\n");
        Assert.Equal(3, recs.Count);
        var bad = recs[2];
        Assert.NotNull(bad.Issue);
        Assert.Equal(CsvIssueKind.UnterminatedQuote, bad.Issue!.Kind);
        Assert.Equal(3, bad.Issue.Line);
        Assert.Contains("line 3", bad.Issue.Message);
        Assert.Equal("oops\n4,5\n6,7\n", bad.Fields[1]);
        Assert.Equal("unterminated-quote", bad.Issue.RuleId);
    }

    [Fact]
    public void UnterminatedQuoteAtVeryEnd()
    {
        var recs = T.Records("a\n\"");
        Assert.Equal(CsvIssueKind.UnterminatedQuote, recs[1].Issue!.Kind);
        Assert.Equal(2, recs[1].Issue!.Line);
    }

    [Fact]
    public void RaggedRowsAreReportedNotThrown()
    {
        var recs = T.Records("a,b,c\n1,2\n1,2,3\n1,2,3,4\n");
        Assert.Null(recs[0].Issue);
        Assert.Equal(CsvIssueKind.RaggedRow, recs[1].Issue!.Kind);
        Assert.Equal(2, recs[1].Issue!.Line);
        Assert.Contains("2 fields, expected 3", recs[1].Issue!.Message);
        Assert.Null(recs[2].Issue);
        Assert.Equal(CsvIssueKind.RaggedRow, recs[3].Issue!.Kind);
        Assert.Equal(4, recs[3].Fields.Length);
    }

    [Fact]
    public void RaggedReportingCanBeDisabled() =>
        Assert.All(T.Records("a,b\n1\n1,2,3\n", new CsvOptions { ReportRagged = false }), r => Assert.Null(r.Issue));

    [Fact]
    public void CharactersAfterClosingQuoteAreFlaggedButKept()
    {
        var recs = T.Records("a,b\n\"x\"y,z\n");
        Assert.Equal(CsvIssueKind.StrayQuote, recs[1].Issue!.Kind);
        Assert.Equal(new[] { "xy", "z" }, recs[1].Fields);
    }

    [Fact]
    public void LineNumbersCountEmbeddedNewlines()
    {
        var recs = T.Records("h\n\"a\nb\nc\"\n\nx\r\ny\n");
        Assert.Equal(new long[] { 1, 2, 6, 7 }, recs.Select(r => r.LineNumber).ToArray());
        Assert.Equal(new long[] { 1, 2, 3, 4 }, recs.Select(r => r.RecordNumber).ToArray());
    }

    [Fact]
    public void LoneCrCountsAsLineBreakInsideQuotes()
    {
        var recs = T.Records("\"a\rb\"\nnext\n");
        Assert.Equal(3, recs[1].LineNumber);
    }

    [Fact]
    public void OversizedFieldIsTruncatedAndFlagged()
    {
        var recs = T.Records("a,b\n" + new string('x', 50) + ",y\n", new CsvOptions { MaxFieldSize = 10 });
        Assert.Equal(CsvIssueKind.FieldTooLong, recs[1].Issue!.Kind);
        Assert.Equal(10, recs[1].Fields[0].Length);
        Assert.Equal("y", recs[1].Fields[1]);
    }

    [Fact]
    public void FieldExactlyAtLimitIsFine()
    {
        var recs = T.Records("a\n" + new string('x', 10) + "\n", new CsvOptions { MaxFieldSize = 10 });
        Assert.Null(recs[1].Issue);
        Assert.Equal(10, recs[1].Fields[0].Length);
    }

    [Fact]
    public void TooManyFieldsAreDiscardedAndFlagged()
    {
        var recs = T.Records("a\n" + string.Join(",", Enumerable.Range(0, 20)) + "\n",
            new CsvOptions { MaxFieldsPerRecord = 5, ReportRagged = false });
        Assert.Equal(CsvIssueKind.TooManyFields, recs[1].Issue!.Kind);
        Assert.Equal(5, recs[1].Fields.Length);
    }

    [Fact]
    public void RecordSizeLimitBoundsTotalCharacters()
    {
        string row = string.Join(",", Enumerable.Repeat(new string('z', 100), 50));
        var recs = T.Records("a\n" + row + "\n", new CsvOptions { MaxRecordSize = 1000, ReportRagged = false });
        Assert.Equal(CsvIssueKind.FieldTooLong, recs[1].Issue!.Kind);
        Assert.True(recs[1].Fields.Sum(f => f.Length) <= 1000);
    }

    [Fact]
    public void RawTextIsExactIncludingEmbeddedNewlines()
    {
        var recs = T.Records("h1,h2\r\n\"a\r\nb\",\"q\"\"\"\nplain,row\n");
        Assert.Equal("h1,h2", recs[0].Raw);
        Assert.Equal("\"a\r\nb\",\"q\"\"\"", recs[1].Raw);
        Assert.Equal("plain,row", recs[2].Raw);
    }

    [Fact]
    public void RawCanBeDisabledAndIsTruncatedWhenHuge()
    {
        Assert.Null(T.Records("a\nb\n", new CsvOptions { CaptureRaw = false })[1].Raw);
        var big = T.Records("a\n" + new string('x', 5000) + "\n", new CsvOptions { MaxFieldSize = 10, MaxRecordSize = 10 })[1];
        Assert.True(big.RawTruncated);
        Assert.True(big.Raw!.Length < 200);
    }

    [Fact]
    public void RaggedRowAfterBlankLinesUsesFirstRecordAsHeader()
    {
        var recs = T.Records("\n\na,b\n1\n");
        Assert.Equal(CsvIssueKind.RaggedRow, recs[1].Issue!.Kind);
        Assert.Equal(4, recs[1].Issue!.Line);
    }

    [Fact]
    public void MemoryStaysBoundedForAnEndlessQuotedField()
    {
        // 20 million characters inside one never-closed quote; only MaxFieldSize chars may be kept
        IEnumerable<string> Chunks()
        {
            yield return "a\n\"";
            for (int i = 0; i < 20_000; i++) yield return new string('q', 1000);
        }

        var recs = CsvReader.Read(new CsvEtl.Synthetic.EnumerableTextReader(Chunks()), new CsvOptions { MaxFieldSize = 1000 }).ToList();
        Assert.Equal(2, recs.Count);
        Assert.Equal(CsvIssueKind.UnterminatedQuote, recs[1].Issue!.Kind);
        Assert.Equal(1000, recs[1].Fields[0].Length);
        Assert.True(recs[1].Raw!.Length <= 2 * 4_194_304 + 64);
    }

    [Fact]
    public void FileReaderStripsUtf8BomAndHandlesUtf16()
    {
        string dir = T.TempDir();
        try
        {
            string p8 = Path.Combine(dir, "u8.csv");
            File.WriteAllBytes(p8, new byte[] { 0xEF, 0xBB, 0xBF }.Concat("a,b\n1,2\n"u8.ToArray()).ToArray());
            using (var r = CsvReader.OpenFile(p8)) Assert.Equal(new[] { "a", "b" }, CsvReader.Read(r).First().Fields);

            string p16 = Path.Combine(dir, "u16.csv");
            File.WriteAllText(p16, "é,b\n", System.Text.Encoding.Unicode);
            using (var r = CsvReader.OpenFile(p16)) Assert.Equal(new[] { "é", "b" }, CsvReader.Read(r).First().Fields);

            string pl1 = Path.Combine(dir, "l1.csv");
            File.WriteAllBytes(pl1, new byte[] { 0x63, 0x61, 0x66, 0xE9, 0x0A });
            using (var r = CsvReader.OpenFile(pl1, System.Text.Encoding.Latin1)) Assert.Equal("café", CsvReader.Read(r).First().Fields[0]);
        }
        finally { Directory.Delete(dir, true); }
    }
}

public class CsvWriterTests
{
    private static string Write(string?[][] rows, char delim = ',', string nl = "\n")
    {
        var sw = new StringWriter();
        var w = new CsvWriter(sw, delim, nl);
        foreach (var r in rows) w.WriteRow(r);
        return sw.ToString();
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("two\nlines", "\"two\nlines\"")]
    [InlineData("cr\rhere", "\"cr\rhere\"")]
    [InlineData(" lead", "\" lead\"")]
    [InlineData("trail ", "\"trail \"")]
    [InlineData("﻿bom", "\"﻿bom\"")]
    [InlineData("", "")]
    [InlineData("é😀", "é😀")]
    public void QuotesOnlyWhenNeeded(string field, string expected) =>
        Assert.Equal(expected + ",x\n", Write(new[] { new string?[] { field, "x" } }));

    [Fact]
    public void SingleEmptyFieldRowIsQuotedSoItIsNotABlankLine() =>
        Assert.Equal("\"\"\n", Write(new[] { new string?[] { "" } }));

    [Fact]
    public void NullIsWrittenAsEmpty() => Assert.Equal("a,,c\n", Write(new[] { new string?[] { "a", null, "c" } }));

    [Fact]
    public void NewlineAndDelimiterAreConfigurable() =>
        Assert.Equal("a;\"b;c\"\r\n", Write(new[] { new string?[] { "a", "b;c" } }, ';', "\r\n"));

    [Fact]
    public void ZeroFieldRowIsRejected() =>
        Assert.Throws<ArgumentException>(() => new CsvWriter(new StringWriter()).WriteRow(Array.Empty<string?>()));

    [Fact]
    public void CountsRowsWritten()
    {
        var w = new CsvWriter(new StringWriter());
        w.WriteRow(new[] { "a" });
        w.WriteRow(new[] { "b" });
        Assert.Equal(2, w.RowsWritten);
    }
}
