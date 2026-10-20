using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CsvEtl.Cli;
using CsvEtl.Csv;

namespace CsvEtl.Tests;

public class CliTests : IDisposable
{
    private readonly string _dir = T.TempDir();
    private static string Schema => T.SamplePath("customers.schema.json");
    private static string Messy => T.SamplePath("customers_messy.csv");

    public void Dispose() => Directory.Delete(_dir, true);

    private string File_(string name, string content)
    {
        string p = Path.Combine(_dir, name);
        File.WriteAllText(p, content, new UTF8Encoding(false));
        return p;
    }

    private string CleanFile() => File_("clean.csv",
        "customer_id,full_name,balance,signup_date\nC000001,Alex Example,10.50,2024-01-02\nC000002,Sam Sample,0,2023-05-06\n");

    private string DirtyFile() => File_("dirty.csv",
        "customer_id,full_name,balance,signup_date\nC000001,Alex Example,10.50,2024-01-02\nBAD,Sam Sample,0,2023-05-06\nC000003,Jo Test,abc,2023-05-06\n");

    private static (int Code, string Out, string Err) RunIn(string stdin, params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        int code = CliApp.Run(args, new StringReader(stdin), o, e);
        return (code, o.ToString(), e.ToString());
    }

    private static (int Code, string Out, string Err) Run(params string[] args) => RunIn("", args);

    // ---- usage errors: exit 2 ----

    [Fact]
    public void NoArgumentsPrintsUsageAndExits2()
    {
        var r = Run();
        Assert.Equal(2, r.Code);
        Assert.Contains("Usage:", r.Err);
        Assert.Equal("", r.Out);
    }

    [Fact]
    public void HelpExits0AndDocumentsExitCodes()
    {
        var r = Run("--help");
        Assert.Equal(0, r.Code);
        Assert.Contains("Exit codes:", r.Out);
        Assert.Contains("3  I/O error", r.Out);
    }

    [Fact]
    public void CommandHelpListsOptions()
    {
        var r = Run("clean", "--help");
        Assert.Equal(0, r.Code);
        Assert.Contains("--fail-on-reject", r.Out);
        Assert.Contains("--rejects", r.Out);
    }

    [Fact]
    public void VersionExits0()
    {
        var r = Run("--version");
        Assert.Equal(0, r.Code);
        Assert.Equal("csvetl 1.0.0", r.Out.Trim());
    }

    [Fact]
    public void UnknownCommandSuggestsTheClosest()
    {
        var r = Run("validat");
        Assert.Equal(2, r.Code);
        Assert.Contains("Did you mean 'validate'?", r.Err);
    }

    [Fact]
    public void UnknownOptionSuggestsTheClosest()
    {
        var r = Run("validate", "--schmea", Schema, "--input", Messy);
        Assert.Equal(2, r.Code);
        Assert.Contains("Did you mean --schema?", r.Err);
    }

    [Fact]
    public void MissingRequiredOptionNamesIt()
    {
        var r = Run("validate", "--schema", Schema);
        Assert.Equal(2, r.Code);
        Assert.Contains("missing required option --input", r.Err);
    }

    [Fact]
    public void OptionWithoutValueIsAUsageError()
    {
        var r = Run("validate", "--schema", Schema, "--input");
        Assert.Equal(2, r.Code);
        Assert.Contains("--input needs a value", r.Err);
    }

    [Theory]
    [InlineData("--max-errors", "abc", "expects an integer")]
    [InlineData("--max-errors", "-1", "must be between")]
    [InlineData("--dedupe", "bloom", "--dedupe must be")]
    [InlineData("--delimiter", "ab", "--delimiter must be")]
    [InlineData("--encoding", "ebcdic", "--encoding must be")]
    [InlineData("--format", "xml", "--format must be")]
    [InlineData("--max-field-size", "0", "must be between")]
    [InlineData("--max-keys", "0", "must be between")]
    public void BadOptionValuesExit2(string option, string value, string message)
    {
        var r = Run("validate", "--schema", Schema, "--input", Messy, option, value);
        Assert.Equal(2, r.Code);
        Assert.Contains(message, r.Err);
    }

    [Fact]
    public void PositionalArgumentsAndRepeatedOptionsExit2()
    {
        Assert.Contains("unexpected argument 'extra'", Run("validate", "--schema", Schema, "--input", Messy, "extra").Err);
        Assert.Contains("given more than once", Run("validate", "--schema", Schema, "--schema", Schema, "--input", Messy).Err);
        Assert.Contains("takes no value", Run("clean", "--schema", Schema, "--input", Messy, "--fail-on-reject=1").Err);
    }

    [Fact]
    public void EqualsSyntaxAndShortOptionsWork()
    {
        var r = Run("validate", "-s", Schema, "--input=" + Messy, "--format=json", "--max-errors=3");
        Assert.Equal(1, r.Code);
        using var doc = JsonDocument.Parse(r.Out);
        Assert.Equal(3, doc.RootElement.GetProperty("summary").GetProperty("storedIssues").GetInt64());
    }

    [Fact]
    public void InvalidSchemaExits2AndListsErrorsOnStderr()
    {
        string bad = File_("bad.json", "{\"columns\":[{\"name\":\"a\",\"type\":\"float\"}]}");
        var r = Run("validate", "--schema", bad, "--input", Messy);
        Assert.Equal(2, r.Code);
        Assert.Contains("is invalid", r.Err);
        Assert.Contains("unknown type", r.Err);
        Assert.Equal("", r.Out);
    }

    // ---- I/O and fatal: exit 3 ----

    [Fact]
    public void MissingInputFileExits3()
    {
        var r = Run("validate", "--schema", Schema, "--input", Path.Combine(_dir, "nope.csv"));
        Assert.Equal(3, r.Code);
        Assert.Contains("I/O error", r.Err);
    }

    [Fact]
    public void MissingSchemaFileExits3() =>
        Assert.Equal(3, Run("validate", "--schema", Path.Combine(_dir, "nope.json"), "--input", Messy).Code);

    [Fact]
    public void UnwritableOutputExits3()
    {
        var r = Run("clean", "--schema", Schema, "--input", CleanFile(), "--output", Path.Combine(_dir, "no", "such", "dir", "out.csv"));
        Assert.Equal(3, r.Code);
        Assert.Contains("I/O error", r.Err);
    }

    // ---- data results: exit 0 / 1 ----

    [Fact]
    public void ValidateCleanFileExits0()
    {
        var r = Run("validate", "--schema", Schema, "--input", CleanFile());
        Assert.Equal(0, r.Code);
        Assert.Contains("rows rejected  : 0", r.Out);
    }

    [Fact]
    public void ValidateWithRejectsExits1AndReportGoesToStdout()
    {
        var r = Run("validate", "--schema", Schema, "--input", DirtyFile());
        Assert.Equal(1, r.Code);
        Assert.Contains("rows rejected  : 2", r.Out);
        Assert.Contains("customer_id", r.Out);
        Assert.Equal("", r.Err);
    }

    [Fact]
    public void ValidateJsonFormatIsValidJson()
    {
        var r = Run("validate", "--schema", Schema, "--input", DirtyFile(), "--format", "json");
        Assert.Equal(1, r.Code);
        using var doc = JsonDocument.Parse(r.Out);
        Assert.Equal(2, doc.RootElement.GetProperty("summary").GetProperty("rowsRejected").GetInt64());
    }

    [Fact]
    public void UnusableHeaderExits1()
    {
        var r = Run("validate", "--schema", Schema, "--input", File_("h.csv", "foo,bar\n1,2\n"));
        Assert.Equal(1, r.Code);
        Assert.Contains("required column 'customer_id' not found", r.Out);
    }

    [Fact]
    public void CleanWithRejectsStillExits0ByDefault() =>
        Assert.Equal(0, Run("clean", "--schema", Schema, "--input", DirtyFile()).Code);

    [Fact]
    public void FailOnRejectTurnsRejectsIntoExit1() =>
        Assert.Equal(1, Run("clean", "--schema", Schema, "--input", DirtyFile(), "--fail-on-reject").Code);

    [Fact]
    public void FailOnRejectDoesNotFireOnCleanData() =>
        Assert.Equal(0, Run("clean", "--schema", Schema, "--input", CleanFile(), "--fail-on-reject").Code);

    [Fact]
    public void CleanWithUnusableHeaderExits1EvenWithoutFailOnReject() =>
        Assert.Equal(1, Run("clean", "--schema", Schema, "--input", File_("h.csv", "foo\n1\n")).Code);

    // ---- stdout is data, stderr is diagnostics ----

    [Fact]
    public void CleanWritesOnlyDataToStdoutAndSummaryToStderr()
    {
        var r = Run("clean", "--schema", Schema, "--input", DirtyFile());
        Assert.StartsWith("customer_id,full_name,email,age,balance,signup_date,tier,marketing_opt_in,notes\n", r.Out);
        Assert.Contains("C000001,Alex Example,,,10.50,2024-01-02,bronze,false,\n", r.Out);
        Assert.DoesNotContain("rows read", r.Out);
        Assert.Contains("rows read      : 3", r.Err);
        Assert.Contains("rows rejected  : 2", r.Err);
    }

    [Fact]
    public void CleanWritesAllOutputFiles()
    {
        string o = Path.Combine(_dir, "out.csv"), j = Path.Combine(_dir, "out.jsonl"), rj = Path.Combine(_dir, "rej.csv"), rp = Path.Combine(_dir, "rep.json");
        var r = Run("clean", "--schema", Schema, "--input", DirtyFile(), "--output", o, "--jsonl", j, "--rejects", rj, "--report", rp);
        Assert.Equal(0, r.Code);
        Assert.Equal("", r.Out);
        Assert.Equal(2, File.ReadAllLines(o).Length);
        var line = Assert.Single(File.ReadAllLines(j));
        using (var doc = JsonDocument.Parse(line)) Assert.Equal("C000001", doc.RootElement.GetProperty("customer_id").GetString());
        var rejects = CsvReader.ReadString(File.ReadAllText(rj), T.Loose).ToList();
        Assert.Equal(3, rejects.Count);
        Assert.Equal("BAD,Sam Sample,0,2023-05-06", rejects[1].Fields[3]);
        using var report = JsonDocument.Parse(File.ReadAllText(rp));
        Assert.Equal(2, report.RootElement.GetProperty("summary").GetProperty("rowsRejected").GetInt64());
        Assert.False(File.ReadAllBytes(o).Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }), "outputs must not start with a BOM");
    }

    [Fact]
    public void NewlineOptionControlsCsvTerminators()
    {
        var r = Run("clean", "--schema", Schema, "--input", CleanFile(), "--newline", "crlf");
        Assert.Contains("\r\n", r.Out);
        Assert.DoesNotContain("\n", r.Out.Replace("\r\n", ""));
    }

    [Fact]
    public void ProgressGoesToStderrAndDoesNotChangeStdout()
    {
        var plain = Run("clean", "--schema", Schema, "--input", Messy);
        var withProgress = Run("clean", "--schema", Schema, "--input", Messy, "--progress", "--progress-every", "50");
        Assert.Equal(plain.Out, withProgress.Out);
        Assert.Contains("[csvetl] 50 rows read", withProgress.Err);
        Assert.Contains("[csvetl] 200 rows read", withProgress.Err);
        Assert.DoesNotContain("[csvetl]", plain.Err);
    }

    [Fact]
    public void MaxErrorsCapsTheListedIssuesOnly()
    {
        var r = Run("validate", "--schema", Schema, "--input", Messy, "--format", "json", "--max-errors", "2");
        using var doc = JsonDocument.Parse(r.Out);
        var s = doc.RootElement.GetProperty("summary");
        Assert.Equal(2, s.GetProperty("storedIssues").GetInt64());
        Assert.True(s.GetProperty("totalIssues").GetInt64() > 2);
        Assert.Equal(s.GetProperty("totalIssues").GetInt64() - 2, s.GetProperty("droppedIssues").GetInt64());
    }

    [Fact]
    public void CleanReadsFromStdin()
    {
        var r = RunIn("customer_id,full_name,balance,signup_date\nC000009,Pat Sample,1,2024-01-01\n", "clean", "--schema", Schema, "--input", "-");
        Assert.Equal(0, r.Code);
        Assert.Contains("C000009,Pat Sample", r.Out);
    }

    [Fact]
    public void SemicolonDelimiterAndLatin1Encoding()
    {
        string p = Path.Combine(_dir, "semi.csv");
        File.WriteAllBytes(p, Encoding.Latin1.GetBytes("customer_id;full_name;balance;signup_date\nC000001;José Example;10.5;2024-01-02\n"));
        string o = Path.Combine(_dir, "o.csv");
        var r = Run("clean", "--schema", Schema, "--input", p, "--delimiter", "semicolon", "--encoding", "latin1", "--output", o);
        Assert.Equal(0, r.Code);
        Assert.Contains("José Example", File.ReadAllText(o, Encoding.UTF8));
    }

    [Fact]
    public void RefusesToOverwriteTheInputOrReuseAnOutputPath()
    {
        string input = CleanFile();
        var a = Run("clean", "--schema", Schema, "--input", input, "--output", input);
        Assert.Equal(2, a.Code);
        Assert.Contains("would overwrite the input", a.Err);
        string same = Path.Combine(_dir, "same.out");
        var b = Run("clean", "--schema", Schema, "--input", input, "--output", same, "--rejects", same);
        Assert.Equal(2, b.Code);
        Assert.Contains("must be different files", b.Err);
        Assert.Equal(2, Run("clean", "--schema", Schema, "--input", input, "--jsonl", "-").Code);
        Assert.Equal(File.ReadAllText(input), File.ReadAllText(input)); // untouched
    }

    // ---- schema-check and sample ----

    [Fact]
    public void SchemaCheckOkExits0AndListsColumns()
    {
        var r = Run("schema-check", "--schema", Schema);
        Assert.Equal(0, r.Code);
        Assert.Contains("schema OK: 'customers', 9 column(s), key: customer_id", r.Out);
    }

    [Fact]
    public void SchemaCheckInvalidExits2()
    {
        string bad = File_("bad.json", "{\"columns\":[{\"name\":\"a\",\"requierd\":true}]}");
        var r = Run("schema-check", "--schema", bad);
        Assert.Equal(2, r.Code);
        Assert.Contains("schema INVALID", r.Out);
        Assert.Contains("did you mean \"required\"", r.Out);
    }

    [Fact]
    public void SchemaCheckMatchesAHeader()
    {
        var ok = Run("schema-check", "--schema", Schema, "--input", Messy);
        Assert.Equal(0, ok.Code);
        Assert.Contains("customer_id        <- 'Cust ID' (field 1)", ok.Out);
        var bad = Run("schema-check", "--schema", Schema, "--input", File_("h.csv", "foo,bar\n"));
        Assert.Equal(1, bad.Code);
        Assert.Contains("ERROR: required column 'customer_id'", bad.Out);
    }

    [Fact]
    public void SampleIsDeterministicPerSeed()
    {
        var a = Run("sample", "--rows", "50", "--seed", "7");
        var b = Run("sample", "--rows", "50", "--seed", "7");
        var c = Run("sample", "--rows", "50", "--seed", "8");
        Assert.Equal(0, a.Code);
        Assert.Equal(a.Out, b.Out);
        Assert.NotEqual(a.Out, c.Out);
    }

    [Fact]
    public void CommittedSampleFileIsExactlyWhatTheGeneratorProduces()
    {
        string o = Path.Combine(_dir, "regen.csv");
        Assert.Equal(0, Run("sample", "--rows", "200", "--seed", "42", "--bom", "--output", o).Code);
        Assert.Equal(File.ReadAllBytes(Messy), File.ReadAllBytes(o));
    }

    [Theory]
    [InlineData("--dirty-rate", "2")]
    [InlineData("--dirty-rate", "abc")]
    [InlineData("--rows", "-5")]
    public void SampleRejectsBadNumbers(string option, string value) =>
        Assert.Equal(2, Run("sample", option, value).Code);

    [Fact]
    public void SampleZeroDirtRateIsFullyClean()
    {
        string o = Path.Combine(_dir, "pure.csv");
        Run("sample", "--rows", "300", "--seed", "3", "--dirty-rate", "0", "--output", o);
        var r = Run("validate", "--schema", Schema, "--input", o);
        Assert.Equal(0, r.Code);
    }

    // ---- the real process ----

    private static (int Code, string Out, string Err) Spawn(params string[] args)
    {
        string dll = Path.Combine(AppContext.BaseDirectory, "csvetl.dll");
        Assert.True(File.Exists(dll), "csvetl.dll missing next to the tests");
        var psi = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add(dll);
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardInput.Close();
        var err = p.StandardError.ReadToEndAsync();
        string output = p.StandardOutput.ReadToEnd();
        if (!p.WaitForExit(60_000)) { p.Kill(true); throw new TimeoutException("csvetl process hung"); }
        return (p.ExitCode, output, err.Result);
    }

    [Fact]
    public void RealProcessValidateDirtyExits1()
    {
        var r = Spawn("validate", "--schema", Schema, "--input", Messy, "--max-errors", "5");
        Assert.Equal(1, r.Code);
        Assert.Contains("rows read      : 200", r.Out);
        Assert.Equal("", r.Err);
    }

    [Fact]
    public void RealProcessCleanKeepsStdoutForDataOnly()
    {
        var r = Spawn("clean", "--schema", Schema, "--input", Messy);
        Assert.Equal(0, r.Code);
        Assert.StartsWith("customer_id,", r.Out);
        Assert.Contains("rows read", r.Err);
        Assert.DoesNotContain("rows read", r.Out);
        // stdout is valid CSV with a constant column count
        Assert.All(CsvReader.ReadString(r.Out), rec => Assert.Null(rec.Issue));
    }

    [Fact]
    public void RealProcessUsageAndIoExitCodes()
    {
        Assert.Equal(2, Spawn("bogus").Code);
        Assert.Equal(3, Spawn("validate", "--schema", Schema, "--input", Path.Combine(_dir, "missing.csv")).Code);
        Assert.Equal(0, Spawn("--version").Code);
    }

    [Fact]
    public void RealProcessSamplePipedIntoValidateViaStdin()
    {
        var sample = Spawn("sample", "--rows", "30", "--seed", "1", "--dirty-rate", "0");
        Assert.Equal(0, sample.Code);
        string p = File_("piped.csv", sample.Out);
        Assert.Equal(0, Spawn("validate", "--schema", Schema, "--input", p).Code);
    }
}
