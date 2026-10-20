using CsvEtl.Csv;
using CsvEtl.Schema;

namespace CsvEtl.Validation;

public enum IssueAction { Rejected, Nulled, Defaulted, Kept }

/// <summary>One finding. Row is the 1-based data row (header excluded); Line is the physical start line in the file.
/// Row 0 means a file-level (header) finding.</summary>
public sealed record RowIssue(long Row, long Line, string Column, string Rule, string RawValue, string Message, IssueAction Action);

public sealed class RowResult
{
    public bool Rejected { get; init; }
    public bool Repaired { get; init; }
    public required object?[] Values { get; init; }
    public required IReadOnlyList<RowIssue> Issues { get; init; }
}

/// <summary>Maps header cells to schema columns by normalised name or alias.</summary>
public sealed class HeaderBinding
{
    /// <summary>Per schema column, the index of the matching field in the file or -1.</summary>
    public required int[] FieldIndex { get; init; }
    public required IReadOnlyList<string> Errors { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
    public required IReadOnlyList<string> Unmapped { get; init; }
    public bool IsFatal => Errors.Count > 0;

    public static HeaderBinding Bind(SchemaDefinition schema, IReadOnlyList<string> header)
    {
        var lookup = new Dictionary<string, int>();
        for (int i = 0; i < schema.Columns.Count; i++)
        {
            lookup[HeaderNames.Normalize(schema.Columns[i].Name)] = i;
            foreach (var a in schema.Columns[i].Aliases) lookup[HeaderNames.Normalize(a)] = i;
        }

        var map = Enumerable.Repeat(-1, schema.Columns.Count).ToArray();
        var warnings = new List<string>();
        var unmapped = new List<string>();
        for (int f = 0; f < header.Count; f++)
        {
            string norm = HeaderNames.Normalize(header[f]);
            if (norm.Length > 0 && lookup.TryGetValue(norm, out int ci))
            {
                if (map[ci] < 0) map[ci] = f;
                else warnings.Add($"header '{header[f]}' (field {f + 1}) also maps to column '{schema.Columns[ci].Name}'; the first match (field {map[ci] + 1}) is used");
            }
            else unmapped.Add(header[f]);
        }

        var errors = new List<string>();
        for (int i = 0; i < schema.Columns.Count; i++)
            if (map[i] < 0 && schema.Columns[i].Required)
                errors.Add($"required column '{schema.Columns[i].Name}' not found in header (accepted names: {string.Join(", ", new[] { schema.Columns[i].Name }.Concat(schema.Columns[i].Aliases))})");
        return new HeaderBinding { FieldIndex = map, Errors = errors, Warnings = warnings, Unmapped = unmapped };
    }
}

/// <summary>Validates and transforms one parsed record at a time.</summary>
public sealed class RowProcessor(SchemaDefinition schema, HeaderBinding binding)
{
    private const int MaxRawInReport = 200;

    public RowResult Process(CsvRecord rec)
    {
        long row = rec.RecordNumber - 1;
        var cols = schema.Columns;
        if (rec.Issue != null)
        {
            string raw = rec.Raw ?? string.Join(",", rec.Fields);
            return new RowResult
            {
                Rejected = true,
                Values = new object?[cols.Count],
                Issues = new[]
                {
                    new RowIssue(row, rec.LineNumber, "", rec.Issue.RuleId, ColumnEvaluator.Clip(raw, MaxRawInReport),
                        rec.Issue.Message, IssueAction.Rejected),
                },
            };
        }

        var values = new object?[cols.Count];
        List<RowIssue>? issues = null;
        bool rejected = false, repaired = false;
        for (int ci = 0; ci < cols.Count; ci++)
        {
            var col = cols[ci];
            int fi = binding.FieldIndex[ci];
            string? raw = fi >= 0 && fi < rec.Fields.Length ? rec.Fields[fi] : null;
            var ev = ColumnEvaluator.Evaluate(col, raw);
            if (ev.Failure == null)
            {
                values[ci] = ev.Empty ? col.DefaultValue : ev.Value;
                continue;
            }

            IssueAction action;
            switch (col.OnInvalid)
            {
                case BadValuePolicy.Null:
                    action = IssueAction.Nulled;
                    values[ci] = null;
                    repaired = true;
                    break;
                case BadValuePolicy.Default:
                    action = IssueAction.Defaulted;
                    values[ci] = col.DefaultValue;
                    repaired = true;
                    break;
                default:
                    action = IssueAction.Rejected;
                    rejected = true;
                    break;
            }

            (issues ??= new List<RowIssue>()).Add(new RowIssue(row, rec.LineNumber, col.Name, ev.Failure.Rule,
                ColumnEvaluator.Clip(raw ?? "", MaxRawInReport), ev.Failure.Message, action));
        }

        return new RowResult
        {
            Rejected = rejected,
            Repaired = repaired && !rejected,
            Values = values,
            Issues = (IReadOnlyList<RowIssue>?)issues ?? Array.Empty<RowIssue>(),
        };
    }

    /// <summary>Human readable form of the key (values joined with +) for reports.</summary>
    public string KeyDisplay(object?[] values) =>
        string.Join("+", schema.Key.Select(k => ColumnEvaluator.Format(schema.Columns[schema.IndexOf(k)], values[schema.IndexOf(k)])));

    /// <summary>Duplicate-detection key built from the cleaned values, or null if any key part is empty.</summary>
    public string? BuildKey(object?[] values)
    {
        if (schema.Key.Count == 0) return null;
        var sb = new System.Text.StringBuilder();
        foreach (var k in schema.Key)
        {
            int ci = schema.IndexOf(k);
            string s = ColumnEvaluator.Format(schema.Columns[ci], values[ci]);
            if (s.Length == 0) return null;
            sb.Append(s.Length).Append(':').Append(s).Append(';');
        }

        return sb.ToString();
    }
}

/// <summary>Collects findings with hard caps: at most MaxErrors detailed entries are stored, but totals are still counted
/// exactly (per rule and per column), so a hostile file cannot grow the report without bound.</summary>
public sealed class ErrorReport(int maxErrors)
{
    private readonly List<RowIssue> _issues = new();
    private readonly SortedDictionary<string, long> _byRule = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, long> _byColumn = new(StringComparer.Ordinal);

    public int MaxErrors { get; } = Math.Max(0, maxErrors);
    public IReadOnlyList<RowIssue> Issues => _issues;
    public long TotalIssues { get; private set; }
    public long DroppedIssues => TotalIssues - _issues.Count;
    public IReadOnlyDictionary<string, long> ByRule => _byRule;
    public IReadOnlyDictionary<string, long> ByColumn => _byColumn;

    public void Add(RowIssue issue)
    {
        TotalIssues++;
        _byRule[issue.Rule] = _byRule.GetValueOrDefault(issue.Rule) + 1;
        string col = issue.Column.Length == 0 ? "(row)" : issue.Column;
        _byColumn[col] = _byColumn.GetValueOrDefault(col) + 1;
        if (_issues.Count < MaxErrors) _issues.Add(issue);
    }
}
