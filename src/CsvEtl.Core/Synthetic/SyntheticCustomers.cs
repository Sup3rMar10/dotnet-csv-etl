using System.Globalization;
using System.Text;

namespace CsvEtl.Synthetic;

/// <summary>
/// Seeded generator of an obviously fake, deliberately messy customer export (all names are "Example" people, all
/// emails are on example.com / example.test, no real data). The same seed always yields the same bytes.
/// Messiness: header aliases, mixed date formats, case and whitespace noise, comma decimals, bad numbers, bad enums,
/// duplicate ids, embedded commas / quotes / newlines in notes, blank lines, ragged rows, stray quotes, mixed LF/CRLF.
/// </summary>
public static class SyntheticCustomers
{
    public const string Header = "Cust ID,Full Name,E-mail,Age (yrs),Account Balance,Signup,Tier,Marketing Opt-In,Notes";

    private static readonly string[] First = { "Alex", "Sam", "Jordan", "Taylor", "Morgan", "Casey", "Riley", "Jamie", "Avery", "Quinn" };
    private static readonly string[] Last = { "Example", "Sample", "Testcase", "Placeholder", "Mockup", "Dummy", "Fixture" };
    private static readonly string[] Tiers = { "bronze", "silver", "gold", "platinum" };
    private static readonly string[] Notes =
    {
        "", "", "", "prefers email", "called on Monday, no answer", "said \"call back later\"",
        "address line 1\nline 2", "VIP, handle with care", "see ticket #1234",
    };

    /// <summary>Lazily yields text chunks (each a whole record plus its line terminator); never holds the file in memory.</summary>
    public static IEnumerable<string> Chunks(int rows, int seed, double dirtyRate = 0.15, bool bom = false)
    {
        var rng = new Random(seed);
        var inv = CultureInfo.InvariantCulture;
        string Nl() => rng.Next(10) < 7 ? "\n" : "\r\n";
        yield return (bom ? "﻿" : "") + Header + Nl();

        for (int i = 1; i <= rows; i++)
        {
            bool dirty = rng.NextDouble() < dirtyRate;
            int defect = dirty ? rng.Next(14) : -1;

            string id = "C" + i.ToString("D6", inv);
            if (defect == 0) id = "c" + i.ToString("D6", inv).ToLowerInvariant() + " "; // fixable: trim + upper
            if (defect == 1) id = "X" + i.ToString(inv); // bad pattern
            if (defect == 2 && i > 5) id = "C" + rng.Next(1, i).ToString("D6", inv); // duplicate of an earlier id

            string name = First[rng.Next(First.Length)] + " " + Last[rng.Next(Last.Length)];
            if (defect == 3) name = "";

            string email = name.Replace(' ', '.').ToLowerInvariant() + i.ToString(inv) + "@example.com";
            if (defect == 4) email = email.Replace("@", "@@"); // invalid, nulled
            if (defect == 5) email = " " + email.ToUpperInvariant() + " "; // fixable

            string age = rng.Next(18, 91).ToString(inv);
            if (defect == 6) age = rng.Next(2) == 0 ? "abc" : "250"; // nulled

            string balance = (rng.Next(-5_000, 2_000_000) / 100m).ToString("0.00", inv);
            if (defect == 7) balance = rng.Next(2) == 0 ? "12,50" : "1,234.56"; // comma traps, rejected

            var date = new DateTime(2018, 1, 1).AddDays(rng.Next(0, 2800));
            string signup = rng.Next(3) switch
            {
                0 => date.ToString("yyyy-MM-dd", inv),
                1 => date.ToString("dd.MM.yyyy", inv),
                _ => date.ToString("d MMM yyyy", inv),
            };
            if (defect == 8) signup = rng.Next(2) == 0 ? "2024-13-45" : "31.02.2023"; // rejected

            string tier = Tiers[rng.Next(Tiers.Length)];
            tier = rng.Next(4) switch { 0 => tier.ToUpperInvariant(), 1 => " " + tier, 2 => char.ToUpperInvariant(tier[0]) + tier[1..], _ => tier };
            if (defect == 9) tier = "diamond"; // defaulted to bronze

            string[] optIns = { "yes", "no", "Y", "N", "1", "0", "TRUE", "false" };
            string optIn = optIns[rng.Next(optIns.Length)];
            if (defect == 10) optIn = "maybe"; // defaulted to false

            string notes = Notes[rng.Next(Notes.Length)];

            var fields = new[] { id, name, email, age, balance, signup, tier, optIn, notes };
            string line = string.Join(",", fields.Select(Quote));

            if (defect == 11) line = string.Join(",", fields.Take(7).Select(Quote)); // ragged: too short
            if (defect == 12) line = line + ",extra"; // ragged: too long
            if (defect == 13) line = line.Replace("\"" + notes + "\"", "\"" + notes + "\"x"); // stray char after quote (only if quoted)

            yield return line + Nl();
            if (dirty && rng.Next(8) == 0) yield return Nl(); // blank line
        }
    }

    private static string Quote(string f) =>
        f.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0 || f.StartsWith(' ') || f.EndsWith(' ')
            ? "\"" + f.Replace("\"", "\"\"") + "\""
            : f;

    public static void WriteTo(TextWriter writer, int rows, int seed, double dirtyRate = 0.15, bool bom = false)
    {
        foreach (var chunk in Chunks(rows, seed, dirtyRate, bom)) writer.Write(chunk);
    }

    public static TextReader AsReader(int rows, int seed, double dirtyRate = 0.15) =>
        new EnumerableTextReader(Chunks(rows, seed, dirtyRate));
}

/// <summary>A TextReader over a lazy sequence of strings: lets tests and tools stream multi-gigabyte inputs that never exist as one string or file.</summary>
public sealed class EnumerableTextReader : TextReader
{
    private readonly IEnumerator<string> _chunks;
    private string _current = "";
    private int _pos;
    private bool _done;
    public long CharsProduced { get; private set; }

    public EnumerableTextReader(IEnumerable<string> chunks) => _chunks = chunks.GetEnumerator();

    private bool Ensure()
    {
        while (_pos >= _current.Length)
        {
            if (_done) return false;
            if (!_chunks.MoveNext()) { _done = true; return false; }
            _current = _chunks.Current;
            _pos = 0;
        }

        return true;
    }

    public override int Peek() => Ensure() ? _current[_pos] : -1;

    public override int Read()
    {
        if (!Ensure()) return -1;
        CharsProduced++;
        return _current[_pos++];
    }

    public override int Read(char[] buffer, int index, int count)
    {
        int n = 0;
        while (n < count && Ensure())
        {
            int take = Math.Min(count - n, _current.Length - _pos);
            _current.CopyTo(_pos, buffer, index + n, take);
            _pos += take;
            n += take;
        }

        CharsProduced += n;
        return n;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _chunks.Dispose();
        base.Dispose(disposing);
    }
}
