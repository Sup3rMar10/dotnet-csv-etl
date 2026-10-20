using System.Globalization;

namespace CsvEtl.Cli;

/// <summary>Bad command line. Mapped to exit code 2.</summary>
public sealed class UsageException(string message) : Exception(message);

public sealed record OptionSpec(string Name, string? Short, bool IsFlag, string Help, string? Default = null, bool Required = false, string? Metavar = null);

public sealed class ParsedArgs
{
    private readonly Dictionary<string, string> _values = new();
    private readonly HashSet<string> _flags = new();
    private readonly Dictionary<string, OptionSpec> _specs;

    internal ParsedArgs(IEnumerable<OptionSpec> specs) => _specs = specs.ToDictionary(s => s.Name);

    internal void SetValue(string name, string value) => _values[name] = value;
    internal void SetFlag(string name) => _flags.Add(name);
    internal bool IsSet(string name) => _values.ContainsKey(name) || _flags.Contains(name);

    public bool Flag(string name) => _flags.Contains(name);

    public string? Get(string name) => _values.TryGetValue(name, out var v) ? v : _specs[name].Default;

    public int GetInt(string name, int min, int max)
    {
        string? text = Get(name);
        if (text == null) throw new UsageException($"option --{name} needs a value");
        if (!int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int v))
            throw new UsageException($"option --{name} expects an integer, got '{text}'");
        if (v < min || v > max) throw new UsageException($"option --{name} must be between {min} and {max}, got {v}");
        return v;
    }

    public long GetLong(string name, long min, long max)
    {
        string? text = Get(name);
        if (text == null) throw new UsageException($"option --{name} needs a value");
        if (!long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long v))
            throw new UsageException($"option --{name} expects an integer, got '{text}'");
        if (v < min || v > max) throw new UsageException($"option --{name} must be between {min} and {max}, got {v}");
        return v;
    }
}

/// <summary>Small hand-written option parser: --name value, --name=value, -s value, flags, "--" to stop, helpful errors.</summary>
public static class ArgParser
{
    public static ParsedArgs Parse(IReadOnlyList<string> args, IReadOnlyList<OptionSpec> specs)
    {
        var result = new ParsedArgs(specs);
        for (int i = 0; i < args.Count; i++)
        {
            string a = args[i];
            if (a == "--")
            {
                if (i + 1 < args.Count) throw new UsageException($"unexpected argument '{args[i + 1]}' (this command takes no positional arguments)");
                break;
            }

            if (!a.StartsWith('-') || a == "-")
                throw new UsageException($"unexpected argument '{a}' (options start with --; this command takes no positional arguments)");

            string? inline = null;
            OptionSpec? spec;
            if (a.StartsWith("--", StringComparison.Ordinal))
            {
                string name = a[2..];
                int eq = name.IndexOf('=');
                if (eq >= 0) { inline = name[(eq + 1)..]; name = name[..eq]; }
                spec = specs.FirstOrDefault(s => s.Name == name);
                if (spec == null) throw new UsageException(Unknown(a, name, specs));
            }
            else
            {
                string sh = a[1..];
                spec = specs.FirstOrDefault(s => s.Short == sh);
                if (spec == null) throw new UsageException($"unknown option '{a}'");
            }

            if (result.IsSet(spec.Name)) throw new UsageException($"option --{spec.Name} given more than once");
            if (spec.IsFlag)
            {
                if (inline != null) throw new UsageException($"option --{spec.Name} is a flag and takes no value");
                result.SetFlag(spec.Name);
                continue;
            }

            if (inline == null)
            {
                if (i + 1 >= args.Count) throw new UsageException($"option --{spec.Name} needs a value ({spec.Metavar ?? "VALUE"})");
                inline = args[++i];
            }

            result.SetValue(spec.Name, inline);
        }

        foreach (var s in specs.Where(s => s.Required && !result.IsSet(s.Name)))
            throw new UsageException($"missing required option --{s.Name} ({s.Help})");
        return result;
    }

    private static string Unknown(string raw, string name, IReadOnlyList<OptionSpec> specs)
    {
        var best = specs.Select(s => (s.Name, D: Distance(s.Name, name))).OrderBy(x => x.D).FirstOrDefault();
        string hint = best.Name != null && best.D <= 2 ? $" Did you mean --{best.Name}?" : "";
        return $"unknown option '{raw}'.{hint}";
    }

    internal static int Distance(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (prev, cur) = (cur, prev);
        }

        return prev[b.Length];
    }
}
