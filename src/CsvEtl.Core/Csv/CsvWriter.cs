namespace CsvEtl.Csv;

/// <summary>RFC 4180 writer. Quotes a field when it contains the delimiter, a quote, CR or LF, has leading/trailing
/// space or tab, or starts with U+FEFF (so a reader cannot mistake it for a BOM). A row consisting of a single empty
/// field is written as "" so it cannot be mistaken for a blank line.</summary>
public sealed class CsvWriter(TextWriter writer, char delimiter = ',', string newLine = "\r\n")
{
    public long RowsWritten { get; private set; }

    public void WriteRow(IReadOnlyList<string?> fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        if (fields.Count == 0) throw new ArgumentException("A CSV row needs at least one field.", nameof(fields));
        for (int i = 0; i < fields.Count; i++)
        {
            if (i > 0) writer.Write(delimiter);
            string f = fields[i] ?? "";
            if (f.Length == 0 && fields.Count == 1) writer.Write("\"\"");
            else if (NeedsQuoting(f, delimiter)) WriteQuoted(f);
            else writer.Write(f);
        }

        writer.Write(newLine);
        RowsWritten++;
    }

    public static bool NeedsQuoting(string f, char delimiter)
    {
        if (f.Length == 0) return false;
        char first = f[0], last = f[^1];
        if (first is ' ' or '\t' or '﻿' || last is ' ' or '\t') return true;
        foreach (char ch in f)
            if (ch == delimiter || ch == '"' || ch == '\r' || ch == '\n') return true;
        return false;
    }

    private void WriteQuoted(string f)
    {
        writer.Write('"');
        int start = 0;
        for (int i = 0; i < f.Length; i++)
        {
            if (f[i] != '"') continue;
            writer.Write(f.AsSpan(start, i - start + 1));
            writer.Write('"');
            start = i + 1;
        }

        writer.Write(f.AsSpan(start));
        writer.Write('"');
    }
}
