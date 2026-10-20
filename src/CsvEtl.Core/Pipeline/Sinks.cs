using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using CsvEtl.Csv;
using CsvEtl.Schema;
using CsvEtl.Validation;

namespace CsvEtl.Pipeline;

public interface ICleanSink
{
    void Begin(SchemaDefinition schema);
    void Write(SchemaDefinition schema, object?[] values);
}

public interface IRejectSink
{
    void Begin();
    void Write(CsvRecord record, IReadOnlyList<RowIssue> issues);
}

/// <summary>Clean rows as CSV with canonical column names and canonical value formats.</summary>
public sealed class CleanCsvSink(TextWriter writer, char delimiter = ',', string newLine = "\n") : ICleanSink
{
    private readonly CsvWriter _csv = new(writer, delimiter, newLine);
    private string?[] _row = Array.Empty<string?>();

    public void Begin(SchemaDefinition schema)
    {
        _row = new string?[schema.Columns.Count];
        _csv.WriteRow(schema.Columns.Select(c => c.Name).ToArray());
    }

    public void Write(SchemaDefinition schema, object?[] values)
    {
        for (int i = 0; i < values.Length; i++) _row[i] = ColumnEvaluator.Format(schema.Columns[i], values[i]);
        _csv.WriteRow(_row);
    }
}

/// <summary>Clean rows as JSON Lines with typed values (numbers, booleans, ISO dates as strings, null for empty).</summary>
public sealed class JsonlSink(TextWriter writer) : ICleanSink
{
    private readonly ArrayBufferWriter<byte> _buffer = new(1024);
    private readonly Utf8JsonWriter _json = new(new ArrayBufferWriter<byte>(), new JsonWriterOptions
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });
    private JsonEncodedText[] _names = Array.Empty<JsonEncodedText>();

    public void Begin(SchemaDefinition schema) =>
        _names = schema.Columns.Select(c => JsonEncodedText.Encode(c.Name, JavaScriptEncoder.UnsafeRelaxedJsonEscaping)).ToArray();

    public void Write(SchemaDefinition schema, object?[] values)
    {
        _buffer.ResetWrittenCount();
        _json.Reset(_buffer);
        _json.WriteStartObject();
        for (int i = 0; i < values.Length; i++)
        {
            switch (values[i])
            {
                case null: _json.WriteNull(_names[i]); break;
                case string s: _json.WriteString(_names[i], s); break;
                case long l: _json.WriteNumber(_names[i], l); break;
                case decimal d: _json.WriteNumber(_names[i], d); break;
                case bool b: _json.WriteBoolean(_names[i], b); break;
                default: _json.WriteString(_names[i], ColumnEvaluator.Format(schema.Columns[i], values[i])); break;
            }
        }

        _json.WriteEndObject();
        _json.Flush();
        writer.Write(Encoding.UTF8.GetString(_buffer.WrittenSpan));
        writer.Write('\n');
    }
}

/// <summary>Rejected records as CSV: row, line, reasons, raw. The raw column is the original text of the record, so the
/// file can be fixed by hand and re-submitted.</summary>
public sealed class RejectsCsvSink(TextWriter writer, char delimiter = ',', string newLine = "\n") : IRejectSink
{
    private readonly CsvWriter _csv = new(writer, delimiter, newLine);

    public void Begin() => _csv.WriteRow(new[] { "row", "line", "reasons", "raw" });

    public void Write(CsvRecord record, IReadOnlyList<RowIssue> issues)
    {
        string reasons = string.Join(" | ", issues.Select(i =>
            (i.Column.Length == 0 ? "" : i.Column + ": ") + i.Rule + ": " + i.Message));
        string raw = record.Raw ?? string.Join(delimiter, record.Fields);
        if (record.RawTruncated) raw += " [truncated]";
        _csv.WriteRow(new[]
        {
            (record.RecordNumber - 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
            record.LineNumber.ToString(System.Globalization.CultureInfo.InvariantCulture),
            reasons,
            raw,
        });
    }
}
