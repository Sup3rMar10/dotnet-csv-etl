using System.Globalization;
using System.Text;
using CsvEtl.Csv;
using CsvEtl.Pipeline;
using CsvEtl.Schema;
using CsvEtl.Synthetic;
using CsvEtl.Validation;

namespace CsvEtl.Cli;

/// <summary>Exit codes: 0 success, 1 data problems (rejects / bad header), 2 usage or invalid schema, 3 I/O or fatal error.</summary>
public static class ExitCodes
{
    public const int Ok = 0;
    public const int DataProblems = 1;
    public const int Usage = 2;
    public const int Fatal = 3;
}

public static class CliApp
{
    public const string Version = "1.0.0";

    private static readonly OptionSpec Schema = new("schema", "s", false, "path to the JSON schema file", Required: true, Metavar: "FILE");
    private static readonly OptionSpec Input = new("input", "i", false, "input CSV file, or - for stdin", Required: true, Metavar: "FILE");

    private static readonly OptionSpec[] CsvSpecs =
    {
        new("delimiter", "d", false, "field delimiter: a single character, or comma|semicolon|tab|pipe (default comma)", ",", Metavar: "CHAR"),
        new("encoding", null, false, "fallback encoding when the file has no BOM: utf-8 (default), latin1, utf-16 (stdin is always UTF-8)", "utf-8", Metavar: "NAME"),
        new("max-field-size", null, false, "maximum characters kept per field (default 1048576)", "1048576", Metavar: "N"),
    };

    private static readonly OptionSpec[] EtlSpecs =
    {
        new("max-errors", null, false, "maximum issues listed in the report; totals are always exact (default 1000)", "1000", Metavar: "N"),
        new("dedupe", null, false, "duplicate detection on the schema key: hash (default), exact, off", "hash", Metavar: "MODE"),
        new("max-keys", null, false, "maximum distinct keys remembered for duplicate detection (default 5000000)", "5000000", Metavar: "N"),
        new("progress", null, true, "print progress to stderr"),
        new("progress-every", null, false, "rows between progress lines (default 100000)", "100000", Metavar: "N"),
    };

    private static OptionSpec[] Specs(string command) => command switch
    {
        "validate" => new[] { Schema, Input }.Concat(CsvSpecs).Concat(EtlSpecs).Append(
            new OptionSpec("format", "f", false, "report format: text (default) or json", "text", Metavar: "FMT")).ToArray(),
        "clean" => new[] { Schema, Input }.Concat(CsvSpecs).Concat(EtlSpecs).Concat(new[]
        {
            new OptionSpec("output", "o", false, "clean CSV output file, or - for stdout (default stdout)", "-", Metavar: "FILE"),
            new OptionSpec("jsonl", null, false, "also write clean rows as JSON Lines to this file", Metavar: "FILE"),
            new OptionSpec("rejects", null, false, "write rejected records (row, line, reasons, raw) to this CSV file", Metavar: "FILE"),
            new OptionSpec("report", null, false, "write the full JSON report to this file", Metavar: "FILE"),
            new OptionSpec("newline", null, false, "line terminator for CSV outputs: lf (default) or crlf", "lf", Metavar: "NL"),
            new OptionSpec("fail-on-reject", null, true, "exit 1 when any row was rejected"),
        }).ToArray(),
        "schema-check" => new[] { Schema, new OptionSpec("input", "i", false, "optional CSV file whose header is matched against the schema", Metavar: "FILE") }.Concat(CsvSpecs).ToArray(),
        "sample" => new[]
        {
            new OptionSpec("rows", "n", false, "number of data rows (default 100)", "100", Metavar: "N"),
            new OptionSpec("seed", null, false, "random seed (default 42)", "42", Metavar: "N"),
            new OptionSpec("dirty-rate", null, false, "probability that a row has a defect, 0..1 (default 0.15)", "0.15", Metavar: "P"),
            new OptionSpec("output", "o", false, "output file, or - for stdout (default stdout)", "-", Metavar: "FILE"),
            new OptionSpec("bom", null, true, "start the file with a UTF-8 BOM"),
        },
        _ => Array.Empty<OptionSpec>(),
    };

    public static int Run(string[] args, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        try
        {
            return Dispatch(args, stdin, stdout, stderr);
        }
        catch (UsageException e)
        {
            stderr.WriteLine($"csvetl: {e.Message}");
            stderr.WriteLine("Run 'csvetl --help' for usage.");
            return ExitCodes.Usage;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            stderr.WriteLine($"csvetl: I/O error: {e.Message}");
            return ExitCodes.Fatal;
        }
        catch (Exception e)
        {
            stderr.WriteLine($"csvetl: fatal: {e.GetType().Name}: {e.Message}");
            return ExitCodes.Fatal;
        }
    }

    private static int Dispatch(string[] args, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        if (args.Length == 0)
        {
            stderr.Write(Usage());
            return ExitCodes.Usage;
        }

        string command = args[0];
        if (command is "--help" or "-h" or "help")
        {
            stdout.Write(Usage());
            return ExitCodes.Ok;
        }

        if (command is "--version")
        {
            stdout.WriteLine($"csvetl {Version}");
            return ExitCodes.Ok;
        }

        if (command is not ("validate" or "clean" or "schema-check" or "sample"))
        {
            string[] names = { "validate", "clean", "schema-check", "sample" };
            var best = names.OrderBy(n => ArgParser.Distance(n, command)).First();
            string hint = ArgParser.Distance(best, command) <= 3 ? $" Did you mean '{best}'?" : "";
            throw new UsageException($"unknown command '{command}'.{hint}");
        }

        var rest = args.Skip(1).ToArray();
        if (rest.Contains("--help") || rest.Contains("-h"))
        {
            stdout.Write(CommandUsage(command));
            return ExitCodes.Ok;
        }

        var parsed = ArgParser.Parse(rest, Specs(command));
        return command switch
        {
            "validate" => RunValidate(parsed, stdin, stdout, stderr),
            "clean" => RunClean(parsed, stdin, stdout, stderr),
            "schema-check" => RunSchemaCheck(parsed, stdout),
            _ => RunSample(parsed, stdout),
        };
    }

    private static SchemaDefinition LoadSchemaOrThrow(string path, TextWriter stderr)
    {
        var r = SchemaLoader.LoadFile(path);
        if (!r.IsValid)
        {
            stderr.WriteLine($"csvetl: schema '{path}' is invalid ({r.Errors.Count} error(s)):");
            foreach (var e in r.Errors) stderr.WriteLine("  " + e);
            throw new SchemaInvalidException();
        }

        return r.Schema!;
    }

    private sealed class SchemaInvalidException : Exception;

    private static char ParseDelimiter(string text)
    {
        switch (text.ToLowerInvariant())
        {
            case "comma": return ',';
            case "semicolon": return ';';
            case "tab": case "\\t": return '\t';
            case "pipe": return '|';
        }

        if (text.Length != 1) throw new UsageException($"--delimiter must be one character or comma|semicolon|tab|pipe, got '{text}'");
        if (text[0] is '"' or '\r' or '\n') throw new UsageException("--delimiter must not be a double quote, CR or LF");
        return text[0];
    }

    private static Encoding ParseEncoding(string text) => text.ToLowerInvariant() switch
    {
        "utf-8" or "utf8" => new UTF8Encoding(false),
        "latin1" or "iso-8859-1" => Encoding.Latin1,
        "utf-16" or "utf16" => Encoding.Unicode,
        _ => throw new UsageException($"--encoding must be utf-8, latin1 or utf-16, got '{text}'"),
    };

    private static CsvOptions BuildCsv(ParsedArgs p) => new()
    {
        Delimiter = ParseDelimiter(p.Get("delimiter")!),
        MaxFieldSize = p.GetInt("max-field-size", 1, 100_000_000),
    };

    private static EtlOptions BuildEtl(ParsedArgs p, TextWriter stderr)
    {
        string mode = p.Get("dedupe")!;
        var dedupe = mode switch
        {
            "hash" => DedupeMode.Hash,
            "exact" => DedupeMode.Exact,
            "off" => DedupeMode.Off,
            _ => throw new UsageException($"--dedupe must be hash, exact or off, got '{mode}'"),
        };
        long every = p.Flag("progress") ? p.GetLong("progress-every", 1, long.MaxValue) : 0;
        return new EtlOptions
        {
            Csv = BuildCsv(p),
            MaxErrors = p.GetInt("max-errors", 0, 10_000_000),
            Dedupe = dedupe,
            MaxKeys = p.GetLong("max-keys", 1, long.MaxValue),
            ProgressEvery = every,
            OnProgress = pr => stderr.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[csvetl] {pr.RowsRead} rows read, {pr.RowsClean} clean, {pr.RowsRejected} rejected")),
        };
    }

    private static TextReader OpenInput(string path, ParsedArgs p, TextReader stdin) =>
        path == "-" ? stdin : CsvReader.OpenFile(path, ParseEncoding(p.Get("encoding")!));

    private static StreamWriter CreateFile(string path) =>
        new(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 65536), new UTF8Encoding(false), 65536) { NewLine = "\n" };

    private static int RunValidate(ParsedArgs p, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        string format = p.Get("format")!;
        if (format is not ("text" or "json")) throw new UsageException($"--format must be text or json, got '{format}'");
        var options = BuildEtl(p, stderr);
        SchemaDefinition schema;
        try { schema = LoadSchemaOrThrow(p.Get("schema")!, stderr); }
        catch (SchemaInvalidException) { return ExitCodes.Usage; }

        string inputName = p.Get("input")!;
        using var reader = OpenInput(inputName, p, stdin);
        var result = EtlRunner.Run(reader, schema, options);
        if (format == "json") stdout.Write(ReportWriter.ToJson(result, schema.Name, inputName) + "\n");
        else ReportWriter.WriteText(stdout, result, schema.Name, inputName);
        return result.HeaderFatal || result.RowsRejected > 0 ? ExitCodes.DataProblems : ExitCodes.Ok;
    }

    private static int RunClean(ParsedArgs p, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        var options = BuildEtl(p, stderr);
        string nl = p.Get("newline")! switch { "lf" => "\n", "crlf" => "\r\n", var x => throw new UsageException($"--newline must be lf or crlf, got '{x}'") };
        char delim = options.Csv.Delimiter;
        string outPath = p.Get("output")!;
        string? jsonlPath = p.Get("jsonl"), rejectsPath = p.Get("rejects"), reportPath = p.Get("report");
        string inputName = p.Get("input")!;
        foreach (var (name, path) in new[] { ("--jsonl", jsonlPath), ("--rejects", rejectsPath), ("--report", reportPath) })
            if (path == "-") throw new UsageException($"{name} needs a file path (stdout carries the clean CSV)");
        foreach (var (name, path) in new[] { ("--output", outPath), ("--jsonl", jsonlPath), ("--rejects", rejectsPath), ("--report", reportPath) })
        {
            if (path == null || path == "-") continue;
            if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(inputName == "-" ? "\0" : inputName), StringComparison.OrdinalIgnoreCase))
                throw new UsageException($"{name} would overwrite the input file '{path}'");
        }

        var outPaths = new[] { outPath, jsonlPath, rejectsPath, reportPath }.Where(x => x != null && x != "-").Select(x => Path.GetFullPath(x!)).ToList();
        if (outPaths.Count != outPaths.Distinct(StringComparer.OrdinalIgnoreCase).Count())
            throw new UsageException("--output, --jsonl, --rejects and --report must be different files");

        SchemaDefinition schema;
        try { schema = LoadSchemaOrThrow(p.Get("schema")!, stderr); }
        catch (SchemaInvalidException) { return ExitCodes.Usage; }

        using var reader = OpenInput(inputName, p, stdin);
        var disposables = new List<IDisposable>();
        try
        {
            var sinks = new List<ICleanSink>();
            TextWriter csvOut = outPath == "-" ? stdout : Track(CreateFile(outPath), disposables);
            sinks.Add(new CleanCsvSink(csvOut, delim, nl));
            if (jsonlPath != null) sinks.Add(new JsonlSink(Track(CreateFile(jsonlPath), disposables)));
            IRejectSink? rejects = rejectsPath == null ? null : new RejectsCsvSink(Track(CreateFile(rejectsPath), disposables), delim, nl);

            var result = EtlRunner.Run(reader, schema, options, sinks, rejects);

            foreach (var d in disposables) d.Dispose();
            disposables.Clear();
            if (reportPath != null)
            {
                using var rw = CreateFile(reportPath);
                rw.Write(ReportWriter.ToJson(result, schema.Name, inputName) + "\n");
            }

            stderr.WriteLine($"csvetl clean: {inputName} against schema '{schema.Name}'");
            ReportWriter.WriteSummary(stderr, result);
            if (result.HeaderFatal) return ExitCodes.DataProblems;
            return p.Flag("fail-on-reject") && result.RowsRejected > 0 ? ExitCodes.DataProblems : ExitCodes.Ok;
        }
        finally
        {
            foreach (var d in disposables) d.Dispose();
        }
    }

    private static T Track<T>(T d, List<IDisposable> list) where T : IDisposable
    {
        list.Add(d);
        return d;
    }

    private static int RunSchemaCheck(ParsedArgs p, TextWriter stdout)
    {
        var r = SchemaLoader.LoadFile(p.Get("schema")!);
        if (!r.IsValid)
        {
            stdout.WriteLine($"schema INVALID: {r.Errors.Count} error(s)");
            foreach (var e in r.Errors) stdout.WriteLine("  " + e);
            return ExitCodes.Usage;
        }

        var s = r.Schema!;
        stdout.WriteLine($"schema OK: '{s.Name}', {s.Columns.Count} column(s), key: {(s.Key.Count == 0 ? "(none)" : string.Join("+", s.Key))}");
        foreach (var c in s.Columns)
        {
            stdout.WriteLine($"  {c.Name,-18} {c.Type.ToString().ToLowerInvariant(),-8} {(c.Required ? "required" : "optional"),-9} onInvalid={c.OnInvalid.ToString().ToLowerInvariant()}" +
                             (c.Aliases.Count > 0 ? $"  aliases: {string.Join(" | ", c.Aliases)}" : ""));
        }

        string? input = p.Get("input");
        if (input == null) return ExitCodes.Ok;
        var csv = BuildCsv(p) with { ReportRagged = false };
        using var reader = CsvReader.OpenFile(input, ParseEncoding(p.Get("encoding")!));
        var header = CsvReader.Read(reader, csv).FirstOrDefault();
        if (header == null)
        {
            stdout.WriteLine($"header check FAILED: '{input}' is empty");
            return ExitCodes.DataProblems;
        }

        var b = HeaderBinding.Bind(s, header.Fields);
        stdout.WriteLine($"header of '{input}':");
        for (int i = 0; i < s.Columns.Count; i++)
            stdout.WriteLine($"  {s.Columns[i].Name,-18} <- " + (b.FieldIndex[i] >= 0 ? $"'{header.Fields[b.FieldIndex[i]]}' (field {b.FieldIndex[i] + 1})" : "(not in file)"));
        foreach (var u in b.Unmapped) stdout.WriteLine($"  ignored column '{u}'");
        foreach (var w in b.Warnings) stdout.WriteLine($"  warning: {w}");
        foreach (var e in b.Errors) stdout.WriteLine($"  ERROR: {e}");
        return b.IsFatal ? ExitCodes.DataProblems : ExitCodes.Ok;
    }

    private static int RunSample(ParsedArgs p, TextWriter stdout)
    {
        int rows = p.GetInt("rows", 0, 100_000_000);
        int seed = p.GetInt("seed", int.MinValue, int.MaxValue);
        if (!double.TryParse(p.Get("dirty-rate"), NumberStyles.Float, CultureInfo.InvariantCulture, out double rate) || rate < 0 || rate > 1)
            throw new UsageException($"--dirty-rate must be a number between 0 and 1 (use a dot), got '{p.Get("dirty-rate")}'");
        string output = p.Get("output")!;
        if (output == "-")
        {
            SyntheticCustomers.WriteTo(stdout, rows, seed, rate, p.Flag("bom"));
            return ExitCodes.Ok;
        }

        using var w = new StreamWriter(new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.Read), new UTF8Encoding(false), 65536);
        SyntheticCustomers.WriteTo(w, rows, seed, rate, p.Flag("bom"));
        return ExitCodes.Ok;
    }

    public static string Usage() => """
        csvetl - validate and clean messy customer CSV exports against a JSON schema

        Usage:
          csvetl validate     --schema FILE --input FILE [options]    report problems, write nothing
          csvetl clean        --schema FILE --input FILE [options]    write clean CSV / JSONL and a rejects file
          csvetl schema-check --schema FILE [--input FILE]            lint a schema, optionally match a header
          csvetl sample       [--rows N] [--seed N] [--output FILE]   generate synthetic messy data
          csvetl --help | --version
          csvetl <command> --help

        Exit codes:
          0  success (clean data; for 'clean' rejects are allowed unless --fail-on-reject)
          1  data problems: rows rejected (validate always, clean with --fail-on-reject) or an unusable header
          2  usage error or invalid schema
          3  I/O error or other fatal error

        stdout carries data (clean CSV, report, sample); progress and summaries go to stderr.

        """;

    public static string CommandUsage(string command)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"csvetl {command} [options]");
        sb.AppendLine();
        foreach (var s in Specs(command))
        {
            string left = "--" + s.Name + (s.Short != null ? $", -{s.Short}" : "") + (s.IsFlag ? "" : " " + (s.Metavar ?? "VALUE"));
            sb.AppendLine($"  {left,-32} {s.Help}{(s.Required ? " (required)" : "")}");
        }

        return sb.ToString();
    }
}
