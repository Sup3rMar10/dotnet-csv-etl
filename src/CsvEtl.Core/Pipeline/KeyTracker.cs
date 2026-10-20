using System.Security.Cryptography;
using System.Text;

namespace CsvEtl.Pipeline;

public enum DedupeMode
{
    /// <summary>No duplicate detection.</summary>
    Off,
    /// <summary>Remember the full key strings. Exact, memory grows with key length.</summary>
    Exact,
    /// <summary>Remember a 128-bit SHA-256 prefix of each key: 16 bytes per key plus set overhead, collision odds negligible.</summary>
    Hash,
}

/// <summary>
/// Streaming duplicate detector with a hard cap on remembered keys. It is deliberately not probabilistic: there are no
/// false positives from a Bloom filter. The price is that memory is O(distinct keys) up to <see cref="MaxKeys"/>; once the
/// cap is reached new keys are no longer remembered (<see cref="Saturated"/> becomes true and the number of unchecked rows is
/// counted), so duplicates of keys first seen after saturation are missed. Duplicates of already-remembered keys are still caught.
/// </summary>
public sealed class KeyTracker
{
    private readonly HashSet<string>? _exact;
    private readonly HashSet<UInt128>? _hashes;

    public KeyTracker(DedupeMode mode, long maxKeys)
    {
        Mode = mode;
        MaxKeys = maxKeys;
        if (mode == DedupeMode.Exact) _exact = new HashSet<string>(StringComparer.Ordinal);
        else if (mode == DedupeMode.Hash) _hashes = new HashSet<UInt128>();
    }

    public DedupeMode Mode { get; }
    public long MaxKeys { get; }
    public long Count => (_exact?.Count ?? _hashes?.Count ?? 0);
    public bool Saturated { get; private set; }
    public long UncheckedKeys { get; private set; }

    /// <summary>Returns false if the key was seen before; true if it is new (or could not be checked).</summary>
    public bool TryAdd(string key)
    {
        if (Mode == DedupeMode.Off) return true;
        if (_exact != null)
        {
            if (_exact.Contains(key)) return false;
            if (Count >= MaxKeys) { Saturated = true; UncheckedKeys++; return true; }
            _exact.Add(key);
            return true;
        }

        var h = Hash(key);
        if (_hashes!.Contains(h)) return false;
        if (Count >= MaxKeys) { Saturated = true; UncheckedKeys++; return true; }
        _hashes.Add(h);
        return true;
    }

    private static UInt128 Hash(string key)
    {
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(key), digest);
        return new UInt128(BitConverter.ToUInt64(digest[8..16]), BitConverter.ToUInt64(digest[..8]));
    }
}
