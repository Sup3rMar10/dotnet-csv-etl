using System.Text;

namespace CsvEtl.Csv;

/// <summary>
/// Streaming RFC 4180 reader. Records are produced lazily; at most one record plus a read buffer is held in memory.
/// Lenient where real exports are sloppy: a quote in the middle of an unquoted field is literal, LF, CRLF and lone CR
/// all end a record, and a leading U+FEFF is dropped. Problems are reported on the record (never thrown).
/// </summary>
public static class CsvReader
{
    public static IEnumerable<CsvRecord> Read(TextReader input, CsvOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        var o = options ?? new CsvOptions();
        o.Validate();
        return ReadCore(input, o);
    }

    public static IEnumerable<CsvRecord> ReadString(string text, CsvOptions? options = null) =>
        Read(new StringReader(text), options);

    /// <summary>Opens a file with BOM detection (UTF-8/16/32 BOMs win over <paramref name="fallback"/>).</summary>
    public static StreamReader OpenFile(string path, Encoding? fallback = null) =>
        new(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan),
            fallback ?? new UTF8Encoding(false), detectEncodingFromByteOrderMarks: true, 65536);

    private static IEnumerable<CsvRecord> ReadCore(TextReader input, CsvOptions o)
    {
        var parser = new RecordParser(input, o);
        CsvRecord? r;
        while ((r = parser.Next()) != null)
            yield return r;
    }

    private sealed class CharSource(TextReader reader, int size)
    {
        private readonly char[] _buf = new char[size];
        private int _pos, _len;
        private bool _eof;

        public int Peek() => _pos < _len || Fill() ? _buf[_pos] : -1;

        public int Read() => _pos < _len || Fill() ? _buf[_pos++] : -1;

        private bool Fill()
        {
            if (_eof) return false;
            _len = reader.Read(_buf, 0, _buf.Length);
            _pos = 0;
            if (_len <= 0) { _eof = true; _len = 0; return false; }
            return true;
        }
    }

    private sealed class RecordParser
    {
        private readonly CharSource _src;
        private readonly CsvOptions _o;
        private readonly char _delim;
        private readonly List<string> _fields = new();
        private readonly StringBuilder _field = new();
        private readonly StringBuilder? _raw;
        private readonly int _rawCap;
        private long _line = 1;
        private long _recordNo;
        private int _expected = -1;

        // per-record state
        private CsvIssue? _issue;
        private bool _rawTruncated, _tooLong, _tooMany;
        private int _recChars;

        public RecordParser(TextReader input, CsvOptions o)
        {
            _src = new CharSource(input, o.BufferSize);
            _o = o;
            _delim = o.Delimiter;
            _raw = o.CaptureRaw ? new StringBuilder() : null;
            _rawCap = (int)Math.Min(int.MaxValue / 2, (long)o.MaxRecordSize * 2 + 64);
            if (_src.Peek() == 0xFEFF) _src.Read();
        }

        public CsvRecord? Next()
        {
            while (true)
            {
                int c = _src.Peek();
                if (c < 0) return null;
                long startLine = _line;
                bool blank = c is '\r' or '\n';
                if (blank && _o.SkipBlankLines)
                {
                    ConsumeNewline();
                    continue;
                }

                _fields.Clear();
                _field.Clear();
                _raw?.Clear();
                _issue = null;
                _rawTruncated = _tooLong = _tooMany = false;
                _recChars = 0;

                if (blank) _fields.Add("");
                else ParseFields(startLine);

                if (_src.Peek() >= 0) ConsumeNewline();

                _recordNo++;
                var fields = _fields.ToArray();
                if (_issue == null && _tooMany)
                    _issue = new CsvIssue(CsvIssueKind.TooManyFields, startLine,
                        $"Record starting at line {startLine} has more than {_o.MaxFieldsPerRecord} fields; extra fields discarded.");
                if (_issue == null && _tooLong)
                    _issue = new CsvIssue(CsvIssueKind.FieldTooLong, startLine,
                        $"A field in the record starting at line {startLine} exceeds the size limit and was truncated.");
                if (_o.ReportRagged)
                {
                    if (_expected < 0) _expected = fields.Length;
                    else if (_issue == null && fields.Length != _expected)
                        _issue = new CsvIssue(CsvIssueKind.RaggedRow, startLine,
                            $"Line {startLine} has {fields.Length} fields, expected {_expected}.");
                }

                return new CsvRecord(_recordNo, startLine, fields, _raw?.ToString(), _rawTruncated, _issue);
            }
        }

        private void ConsumeNewline()
        {
            int c = _src.Read();
            if (c == '\r' && _src.Peek() == '\n') _src.Read();
            _line++;
        }

        private void AddRaw(int ch)
        {
            if (_raw == null) return;
            if (_raw.Length < _rawCap) _raw.Append((char)ch);
            else _rawTruncated = true;
        }

        private void AddChar(int ch)
        {
            if (_field.Length < _o.MaxFieldSize && _recChars < _o.MaxRecordSize)
            {
                _field.Append((char)ch);
                _recChars++;
            }
            else _tooLong = true;
        }

        private void EndField()
        {
            if (_fields.Count < _o.MaxFieldsPerRecord) _fields.Add(_field.ToString());
            else _tooMany = true;
            _field.Clear();
        }

        private void ParseFields(long startLine)
        {
            while (true)
            {
                int c = _src.Peek();
                if (c == '"')
                {
                    _src.Read();
                    AddRaw('"');
                    long quoteLine = _line;
                    bool closed = false;
                    while (true)
                    {
                        c = _src.Read();
                        if (c < 0) break;
                        AddRaw(c);
                        if (c == '"')
                        {
                            if (_src.Peek() == '"')
                            {
                                _src.Read();
                                AddRaw('"');
                                AddChar('"');
                            }
                            else { closed = true; break; }
                        }
                        else
                        {
                            if (c == '\n') _line++;
                            else if (c == '\r' && _src.Peek() != '\n') _line++;
                            AddChar(c);
                        }
                    }

                    if (!closed)
                        _issue ??= new CsvIssue(CsvIssueKind.UnterminatedQuote, quoteLine,
                            $"Unterminated quoted field: the quote opened on line {quoteLine} is never closed.");
                    else
                    {
                        // characters after the closing quote but before the delimiter or end of record
                        while ((c = _src.Peek()) >= 0 && c != _delim && c != '\r' && c != '\n')
                        {
                            _src.Read();
                            AddRaw(c);
                            AddChar(c);
                            _issue ??= new CsvIssue(CsvIssueKind.StrayQuote, _line,
                                $"Unexpected character after closing quote on line {_line}.");
                        }
                    }
                }
                else
                {
                    while ((c = _src.Peek()) >= 0 && c != _delim && c != '\r' && c != '\n')
                    {
                        _src.Read();
                        AddRaw(c);
                        AddChar(c);
                    }
                }

                EndField();
                c = _src.Peek();
                if (c == _delim)
                {
                    _src.Read();
                    AddRaw(c);
                    continue;
                }

                return; // newline or EOF
            }
        }
    }
}
