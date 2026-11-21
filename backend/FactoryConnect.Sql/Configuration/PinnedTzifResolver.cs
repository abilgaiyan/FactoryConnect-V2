using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace FactoryConnect.Sql.Configuration;

/// <summary>
/// Deterministic UTC-to-local conversion using pinned TZif v2/v3/v4 bytes.
/// Does not consult the host operating system timezone database.
/// </summary>
public sealed class PinnedTzifResolver
{
    private readonly long[] transitions;
    private readonly byte[] transitionTypes;
    private readonly int[] offsets;
    private readonly int initialType;

    public PinnedTzifResolver(ReadOnlySpan<byte> rules)
    {
        if (rules.Length < 88 || !rules[..4].SequenceEqual("TZif"u8) ||
            rules[4] is not ((byte)'2' or (byte)'3' or (byte)'4'))
            throw new ArgumentException("TZif v2/v3/v4 rules required.", nameof(rules));

        var first = ReadHeader(rules, 0);
        long firstSize = BlockSize(first, false);
        long secondHeader = 44L + firstSize;
        if (secondHeader > rules.Length - 44 ||
            !rules.Slice((int)secondHeader, 5).SequenceEqual(rules[..5]))
            throw new ArgumentException("Missing matching TZif 64-bit section.", nameof(rules));
        var second = ReadHeader(rules, (int)secondHeader);
        long blockStart = secondHeader + 44;
        long blockEnd = blockStart + BlockSize(second, true);
        if (blockEnd > rules.Length || blockEnd > int.MaxValue)
            throw new ArgumentException("Truncated TZif data.", nameof(rules));
        if (second.TimeCount > 100000 || second.TypeCount > 256)
            throw new ArgumentException("Excessive TZif record count.", nameof(rules));

        var p = (int)blockStart;
        transitions = new long[second.TimeCount];
        for (int i = 0; i < transitions.Length; i++, p += 8)
        {
            transitions[i] = BinaryPrimitives.ReadInt64BigEndian(rules.Slice(p, 8));
            if (i > 0 && transitions[i] <= transitions[i - 1])
                throw new ArgumentException("TZif transitions must be increasing.", nameof(rules));
        }
        transitionTypes = rules.Slice(p, second.TimeCount).ToArray();
        p += second.TimeCount;
        offsets = new int[second.TypeCount];
        var isDst = new bool[second.TypeCount];
        for (int i = 0; i < offsets.Length; i++, p += 6)
        {
            offsets[i] = BinaryPrimitives.ReadInt32BigEndian(rules.Slice(p, 4));
            isDst[i] = rules[p + 4] != 0;
            if (rules[p + 4] > 1 || Math.Abs((long)offsets[i]) > 86400 ||
                rules[p + 5] >= second.CharCount)
                throw new ArgumentException("Invalid TZif local-time type.", nameof(rules));
        }
        if (transitionTypes.Any(t => t >= offsets.Length))
            throw new ArgumentException("TZif transition type out of range.", nameof(rules));

        // RFC 8536: before the first transition use the first standard type, if any.
        initialType = Array.FindIndex(isDst, value => !value);
        if (initialType < 0) initialType = 0;

        // TZif's optional POSIX trailer may define future recurring transitions.
        // Reject any non-empty trailer instead of silently ignoring its rules.
        if (blockEnd < rules.Length)
        {
            var tail = rules[(int)blockEnd..];
            if (tail.Length < 2 || tail[0] != (byte)'\n' || tail[^1] != (byte)'\n')
                throw new ArgumentException("Malformed TZif POSIX trailer.", nameof(rules));
            var posix = Encoding.ASCII.GetString(tail[1..^1]);
            if (posix.Length > 0)
            {
                // Only fixed standard-time tails are supported. POSIX offset signs are reversed.
                var match = Regex.Match(posix, @"^[A-Za-z]{3,}([+-]?)(\d{1,2})(?::(\d{1,2}))?(?::(\d{1,2}))?$",
                    RegexOptions.CultureInvariant);
                if (!match.Success)
                    throw new NotSupportedException("Recurring DST rules in TZif POSIX trailer are unsupported.");
                int magnitude = int.Parse(match.Groups[2].Value) * 3600 +
                    (match.Groups[3].Success ? int.Parse(match.Groups[3].Value) * 60 : 0) +
                    (match.Groups[4].Success ? int.Parse(match.Groups[4].Value) : 0);
                int fixedOffset = match.Groups[1].Value == "-" ? magnitude : -magnitude;
                int finalType = transitions.Length == 0 ? initialType : transitionTypes[^1];
                if (fixedOffset != offsets[finalType])
                    throw new NotSupportedException("TZif trailer offset differs from last transition.");
            }
        }
    }

    public TimeSpan OffsetAt(DateTimeOffset utc)
    {
        long seconds = utc.ToUnixTimeSeconds();
        int index = Array.BinarySearch(transitions, seconds);
        if (index < 0) index = ~index - 1;
        int type = index < 0 ? initialType : transitionTypes[index];
        return TimeSpan.FromSeconds(offsets[type]);
    }

    public DateTimeOffset ToLocal(DateTimeOffset instant)
    {
        var utc = instant.ToUniversalTime();
        return utc.ToOffset(OffsetAt(utc));
    }

    private static Header ReadHeader(ReadOnlySpan<byte> bytes, int start)
    {
        if (start < 0 || bytes.Length - start < 44 ||
            !bytes.Slice(start, 4).SequenceEqual("TZif"u8))
            throw new ArgumentException("Invalid TZif header.");
        int[] counts = new int[6];
        for (int i = 0; i < 6; i++)
        {
            counts[i] = BinaryPrimitives.ReadInt32BigEndian(bytes.Slice(start + 20 + i * 4, 4));
            if (counts[i] < 0) throw new ArgumentException("Negative TZif count.");
        }
        if (counts[4] == 0 || counts[5] == 0)
            throw new ArgumentException("TZif requires at least one type and abbreviation.");
        return new Header(counts[0], counts[1], counts[2], counts[3], counts[4], counts[5]);
    }

    private static long BlockSize(Header h, bool wide) =>
        (long)h.TimeCount * (wide ? 8 : 4) + h.TimeCount +
        (long)h.TypeCount * 6 + h.CharCount +
        (long)h.LeapCount * (wide ? 12 : 8) + h.IsStdCount + h.IsUtcCount;

    private sealed record Header(int IsUtcCount, int IsStdCount, int LeapCount,
        int TimeCount, int TypeCount, int CharCount)
    {
        // Header fields are: ttisgmtcnt, ttisstdcnt, leapcnt, timecnt, typecnt, charcnt.
    }
}
