using System.Globalization;
using System.Text.RegularExpressions;

namespace CsvEtl.Schema;

public enum ColumnType { String, Int, Decimal, Date, Bool, Enum }

public enum CaseMode { None, Upper, Lower, Title }

public enum BadValuePolicy { Reject, Null, Default }

public enum DuplicatePolicy { Reject, Keep }

/// <summary>Stable rule ids used in error reports.</summary>
public static class Rules
{
    public const string Required = "required";
    public const string Type = "type";
    public const string Min = "min";
    public const string Max = "max";
    public const string MinLength = "min-length";
    public const string MaxLength = "max-length";
    public const string Regex = "regex";
    public const string Allowed = "allowed";
    public const string Duplicate = "duplicate";
    public const string Header = "header";
}

public sealed record Column
{
    public required string Name { get; init; }
    public IReadOnlyList<string> Aliases { get; init; } = Array.Empty<string>();
    public ColumnType Type { get; init; } = ColumnType.String;
    public bool Required { get; init; }
    public bool Trim { get; init; } = true;
    public CaseMode Case { get; init; } = CaseMode.None;
    public int? MinLength { get; init; }
    public int? MaxLength { get; init; }
    /// <summary>Boxed long (int), decimal or DateOnly depending on <see cref="Type"/>.</summary>
    public object? Min { get; init; }
    public object? Max { get; init; }
    public Regex? Regex { get; init; }
    public string? RegexSource { get; init; }
    public IReadOnlyList<string> Allowed { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Formats { get; init; } = new[] { "yyyy-MM-dd" };
    public string OutputFormat { get; init; } = "yyyy-MM-dd";
    private string[]? _formatsArray;
    internal string[] FormatsArray => _formatsArray ??= Formats.ToArray();
    public char DecimalSeparator { get; init; } = '.';
    public IReadOnlyList<string> TrueValues { get; init; } = new[] { "true", "yes", "y", "1", "t" };
    public IReadOnlyList<string> FalseValues { get; init; } = new[] { "false", "no", "n", "0", "f" };
    public BadValuePolicy OnInvalid { get; init; } = BadValuePolicy.Reject;
    public string? DefaultText { get; init; }
    public object? DefaultValue { get; init; }

    private static readonly NumberFormatInfo CommaFormat = new()
    {
        NumberDecimalSeparator = ",",
        NegativeSign = "-",
        PositiveSign = "+",
    };

    /// <summary>Number format derived only from <see cref="DecimalSeparator"/>; never from the current culture.</summary>
    public NumberFormatInfo NumberFormat => DecimalSeparator == ',' ? CommaFormat : NumberFormatInfo.InvariantInfo;
}

public sealed class SchemaDefinition
{
    public required string Name { get; init; }
    public required IReadOnlyList<Column> Columns { get; init; }
    public IReadOnlyList<string> Key { get; init; } = Array.Empty<string>();
    public DuplicatePolicy OnDuplicate { get; init; } = DuplicatePolicy.Reject;

    public int IndexOf(string name)
    {
        for (int i = 0; i < Columns.Count; i++)
            if (string.Equals(Columns[i].Name, name, StringComparison.Ordinal)) return i;
        return -1;
    }
}

public static class HeaderNames
{
    /// <summary>Lowercase letters and digits only, so "Cust ID", "cust_id" and "CustID" are the same name.</summary>
    public static string Normalize(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (char ch in s)
            if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
        return sb.ToString();
    }
}
