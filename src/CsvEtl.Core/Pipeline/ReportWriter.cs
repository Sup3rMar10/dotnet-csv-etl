using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using CsvEtl.Schema;

namespace CsvEtl.Pipeline;

/// <summary>Renders an <see cref="EtlResult"/> as stable JSON or as a human readable text report. Culture independent.</summary>
public static class ReportWriter
{
    public static string ToJson(EtlResult r, string schemaName, string input)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteString("schema", schemaName);
            w.WriteString("input", input);
            w.WriteStartObject("summary");
            w.WriteNumber("rowsRead", r.RowsRead);
            w.WriteNumber("rowsClean", r.RowsClean);
            w.WriteNumber("rowsRejected", r.RowsRejected);
            w.WriteNumber("rowsRepaired", r.RowsRepaired);
            w.WriteNumber("duplicatesFound", r.DuplicatesFound);
            w.WriteBoolean("dedupeSaturated", r.DedupeSaturated);
            w.WriteNumber("dedupeUncheckedRows", r.DedupeUncheckedRows);
            w.WriteBoolean("headerFatal", r.HeaderFatal);
            w.WriteNumber("totalIssues", r.Report.TotalIssues);
            w.WriteNumber("storedIssues", r.Report.Issues.Count);
            w.WriteNumber("droppedIssues", r.Report.DroppedIssues);
            w.WriteNumber("maxErrors", r.Report.MaxErrors);
            w.WriteEndObject();
            WriteCounts(w, "byRule", r.Report.ByRule);
            WriteCounts(w, "byColumn", r.Report.ByColumn);
            w.WriteStartObject("header");
            WriteStrings(w, "errors", r.HeaderErrors);
            WriteStrings(w, "warnings", r.HeaderWarnings);
            WriteStrings(w, "unmapped", r.UnmappedHeaders);
            w.WriteEndObject();
            w.WriteStartArray("issues");
            foreach (var i in r.Report.Issues)
            {
                w.WriteStartObject();
                w.WriteNumber("row", i.Row);
                w.WriteNumber("line", i.Line);
                w.WriteString("column", i.Column);
                w.WriteString("rule", i.Rule);
                w.WriteString("rawValue", i.RawValue);
                w.WriteString("message", i.Message);
                w.WriteString("action", i.Action.ToString().ToLowerInvariant());
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();
        }

        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static void WriteCounts(Utf8JsonWriter w, string name, IReadOnlyDictionary<string, long> counts)
    {
        w.WriteStartObject(name);
        foreach (var (k, v) in counts) w.WriteNumber(k, v);
        w.WriteEndObject();
    }

    private static void WriteStrings(Utf8JsonWriter w, string name, IReadOnlyList<string> items)
    {
        w.WriteStartArray(name);
        foreach (var s in items) w.WriteStringValue(s);
        w.WriteEndArray();
    }

    public static void WriteSummary(TextWriter w, EtlResult r)
    {
        var inv = CultureInfo.InvariantCulture;
        w.WriteLine($"rows read      : {r.RowsRead.ToString(inv)}");
        w.WriteLine($"rows clean     : {r.RowsClean.ToString(inv)} ({r.RowsRepaired.ToString(inv)} repaired by null/default policy)");
        w.WriteLine($"rows rejected  : {r.RowsRejected.ToString(inv)}");
        if (r.DuplicatesFound > 0 || r.DedupeSaturated)
            w.WriteLine($"duplicates     : {r.DuplicatesFound.ToString(inv)}" + (r.DedupeSaturated
                ? $" (dedupe table full: {r.DedupeUncheckedRows.ToString(inv)} rows could not be checked)" : ""));
        foreach (var e in r.HeaderErrors) w.WriteLine($"header error   : {e}");
        foreach (var e in r.HeaderWarnings) w.WriteLine($"header warning : {e}");
        if (r.UnmappedHeaders.Count > 0) w.WriteLine($"ignored columns: {string.Join(", ", r.UnmappedHeaders)}");
        w.WriteLine($"issues         : {r.Report.TotalIssues.ToString(inv)} total, {r.Report.Issues.Count.ToString(inv)} listed, {r.Report.DroppedIssues.ToString(inv)} not listed (cap {r.Report.MaxErrors.ToString(inv)})");
        if (r.Report.ByRule.Count > 0)
            w.WriteLine("by rule        : " + string.Join(", ", r.Report.ByRule.Select(kv => $"{kv.Key}={kv.Value.ToString(inv)}")));
        if (r.Report.ByColumn.Count > 0)
            w.WriteLine("by column      : " + string.Join(", ", r.Report.ByColumn.Select(kv => $"{kv.Key}={kv.Value.ToString(inv)}")));
    }

    public static void WriteText(TextWriter w, EtlResult r, string schemaName, string input)
    {
        var inv = CultureInfo.InvariantCulture;
        w.WriteLine($"csvetl validate: {input} against schema '{schemaName}'");
        WriteSummary(w, r);
        if (r.Report.Issues.Count == 0) return;
        w.WriteLine();
        w.WriteLine("row   line  column            rule              action     message");
        foreach (var i in r.Report.Issues)
        {
            string col = i.Column.Length == 0 ? "(row)" : i.Column;
            w.WriteLine($"{i.Row.ToString(inv),-5} {i.Line.ToString(inv),-5} {Clip(col, 17),-17} {Clip(i.Rule, 17),-17} {i.Action.ToString().ToLowerInvariant(),-10} {OneLine(i.Message)}  [{OneLine(Clip(i.RawValue, 40))}]");
        }

        if (r.Report.DroppedIssues > 0)
            w.WriteLine($"... {r.Report.DroppedIssues.ToString(inv)} more issue(s) not listed (raise --max-errors to see them)");
    }

    private static string Clip(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "~";

    private static string OneLine(string s) => s.Replace("\r", "\\r").Replace("\n", "\\n");
}
