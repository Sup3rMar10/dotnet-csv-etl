using System.Globalization;
using CsvEtl.Cli;
using CsvEtl.Csv;
using CsvEtl.Pipeline;
using CsvEtl.Schema;

namespace CsvEtl.Tests;

/// <summary>Locale traps. Every test sets CurrentCulture explicitly; the whole suite can also be run under another
/// culture with the CSVETL_TEST_CULTURE environment variable (see README).</summary>
public class CultureTests
{
    public static IEnumerable<object[]> Cultures() =>
        new[] { "en-US", "de-DE", "fr-FR", "tr-TR", "ar-SA", "th-TH", "ja-JP" }.Select(c => new object[] { c });

    [Theory]
    [MemberData(nameof(Cultures))]
    public void DecimalsUseTheDotNotTheCurrentCulture(string culture) => T.WithCulture(culture, () =>
    {
        var c = T.Col("\"type\":\"decimal\"");
        Assert.Equal(1234.56m, ColumnEvaluator.Evaluate(c, "1234.56").Value);
        Assert.Equal("type", ColumnEvaluator.Evaluate(c, "1,5").Failure?.Rule);
        Assert.Equal("type", ColumnEvaluator.Evaluate(c, "1.234,56").Failure?.Rule);
        Assert.Equal("1234.5", ColumnEvaluator.Format(c, 1234.5m));
    });

    [Theory]
    [MemberData(nameof(Cultures))]
    public void IntsIgnoreCultureGroupSeparators(string culture) => T.WithCulture(culture, () =>
    {
        var c = T.Col("\"type\":\"int\"");
        Assert.Equal(1234L, ColumnEvaluator.Evaluate(c, "1234").Value);
        Assert.Equal("type", ColumnEvaluator.Evaluate(c, "1 234").Failure?.Rule);
        Assert.Equal("type", ColumnEvaluator.Evaluate(c, "1 234").Failure?.Rule);
        Assert.Equal("type", ColumnEvaluator.Evaluate(c, "1.234").Failure?.Rule);
        Assert.Equal("-5", ColumnEvaluator.Format(c, -5L));
    });

    [Theory]
    [MemberData(nameof(Cultures))]
    public void DatesAreParsedAndFormattedInvariantly(string culture) => T.WithCulture(culture, () =>
    {
        var c = T.Col("\"type\":\"date\",\"formats\":[\"yyyy-MM-dd\",\"dd/MM/yyyy\",\"dd.MM.yyyy\",\"d MMM yyyy\"]");
        foreach (var raw in new[] { "2024-03-05", "05/03/2024", "05.03.2024", "5 Mar 2024" })
        {
            var ev = ColumnEvaluator.Evaluate(c, raw);
            Assert.True(ev.Failure == null, $"{culture}: {raw}: {ev.Failure?.Message}");
            Assert.Equal(new DateOnly(2024, 3, 5), ev.Value);
            Assert.Equal("2024-03-05", ColumnEvaluator.Format(c, ev.Value));
        }

        Assert.Equal("type", ColumnEvaluator.Evaluate(c, "5 Mär 2024").Failure?.Rule);
    });

    [Theory]
    [MemberData(nameof(Cultures))]
    public void CaseMappingIsInvariantEvenInTurkish(string culture) => T.WithCulture(culture, () =>
    {
        Assert.Equal("title info", ColumnEvaluator.ApplyCase(CaseMode.Lower, "TITLE INFO"));
        Assert.Equal("TITLE INFO", ColumnEvaluator.ApplyCase(CaseMode.Upper, "title info"));
        Assert.Equal("Title Info", ColumnEvaluator.ApplyCase(CaseMode.Title, "tITLE iNFO"));
    });

    [Theory]
    [MemberData(nameof(Cultures))]
    public void BoolAndEnumMatchingIsCultureIndependent(string culture) => T.WithCulture(culture, () =>
    {
        Assert.Equal(true, ColumnEvaluator.Evaluate(T.Col("\"type\":\"bool\""), "TRUE").Value);
        var e = T.Col("\"type\":\"enum\",\"allowed\":[\"InFo\"]");
        Assert.Equal("InFo", ColumnEvaluator.Evaluate(e, "INFO").Value);
        Assert.Equal("InFo", ColumnEvaluator.Evaluate(e, "info").Value);
    });

    [Fact]
    public void NaiveParsingWouldHaveGotItWrong()
    {
        // documents the trap this project avoids: the same text means different numbers in different cultures
        var fr = CultureInfo.GetCultureInfo("fr-FR");
        var us = CultureInfo.GetCultureInfo("en-US");
        Assert.Equal(1.5m, decimal.Parse("1,5", NumberStyles.Number, fr));
        Assert.Equal(15m, decimal.Parse("1,5", NumberStyles.Number, us));
        var c = T.Col("\"type\":\"decimal\"");
        T.WithCulture("fr-FR", () => Assert.NotNull(ColumnEvaluator.Evaluate(c, "1,5").Failure));
        T.WithCulture("en-US", () => Assert.NotNull(ColumnEvaluator.Evaluate(c, "1,5").Failure));
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("fr-FR")]
    [InlineData("tr-TR")]
    [InlineData("th-TH")]
    public void WholePipelineOutputIsByteIdenticalAcrossCultures(string culture)
    {
        string Run()
        {
            var clean = new StringWriter();
            var jsonl = new StringWriter();
            using var reader = CsvReader.OpenFile(T.SamplePath("customers_messy.csv"));
            var r = EtlRunner.Run(reader, T.SampleSchema(), new EtlOptions(),
                new ICleanSink[] { new CleanCsvSink(clean), new JsonlSink(jsonl) }, new RejectsCsvSink(new StringWriter()));
            return clean + "\n----\n" + jsonl + "\n----\n" + ReportWriter.ToJson(r, "s", "i");
        }

        string baseline = "";
        T.WithCulture("en-US", () => baseline = Run());
        string actual = "";
        T.WithCulture(culture, () => actual = Run());
        Assert.Equal(baseline, actual);
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("fr-FR")]
    public void CliOutputIsIdenticalAcrossCultures(string culture)
    {
        string Run()
        {
            var sw = new StringWriter();
            var err = new StringWriter();
            int code = CliApp.Run(new[] { "clean", "--schema", T.SamplePath("customers.schema.json"), "--input", T.SamplePath("customers_messy.csv") },
                TextReader.Null, sw, err);
            Assert.Equal(0, code);
            return sw + "\n----\n" + err;
        }

        string baseline = "";
        T.WithCulture("en-US", () => baseline = Run());
        string actual = "";
        T.WithCulture(culture, () => actual = Run());
        Assert.Equal(baseline, actual);
    }

    [Fact]
    public void SyntheticDataIsTheSameUnderAnyCulture()
    {
        string Gen() { var sw = new StringWriter(); CsvEtl.Synthetic.SyntheticCustomers.WriteTo(sw, 300, 5); return sw.ToString(); }
        string a = "", b = "";
        T.WithCulture("en-US", () => a = Gen());
        T.WithCulture("de-DE", () => b = Gen());
        Assert.Equal(a, b);
    }

    [Fact]
    public void CommaDecimalColumnWorksUnderEnglishCultureToo() =>
        T.WithCulture("en-US", () =>
        {
            var c = T.Col("\"type\":\"decimal\",\"decimalSeparator\":\",\"");
            Assert.Equal(1234.5m, ColumnEvaluator.Evaluate(c, "1234,5").Value);
        });
}
