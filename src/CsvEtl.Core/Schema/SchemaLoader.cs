using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CsvEtl.Schema;

public sealed record SchemaLoadResult(SchemaDefinition? Schema, IReadOnlyList<string> Errors)
{
    public bool IsValid => Schema != null && Errors.Count == 0;
}

/// <summary>Strict loader for the JSON schema format: unknown properties, wrong types and contradictory rules are all
/// reported (with a path) instead of silently ignored, because a typo in a schema silently disables a rule.</summary>
public static class SchemaLoader
{
    private static readonly string[] TopLevel = { "name", "key", "onDuplicate", "columns" };

    private static readonly string[] ColumnProps =
    {
        "name", "aliases", "type", "required", "trim", "case", "minLength", "maxLength", "min", "max", "regex",
        "allowed", "formats", "outputFormat", "decimalSeparator", "trueValues", "falseValues", "onInvalid", "default",
    };

    public static SchemaLoadResult LoadFile(string path) => Parse(File.ReadAllText(path));

    public static SchemaLoadResult Parse(string json)
    {
        var errors = new List<string>();
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        }
        catch (JsonException e)
        {
            errors.Add($"schema is not valid JSON: {e.Message}");
            return new SchemaLoadResult(null, errors);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                errors.Add("schema root must be a JSON object");
                return new SchemaLoadResult(null, errors);
            }

            CheckUnknown(root, TopLevel, "$", errors);
            string name = GetString(root, "name", "$", errors) ?? "schema";
            var key = GetStringArray(root, "key", "$", errors) ?? new List<string>();
            var onDup = DuplicatePolicy.Reject;
            string? dupText = GetString(root, "onDuplicate", "$", errors);
            if (dupText != null)
            {
                if (dupText == "reject") onDup = DuplicatePolicy.Reject;
                else if (dupText == "keep") onDup = DuplicatePolicy.Keep;
                else errors.Add($"$.onDuplicate: must be \"reject\" or \"keep\", got \"{dupText}\"");
            }

            var columns = new List<Column>();
            if (!root.TryGetProperty("columns", out var cols) || cols.ValueKind != JsonValueKind.Array)
                errors.Add("$.columns: required array of column objects");
            else if (cols.GetArrayLength() == 0)
                errors.Add("$.columns: must contain at least one column");
            else
            {
                int i = 0;
                foreach (var el in cols.EnumerateArray())
                {
                    var col = ParseColumn(el, i, errors);
                    if (col != null) columns.Add(col);
                    i++;
                }
            }

            // names and aliases must be unique after normalisation
            var seen = new Dictionary<string, string>();
            foreach (var c in columns)
            {
                foreach (var n in new[] { c.Name }.Concat(c.Aliases))
                {
                    string norm = HeaderNames.Normalize(n);
                    if (norm.Length == 0)
                        errors.Add($"column '{c.Name}': name/alias '{n}' has no letters or digits");
                    else if (seen.TryGetValue(norm, out var owner) && owner != c.Name)
                        errors.Add($"column '{c.Name}': name/alias '{n}' collides with column '{owner}' (names are compared ignoring case and punctuation)");
                    else seen[norm] = c.Name;
                }
            }

            foreach (var k in key)
                if (!columns.Any(c => c.Name == k))
                    errors.Add($"$.key: '{k}' is not a column name");

            if (errors.Count > 0) return new SchemaLoadResult(null, errors);
            return new SchemaLoadResult(
                new SchemaDefinition { Name = name, Columns = columns, Key = key, OnDuplicate = onDup }, errors);
        }
    }

    private static Column? ParseColumn(JsonElement el, int index, List<string> errors)
    {
        string path = $"$.columns[{index}]";
        if (el.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{path}: must be an object");
            return null;
        }

        int before = errors.Count;
        string? name = GetString(el, "name", path, errors);
        if (string.IsNullOrWhiteSpace(name))
        {
            errors.Add($"{path}.name: required non-empty string");
            return null;
        }

        path = $"$.columns[{index}] ('{name}')";
        CheckUnknown(el, ColumnProps, path, errors);

        var type = ColumnType.String;
        string? typeText = GetString(el, "type", path, errors);
        if (typeText != null)
        {
            switch (typeText)
            {
                case "string": type = ColumnType.String; break;
                case "int": type = ColumnType.Int; break;
                case "decimal": type = ColumnType.Decimal; break;
                case "date": type = ColumnType.Date; break;
                case "bool": type = ColumnType.Bool; break;
                case "enum": type = ColumnType.Enum; break;
                default: errors.Add($"{path}.type: unknown type \"{typeText}\" (string, int, decimal, date, bool, enum)"); break;
            }
        }

        var caseMode = CaseMode.None;
        string? caseText = GetString(el, "case", path, errors);
        if (caseText != null)
        {
            switch (caseText)
            {
                case "none": break;
                case "upper": caseMode = CaseMode.Upper; break;
                case "lower": caseMode = CaseMode.Lower; break;
                case "title": caseMode = CaseMode.Title; break;
                default: errors.Add($"{path}.case: must be none, upper, lower or title, got \"{caseText}\""); break;
            }
        }

        var policy = BadValuePolicy.Reject;
        string? policyText = GetString(el, "onInvalid", path, errors);
        if (policyText != null)
        {
            switch (policyText)
            {
                case "reject": break;
                case "null": policy = BadValuePolicy.Null; break;
                case "default": policy = BadValuePolicy.Default; break;
                default: errors.Add($"{path}.onInvalid: must be reject, null or default, got \"{policyText}\""); break;
            }
        }

        var aliases = GetStringArray(el, "aliases", path, errors) ?? new List<string>();
        var allowed = GetStringArray(el, "allowed", path, errors) ?? new List<string>();
        var formats = GetStringArray(el, "formats", path, errors);
        var trueValues = GetStringArray(el, "trueValues", path, errors);
        var falseValues = GetStringArray(el, "falseValues", path, errors);
        string? outputFormat = GetString(el, "outputFormat", path, errors);
        string? sepText = GetString(el, "decimalSeparator", path, errors);
        bool? required = GetBool(el, "required", path, errors);
        bool? trim = GetBool(el, "trim", path, errors);
        int? minLength = GetInt(el, "minLength", path, errors);
        int? maxLength = GetInt(el, "maxLength", path, errors);
        string? regexText = GetString(el, "regex", path, errors);

        // cross-field rules
        if (caseMode != CaseMode.None && type is not (ColumnType.String or ColumnType.Enum))
            errors.Add($"{path}.case: only valid for string and enum columns");
        if ((minLength != null || maxLength != null) && type != ColumnType.String)
            errors.Add($"{path}: minLength/maxLength are only valid for string columns");
        if (minLength is < 0 || maxLength is < 0) errors.Add($"{path}: minLength/maxLength must be >= 0");
        if (minLength != null && maxLength != null && minLength > maxLength)
            errors.Add($"{path}: minLength {minLength} is greater than maxLength {maxLength}");
        if (allowed.Count > 0 && type is not (ColumnType.String or ColumnType.Enum))
            errors.Add($"{path}.allowed: only valid for string and enum columns");
        if (type == ColumnType.Enum && allowed.Count == 0)
            errors.Add($"{path}.allowed: an enum column needs a non-empty allowed list");
        if (formats != null && type != ColumnType.Date) errors.Add($"{path}.formats: only valid for date columns");
        if (outputFormat != null && type != ColumnType.Date) errors.Add($"{path}.outputFormat: only valid for date columns");
        if ((trueValues != null || falseValues != null) && type != ColumnType.Bool)
            errors.Add($"{path}.trueValues/falseValues: only valid for bool columns");
        if (sepText != null && type != ColumnType.Decimal)
            errors.Add($"{path}.decimalSeparator: only valid for decimal columns");

        char sep = '.';
        if (sepText != null)
        {
            if (sepText is "." or ",") sep = sepText[0];
            else errors.Add($"{path}.decimalSeparator: must be \".\" or \",\"");
        }

        if (formats != null)
        {
            if (formats.Count == 0) errors.Add($"{path}.formats: must not be empty");
            foreach (var f in formats)
            {
                if (f.Length < 2)
                    errors.Add($"{path}.formats: \"{f}\" is not a usable date format (single-character formats are culture dependent; use e.g. yyyy-MM-dd)");
                else
                {
                    try { _ = new DateOnly(2001, 2, 3).ToString(f, CultureInfo.InvariantCulture); }
                    catch (FormatException) { errors.Add($"{path}.formats: \"{f}\" is not a valid .NET date format"); }
                }
            }
        }

        if (outputFormat != null && outputFormat.Length < 2)
            errors.Add($"{path}.outputFormat: use a custom format such as yyyy-MM-dd");

        Regex? regex = null;
        if (regexText != null)
        {
            try { regex = new Regex(regexText, RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(500)); }
            catch (ArgumentException e) { errors.Add($"{path}.regex: invalid pattern: {e.Message}"); }
        }

        object? min = ParseBound(el, "min", type, path, errors);
        object? max = ParseBound(el, "max", type, path, errors);
        if (min != null && max != null && Compare(min, max) > 0)
            errors.Add($"{path}: min {min} is greater than max {max}");

        string? defaultText = null;
        if (el.TryGetProperty("default", out var def))
        {
            defaultText = def.ValueKind switch
            {
                JsonValueKind.String => def.GetString(),
                JsonValueKind.Number => def.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => null,
            };
            if (defaultText == null) errors.Add($"{path}.default: must be a string, number or boolean");
        }

        if (policy == BadValuePolicy.Default && defaultText == null)
            errors.Add($"{path}: onInvalid is \"default\" but no default value is given");

        if (errors.Count > before)
        {
            return null;
        }

        var col = new Column
        {
            Name = name,
            Aliases = aliases,
            Type = type,
            Required = required ?? false,
            Trim = trim ?? true,
            Case = caseMode,
            MinLength = minLength,
            MaxLength = maxLength,
            Min = min,
            Max = max,
            Regex = regex,
            RegexSource = regexText,
            Allowed = allowed,
            Formats = formats ?? new List<string> { "yyyy-MM-dd" },
            OutputFormat = outputFormat ?? "yyyy-MM-dd",
            DecimalSeparator = sep,
            TrueValues = trueValues ?? new List<string> { "true", "yes", "y", "1", "t" },
            FalseValues = falseValues ?? new List<string> { "false", "no", "n", "0", "f" },
            OnInvalid = policy,
            DefaultText = defaultText,
        };

        if (defaultText != null)
        {
            // the default must itself satisfy the column's rules (required does not apply to the default itself)
            var ev = ColumnEvaluator.Evaluate(col with { Required = false }, defaultText);
            if (ev.Failure != null)
            {
                errors.Add($"{path}.default: \"{defaultText}\" fails the column's own rules ({ev.Failure.Rule}: {ev.Failure.Message})");
                return null;
            }

            col = col with { DefaultValue = ev.Value };
        }

        return col;
    }

    private static int Compare(object a, object b) => (a, b) switch
    {
        (long x, long y) => x.CompareTo(y),
        (decimal x, decimal y) => x.CompareTo(y),
        (DateOnly x, DateOnly y) => x.CompareTo(y),
        _ => 0,
    };

    private static object? ParseBound(JsonElement el, string prop, ColumnType type, string path, List<string> errors)
    {
        if (!el.TryGetProperty(prop, out var v)) return null;
        switch (type)
        {
            case ColumnType.Int:
                if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out long l)) return l;
                errors.Add($"{path}.{prop}: must be an integer for int columns");
                return null;
            case ColumnType.Decimal:
                if (v.ValueKind == JsonValueKind.Number && v.TryGetDecimal(out decimal d)) return d;
                errors.Add($"{path}.{prop}: must be a number for decimal columns");
                return null;
            case ColumnType.Date:
                if (v.ValueKind == JsonValueKind.String &&
                    DateOnly.TryParseExact(v.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt))
                    return dt;
                errors.Add($"{path}.{prop}: must be an ISO date string yyyy-MM-dd for date columns");
                return null;
            default:
                errors.Add($"{path}.{prop}: only valid for int, decimal and date columns (use minLength/maxLength for strings)");
                return null;
        }
    }

    private static void CheckUnknown(JsonElement el, string[] known, string path, List<string> errors)
    {
        foreach (var p in el.EnumerateObject())
            if (!known.Contains(p.Name))
            {
                string? hint = known.OrderBy(k => Distance(k, p.Name)).First();
                errors.Add($"{path}: unknown property \"{p.Name}\" (did you mean \"{hint}\"?)");
            }
    }

    private static int Distance(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1),
                    prev[j - 1] + (char.ToLowerInvariant(a[i - 1]) == char.ToLowerInvariant(b[j - 1]) ? 0 : 1));
            (prev, cur) = (cur, prev);
        }

        return prev[b.Length];
    }

    private static string? GetString(JsonElement el, string prop, string path, List<string> errors)
    {
        if (!el.TryGetProperty(prop, out var v)) return null;
        if (v.ValueKind == JsonValueKind.String) return v.GetString();
        errors.Add($"{path}.{prop}: must be a string");
        return null;
    }

    private static bool? GetBool(JsonElement el, string prop, string path, List<string> errors)
    {
        if (!el.TryGetProperty(prop, out var v)) return null;
        if (v.ValueKind is JsonValueKind.True or JsonValueKind.False) return v.GetBoolean();
        errors.Add($"{path}.{prop}: must be true or false");
        return null;
    }

    private static int? GetInt(JsonElement el, string prop, string path, List<string> errors)
    {
        if (!el.TryGetProperty(prop, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i)) return i;
        errors.Add($"{path}.{prop}: must be an integer");
        return null;
    }

    private static List<string>? GetStringArray(JsonElement el, string prop, string path, List<string> errors)
    {
        if (!el.TryGetProperty(prop, out var v)) return null;
        if (v.ValueKind != JsonValueKind.Array)
        {
            errors.Add($"{path}.{prop}: must be an array of strings");
            return null;
        }

        var list = new List<string>();
        foreach (var item in v.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                errors.Add($"{path}.{prop}: every element must be a string");
                return null;
            }

            list.Add(item.GetString()!);
        }

        return list;
    }
}
