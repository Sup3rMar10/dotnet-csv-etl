namespace CsvEtl.Csv;

/// <summary>Options shared by the CSV reader and writer. All limits exist to bound memory on hostile input.</summary>
public sealed record CsvOptions
{
    /// <summary>Field delimiter. Must not be a quote, CR or LF.</summary>
    public char Delimiter { get; init; } = ',';

    /// <summary>Maximum characters kept per field. Longer fields are truncated and the record is flagged.</summary>
    public int MaxFieldSize { get; init; } = 1_048_576;

    /// <summary>Maximum fields kept per record. Extra fields are parsed and discarded and the record is flagged.</summary>
    public int MaxFieldsPerRecord { get; init; } = 10_000;

    /// <summary>Maximum total characters kept across all fields of one record.</summary>
    public int MaxRecordSize { get; init; } = 4_194_304;

    /// <summary>When true (default) lines with no characters at all are skipped. When false each yields one empty field.</summary>
    public bool SkipBlankLines { get; init; } = true;

    /// <summary>When true the first record sets the expected field count and later records with a different count get a RaggedRow issue.</summary>
    public bool ReportRagged { get; init; } = true;

    /// <summary>Keep the original text of every record (needed for rejects files). Costs one StringBuilder append per character.</summary>
    public bool CaptureRaw { get; init; } = true;

    /// <summary>Read buffer size in characters.</summary>
    public int BufferSize { get; init; } = 16_384;

    public void Validate()
    {
        if (Delimiter is '"' or '\r' or '\n')
            throw new ArgumentException("Delimiter must not be a double quote, CR or LF.", nameof(Delimiter));
        if (MaxFieldSize < 1) throw new ArgumentOutOfRangeException(nameof(MaxFieldSize), "must be >= 1");
        if (MaxFieldsPerRecord < 1) throw new ArgumentOutOfRangeException(nameof(MaxFieldsPerRecord), "must be >= 1");
        if (MaxRecordSize < 1) throw new ArgumentOutOfRangeException(nameof(MaxRecordSize), "must be >= 1");
        if (BufferSize < 16) throw new ArgumentOutOfRangeException(nameof(BufferSize), "must be >= 16");
    }
}

public enum CsvIssueKind
{
    UnterminatedQuote,
    StrayQuote,
    FieldTooLong,
    TooManyFields,
    RaggedRow,
}

/// <summary>A structural problem found while parsing one record. The record is still returned.</summary>
public sealed record CsvIssue(CsvIssueKind Kind, long Line, string Message)
{
    /// <summary>Stable kebab-case rule id used in reports.</summary>
    public string RuleId => Kind switch
    {
        CsvIssueKind.UnterminatedQuote => "unterminated-quote",
        CsvIssueKind.StrayQuote => "stray-quote",
        CsvIssueKind.FieldTooLong => "field-too-long",
        CsvIssueKind.TooManyFields => "too-many-fields",
        CsvIssueKind.RaggedRow => "ragged-row",
        _ => "csv",
    };
}

/// <summary>One parsed CSV record.</summary>
/// <param name="RecordNumber">1-based index of this record among records returned (the header is record 1).</param>
/// <param name="LineNumber">1-based physical line on which the record starts.</param>
/// <param name="Fields">Parsed field values.</param>
/// <param name="Raw">Original text of the record without its line terminator (null if CaptureRaw is off).</param>
/// <param name="RawTruncated">True if Raw was cut because it exceeded the record size limits.</param>
/// <param name="Issue">First structural problem, or null.</param>
public sealed record CsvRecord(
    long RecordNumber,
    long LineNumber,
    string[] Fields,
    string? Raw,
    bool RawTruncated,
    CsvIssue? Issue);
