using System.Text;
using CsvEtl.Csv;
using CsvEtl.Pipeline;
using Microsoft.VisualBasic.FileIO;
using Xunit.Abstractions;

namespace CsvEtl.Tests;

public class CsvPropertyTests(ITestOutputHelper output)
{
    private static readonly string[] Atoms =
    {
        "a", "b", "x", "0", " ", "  ", ",", ";", "\t", "|", "\"", "\"\"", "\r", "\n", "\r\n", "é", "中", "😀", "\uFEFF", "'", "\\", "=",
    };

    private static string RandomField(Random rng, string[] atoms)
    {
        int n = rng.Next(0, 9);
        var sb = new StringBuilder();
        for (int i = 0; i < n; i++) sb.Append(atoms[rng.Next(atoms.Length)]);
        return sb.ToString();
    }

    private static string[][] RandomRows(Random rng, string[] atoms, int maxRows = 6, int maxWidth = 5)
    {
        int rows = rng.Next(1, maxRows + 1);
        return Enumerable.Range(0, rows).Select(_ => Enumerable.Range(0, rng.Next(1, maxWidth + 1)).Select(_ => RandomField(rng, atoms)).ToArray()).ToArray();
    }

    private static string Serialise(string[][] rows, char delim, string nl)
    {
        var sw = new StringWriter();
        var w = new CsvWriter(sw, delim, nl);
        foreach (var r in rows) w.WriteRow(r);
        return sw.ToString();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(20260101)]
    public void WriteThenReadReturnsTheSameRows(int seed)
    {
        var rng = new Random(seed);
        char[] delims = { ',', ';', '\t', '|' };
        string[] newlines = { "\n", "\r\n", "\r" };
        for (int iter = 0; iter < 1500; iter++)
        {
            var rows = RandomRows(rng, Atoms);
            char d = delims[rng.Next(delims.Length)];
            string nl = newlines[rng.Next(newlines.Length)];
            string text = Serialise(rows, d, nl);
            var back = T.Parse(text, new CsvOptions { Delimiter = d, ReportRagged = false, BufferSize = rng.Next(16, 70) });
            Assert.True(rows.Length == back.Length, $"seed {seed} iter {iter}: row count {rows.Length} vs {back.Length} for text {Escape(text)}");
            for (int i = 0; i < rows.Length; i++)
                Assert.True(rows[i].SequenceEqual(back[i]), $"seed {seed} iter {iter} row {i}: {Escape(string.Join("|", rows[i]))} vs {Escape(string.Join("|", back[i]))} text {Escape(text)}");
        }
    }

    [Fact]
    public void RawTextOfEveryRecordReparsesToTheSameFields()
    {
        var rng = new Random(77);
        for (int iter = 0; iter < 500; iter++)
        {
            var rows = RandomRows(rng, Atoms);
            string text = Serialise(rows, ',', rng.Next(2) == 0 ? "\n" : "\r\n");
            foreach (var rec in CsvReader.ReadString(text, T.Loose))
            {
                var again = T.Parse(rec.Raw!);
                Assert.Single(again);
                Assert.Equal(rec.Fields, again[0]);
            }
        }
    }

    [Fact]
    public void FuzzArbitraryBytesNeverThrow()
    {
        var rng = new Random(424242);
        byte[] special = Encoding.UTF8.GetBytes("\"\",;\r\n\t |");
        var optionSets = new[]
        {
            new CsvOptions(),
            new CsvOptions { MaxFieldSize = 8, MaxFieldsPerRecord = 4, MaxRecordSize = 20 },
            new CsvOptions { Delimiter = ';', SkipBlankLines = false, BufferSize = 16 },
            new CsvOptions { Delimiter = '\t', CaptureRaw = false },
        };
        long records = 0, flagged = 0;
        for (int iter = 0; iter < 3000; iter++)
        {
            var bytes = new byte[rng.Next(0, 600)];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = rng.Next(3) == 0 ? (byte)rng.Next(256) : special[rng.Next(special.Length)];
            string text = new StreamReader(new MemoryStream(bytes), new UTF8Encoding(false), true).ReadToEnd();
            foreach (var o in optionSets)
            {
                long lastLine = 0;
                foreach (var rec in CsvReader.ReadString(text, o))
                {
                    records++;
                    if (rec.Issue != null) flagged++;
                    Assert.NotEmpty(rec.Fields);
                    Assert.True(rec.Fields.Length <= o.MaxFieldsPerRecord);
                    Assert.All(rec.Fields, f => Assert.True(f.Length <= o.MaxFieldSize));
                    Assert.True(rec.LineNumber >= lastLine && rec.LineNumber >= 1);
                    lastLine = rec.LineNumber;
                }
            }
        }

        output.WriteLine($"fuzz: {records} records parsed, {flagged} carried a structured issue, 0 exceptions");
        Assert.True(records > 0);
    }

    [Fact]
    public void FuzzFullPipelineNeverThrowsAndCountsAddUp()
    {
        var rng = new Random(99);
        var schema = T.SampleSchema();
        byte[] special = Encoding.UTF8.GetBytes("\"\",\r\n C0123456789@.-");
        for (int iter = 0; iter < 800; iter++)
        {
            var sb = new StringBuilder("Cust ID,Full Name,Account Balance,Signup\n");
            int n = rng.Next(0, 300);
            for (int i = 0; i < n; i++) sb.Append((char)special[rng.Next(special.Length)]);
            var res = EtlRunner.Run(new StringReader(sb.ToString()), schema, new EtlOptions { MaxErrors = 5 });
            Assert.Equal(res.RowsRead, res.RowsClean + res.RowsRejected);
            Assert.True(res.Report.Issues.Count <= 5);
        }
    }

    // ---- independent oracle: Microsoft.VisualBasic.FileIO.TextFieldParser ----

    private static (string[][]? Rows, string? Error) Oracle(string text, char delim)
    {
        var rows = new List<string[]>();
        using var p = new TextFieldParser(new StringReader(text))
        {
            TextFieldType = FieldType.Delimited,
            HasFieldsEnclosedInQuotes = true,
            TrimWhiteSpace = false,
        };
        p.SetDelimiters(delim.ToString());
        try
        {
            string[]? f;
            while ((f = p.ReadFields()) != null) rows.Add(f);
        }
        catch (MalformedLineException e)
        {
            return (null, e.GetType().Name);
        }

        return (rows.ToArray(), null);
    }

    private static string Render(string[][] rows) => "[" + string.Join(" // ", rows.Select(r => string.Join("|", r.Select(Escape)))) + "]";

    private static string Escape(string s) => s.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");

    private static string[][] NormaliseNewlines(string[][] rows) =>
        rows.Select(r => r.Select(f => f.Replace("\r\n", "\n").Replace("\r", "\n")).ToArray()).ToArray();

    private static string[][] StripNewlines(string[][] rows) =>
        rows.Select(r => r.Select(f => f.Replace("\r", "").Replace("\n", "")).ToArray()).ToArray();

    private static bool SameRows(string[][] a, string[][] b) =>
        a.Length == b.Length && a.Zip(b).All(p => p.First.SequenceEqual(p.Second));

    [Fact]
    public void TextFieldParserAgreesOnTameWellFormedCorpus()
    {
        // quotes, delimiters, unicode, emoji, but no CR/LF and no leading/trailing spaces: expect a perfect match
        string[] tame = { "a", "b", "x", "0", ",", ";", "\"", "\"\"", "é", "中", "😀", "'", "\\", "=", "-" };
        var rng = new Random(2024);
        int compared = 0;
        for (int iter = 0; iter < 1500; iter++)
        {
            var rows = RandomRows(rng, tame);
            string text = Serialise(rows, ',', "\n");
            var (oracle, err) = Oracle(text, ',');
            var mine = T.Parse(text);
            Assert.True(err == null, $"oracle rejected well-formed text {Escape(text)}: {err}");
            // known difference: TextFieldParser skips a row that is a single empty field written as "" ? check below
            var expected = rows.Where(r => !(r.Length == 1 && r[0].Length == 0)).ToArray();
            Assert.True(SameRows(mine, rows), $"mine differs from input: {Escape(text)}");
            Assert.True(SameRows(oracle!, expected) || SameRows(oracle!, rows), $"oracle differs: {Escape(text)}");
            compared++;
        }

        output.WriteLine($"tame corpus: {compared} files compared");
    }

    [Fact]
    public void TextFieldParserComparisonOnNastyWellFormedCorpusIsCategorised()
    {
        var rng = new Random(31337);
        var counts = new SortedDictionary<string, int>();
        var examples = new Dictionary<string, string>();
        void Count(string cat, string text, string[][]? oracleRows = null, string[][]? mineRows = null)
        {
            counts[cat] = counts.GetValueOrDefault(cat) + 1;
            string detail = Escape(text) + (oracleRows == null ? "" : "  ORACLE=" + Render(oracleRows) + "  MINE=" + Render(mineRows!));
            if (!examples.TryGetValue(cat, out var old) || detail.Length < old.Length) examples[cat] = detail;
        }

        for (int iter = 0; iter < 3000; iter++)
        {
            var rows = RandomRows(rng, Atoms);
            string text = Serialise(rows, ',', "\n");
            var mine = T.Parse(text);
            Assert.True(SameRows(mine, rows), "own round trip must always hold: " + Escape(text));
            var (oracle, err) = Oracle(text, ',');
            if (err != null) Count("oracle throws " + err, text);
            else if (SameRows(oracle!, mine)) Count("identical", text);
            else if (SameRows(NormaliseNewlines(oracle!), NormaliseNewlines(mine))) Count("differs only in newline form inside quoted fields", text);
            else if (SameRows(StripNewlines(oracle!), StripNewlines(mine))) Count("differs only in the exact CR/LF content of quoted fields (oracle does not preserve embedded line breaks exactly)", text, oracle, mine);
            else if (SameRows(oracle!.Where(r => !(r.Length == 1 && r[0].Length == 0)).ToArray(), mine.Where(r => !(r.Length == 1 && r[0].Length == 0)).ToArray())) Count("differs only in empty-single-field rows", text);
            else if (rows.Any(r => r.Any(f => f.Contains('\r') || f.Contains('\n'))))
                Count("differs by more than CR/LF content, in files with embedded line breaks (oracle drops text around a CR inside quotes)", text, oracle, mine);
            else Count("OTHER", text, oracle, mine);
        }

        foreach (var kv in counts) output.WriteLine($"{kv.Key}: {kv.Value}   e.g. {examples[kv.Key]}");
        File.WriteAllLines(Path.Combine(AppContext.BaseDirectory, "textfieldparser-comparison.txt"),
            counts.Select(kv => $"{kv.Key}: {kv.Value}   e.g. {examples[kv.Key]}"));
        Assert.False(counts.ContainsKey("OTHER"), "unexplained differences: " + examples.GetValueOrDefault("OTHER"));
    }

    [Fact]
    public void TextFieldParserKnownBehaviourDifferences()
    {
        // Each assertion documents a real, observed difference (see README).
        // 1. blank lines: both skip them
        Assert.Equal(new[] { new[] { "a" }, new[] { "b" } }, Oracle("a\n\nb\n", ',').Rows!);
        Assert.Equal(new[] { new[] { "a" }, new[] { "b" } }, T.Parse("a\n\nb\n"));
        // 2. stray character after a closing quote: oracle throws, this reader keeps the data and flags the record
        Assert.NotNull(Oracle("\"x\"y,z\n", ',').Error);
        Assert.Equal(CsvIssueKind.StrayQuote, T.Records("\"x\"y,z\n")[0].Issue!.Kind);
        // 3. unterminated quote: oracle throws, this reader returns the record with an issue and the line number
        Assert.NotNull(Oracle("a,\"b\n", ',').Error);
        Assert.Equal(CsvIssueKind.UnterminatedQuote, T.Records("a,\"b\n")[0].Issue!.Kind);
    }
}
