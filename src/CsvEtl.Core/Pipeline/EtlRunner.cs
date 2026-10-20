using CsvEtl.Csv;
using CsvEtl.Schema;
using CsvEtl.Validation;

namespace CsvEtl.Pipeline;

public sealed record EtlProgress(long RowsRead, long RowsClean, long RowsRejected);

public sealed class EtlOptions
{
    public CsvOptions Csv { get; init; } = new();
    public int MaxErrors { get; init; } = 1000;
    public DedupeMode Dedupe { get; init; } = DedupeMode.Hash;
    public long MaxKeys { get; init; } = 5_000_000;
    /// <summary>Call <see cref="OnProgress"/> every this many data rows (0 = never).</summary>
    public long ProgressEvery { get; init; }
    public Action<EtlProgress>? OnProgress { get; init; }
}

public sealed class EtlResult
{
    public long RowsRead { get; internal set; }
    public long RowsClean { get; internal set; }
    public long RowsRejected { get; internal set; }
    /// <summary>Clean rows in which at least one value was nulled or defaulted.</summary>
    public long RowsRepaired { get; internal set; }
    public long DuplicatesFound { get; internal set; }
    public bool DedupeSaturated { get; internal set; }
    public long DedupeUncheckedRows { get; internal set; }
    public bool HeaderFatal { get; internal set; }
    public required ErrorReport Report { get; init; }
    public IReadOnlyList<string> HeaderErrors { get; internal set; } = Array.Empty<string>();
    public IReadOnlyList<string> HeaderWarnings { get; internal set; } = Array.Empty<string>();
    public IReadOnlyList<string> UnmappedHeaders { get; internal set; } = Array.Empty<string>();
}

/// <summary>Streams records through header binding, validation, duplicate detection and the output sinks.</summary>
public static class EtlRunner
{
    public static EtlResult Run(
        TextReader input,
        SchemaDefinition schema,
        EtlOptions options,
        IReadOnlyList<ICleanSink>? cleanSinks = null,
        IRejectSink? rejects = null)
    {
        cleanSinks ??= Array.Empty<ICleanSink>();
        var result = new EtlResult { Report = new ErrorReport(options.MaxErrors) };
        var csv = options.Csv with { ReportRagged = true, CaptureRaw = rejects != null || options.Csv.CaptureRaw };
        var tracker = new KeyTracker(schema.Key.Count == 0 ? DedupeMode.Off : options.Dedupe, options.MaxKeys);

        RowProcessor? processor = null;
        rejects?.Begin();
        foreach (var rec in CsvReader.Read(input, csv))
        {
            if (processor == null)
            {
                // first record is the header
                if (rec.Issue is { Kind: not CsvIssueKind.RaggedRow })
                {
                    result.HeaderFatal = true;
                    result.HeaderErrors = new[] { "header record is malformed: " + rec.Issue.Message };
                    result.Report.Add(new RowIssue(0, rec.LineNumber, "", Rules.Header, "", rec.Issue.Message, IssueAction.Rejected));
                    return result;
                }

                var binding = HeaderBinding.Bind(schema, rec.Fields);
                result.HeaderErrors = binding.Errors;
                result.HeaderWarnings = binding.Warnings;
                result.UnmappedHeaders = binding.Unmapped;
                foreach (var e in binding.Errors)
                    result.Report.Add(new RowIssue(0, rec.LineNumber, "", Rules.Header, "", e, IssueAction.Rejected));
                if (binding.IsFatal)
                {
                    result.HeaderFatal = true;
                    return result;
                }

                processor = new RowProcessor(schema, binding);
                foreach (var s in cleanSinks) s.Begin(schema);
                continue;
            }

            result.RowsRead++;
            var res = processor.Process(rec);
            var issues = res.Issues;
            List<RowIssue>? extra = null;

            if (!res.Rejected && tracker.Mode != DedupeMode.Off)
            {
                string? key = processor.BuildKey(res.Values);
                if (key != null && !tracker.TryAdd(key))
                {
                    result.DuplicatesFound++;
                    bool reject = schema.OnDuplicate == DuplicatePolicy.Reject;
                    extra = new List<RowIssue>(issues)
                    {
                        new(rec.RecordNumber - 1, rec.LineNumber, string.Join("+", schema.Key), Rules.Duplicate,
                            ColumnEvaluator.Clip(processor.KeyDisplay(res.Values), 200),
                            $"duplicate key ({string.Join(", ", schema.Key)}); first occurrence wins",
                            reject ? IssueAction.Rejected : IssueAction.Kept),
                    };
                    issues = extra;
                    if (reject) res = new RowResult { Rejected = true, Values = res.Values, Issues = issues };
                }
            }

            foreach (var i in issues) result.Report.Add(i);

            if (res.Rejected)
            {
                result.RowsRejected++;
                rejects?.Write(rec, issues);
            }
            else
            {
                result.RowsClean++;
                if (res.Repaired) result.RowsRepaired++;
                foreach (var s in cleanSinks) s.Write(schema, res.Values);
            }

            if (options.ProgressEvery > 0 && result.RowsRead % options.ProgressEvery == 0)
                options.OnProgress?.Invoke(new EtlProgress(result.RowsRead, result.RowsClean, result.RowsRejected));
        }

        if (processor == null)
        {
            result.HeaderFatal = true;
            result.HeaderErrors = new[] { "input is empty: no header row found" };
            result.Report.Add(new RowIssue(0, 1, "", Rules.Header, "", "input is empty: no header row found", IssueAction.Rejected));
        }

        result.DedupeSaturated = tracker.Saturated;
        result.DedupeUncheckedRows = tracker.UncheckedKeys;
        return result;
    }
}
