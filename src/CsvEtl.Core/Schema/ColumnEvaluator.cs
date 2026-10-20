using System.Globalization;
using System.Text.RegularExpressions;

namespace CsvEtl.Schema;

public sealed record Failure(string Rule, string Message);

/// <summary>Result of evaluating one raw cell against one column. Value is null when empty or failed.</summary>
public readonly record struct Eval(object? Value, Failure? Failure, bool Empty);

/// <summary>Applies the per-column rules in a fixed order: trim, empty/required, case, regex, type or string rules, min/max.
/// Everything uses InvariantCulture (or the column's explicit decimal separator), never the current culture.</summary>
public static class ColumnEvaluator
{
    private const NumberStyles IntStyles = NumberStyles.AllowLeadingSign;
    private const NumberStyles DecimalStyles = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint;

    public static Eval Evaluate(Column c, string? raw)
    {
        string text = raw ?? "";
        if (c.Trim) text = text.Trim();
        if (text.Length == 0)
            return new Eval(null, c.Required ? new Failure(Rules.Required, "value is required but empty") : null, true);

        text = ApplyCase(c.Case, text);

        if (c.Regex != null)
        {
            try
            {
                if (!c.Regex.IsMatch(text))
                    return Fail(Rules.Regex, $"value '{Clip(text)}' does not match /{c.RegexSource}/");
            }
            catch (RegexMatchTimeoutException)
            {
                return Fail(Rules.Regex, $"regex /{c.RegexSource}/ timed out on value '{Clip(text)}'");
            }
        }

        switch (c.Type)
        {
            case ColumnType.String:
                if (c.MinLength is int minLen && text.Length < minLen)
                    return Fail(Rules.MinLength, $"length {text.Length} is shorter than minLength {minLen}");
                if (c.MaxLength is int maxLen && text.Length > maxLen)
                    return Fail(Rules.MaxLength, $"length {text.Length} is longer than maxLength {maxLen}");
                if (c.Allowed.Count > 0 && !c.Allowed.Contains(text, StringComparer.Ordinal))
                    return Fail(Rules.Allowed, $"value '{Clip(text)}' is not one of: {string.Join(", ", c.Allowed)}");
                return new Eval(text, null, false);

            case ColumnType.Enum:
                foreach (var a in c.Allowed)
                    if (string.Equals(a, text, StringComparison.OrdinalIgnoreCase))
                        return new Eval(a, null, false);
                return Fail(Rules.Allowed, $"value '{Clip(text)}' is not one of: {string.Join(", ", c.Allowed)}");

            case ColumnType.Int:
            {
                if (!long.TryParse(text, IntStyles, CultureInfo.InvariantCulture, out long v))
                    return Fail(Rules.Type, $"'{Clip(text)}' is not an integer");
                return Range(c, v, v.CompareTo);
            }

            case ColumnType.Decimal:
            {
                if (!decimal.TryParse(text, DecimalStyles, c.NumberFormat, out decimal v))
                    return Fail(Rules.Type, $"'{Clip(text)}' is not a decimal number (decimal separator '{c.DecimalSeparator}')");
                return Range(c, v, v.CompareTo);
            }

            case ColumnType.Date:
            {
                if (!DateOnly.TryParseExact(text, c.FormatsArray, CultureInfo.InvariantCulture, DateTimeStyles.None, out var v))
                    return Fail(Rules.Type, $"'{Clip(text)}' is not a date in any of: {string.Join(", ", c.Formats)}");
                return Range(c, v, v.CompareTo);
            }

            case ColumnType.Bool:
            {
                foreach (var t in c.TrueValues)
                    if (string.Equals(t, text, StringComparison.OrdinalIgnoreCase)) return new Eval(true, null, false);
                foreach (var f in c.FalseValues)
                    if (string.Equals(f, text, StringComparison.OrdinalIgnoreCase)) return new Eval(false, null, false);
                return Fail(Rules.Type, $"'{Clip(text)}' is not a boolean (true: {string.Join("/", c.TrueValues)}; false: {string.Join("/", c.FalseValues)})");
            }

            default:
                throw new InvalidOperationException($"Unhandled column type {c.Type}");
        }
    }

    private static Eval Range<T>(Column c, T value, Func<T, int> compareToValue) where T : struct
    {
        // compareToValue(x) compares value to x
        if (c.Min is T min && compareToValue(min) < 0)
            return Fail(Rules.Min, $"value {Format(c, value)} is less than min {Format(c, min)}");
        if (c.Max is T max && compareToValue(max) > 0)
            return Fail(Rules.Max, $"value {Format(c, value)} is greater than max {Format(c, max)}");
        return new Eval(value, null, false);
    }

    private static Eval Fail(string rule, string message) => new(null, new Failure(rule, message), false);

    public static string ApplyCase(CaseMode mode, string text) => mode switch
    {
        CaseMode.Upper => text.ToUpperInvariant(),
        CaseMode.Lower => text.ToLowerInvariant(),
        CaseMode.Title => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(text.ToLowerInvariant()),
        _ => text,
    };

    /// <summary>Canonical text form of a typed value (invariant, dot decimal, ISO dates, lowercase booleans).</summary>
    public static string Format(Column c, object? value) => value switch
    {
        null => "",
        string s => s,
        long l => l.ToString(CultureInfo.InvariantCulture),
        decimal d => d.ToString(CultureInfo.InvariantCulture),
        DateOnly d => d.ToString(c.OutputFormat, CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
    };

    internal static string Clip(string s, int max = 60) => s.Length <= max ? s : s[..max] + "...";
}
