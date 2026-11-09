using System.Text;
using System.Text.RegularExpressions;

namespace Kql.Net.Runtime;

/// <summary>Options for <c>parse-kv</c>.</summary>
public sealed record KeyValueSpec
{
    /// <summary>Pair delimiter; null selects "non-specified" mode (any non-alphanumeric character delimits).</summary>
    public string? PairDelimiter { get; init; }
    public string? KvDelimiter { get; init; }
    /// <summary>Quote pairs: one char (same open/close) or two (open, close).</summary>
    public IReadOnlyList<string> Quotes { get; init; } = [];
    public IReadOnlyList<char> Escapes { get; init; } = [];
    public bool Greedy { get; init; }
    /// <summary>Regex mode: two capture groups (key, value).</summary>
    public Regex? Regex { get; init; }
}

/// <summary>Extracts key/value pairs from text, following KQL's parse-kv rules.</summary>
public static class KeyValueParser
{
    /// <summary>Returns the first value found for each requested key (raw text, or null when absent).</summary>
    public static string?[] Extract(string? text, IReadOnlyList<string> keys, KeyValueSpec spec)
    {
        var result = new string?[keys.Count];
        if (string.IsNullOrEmpty(text)) return result;
        var wanted = keys.Select((k, i) => (k, i)).ToDictionary(x => x.k, x => x.i, StringComparer.Ordinal);

        void Found(string key, string value)
        {
            if (wanted.TryGetValue(key.Trim(), out var i) && result[i] is null) result[i] = value.Trim();
        }

        if (spec.Regex is { } regex)
        {
            foreach (Match m in regex.Matches(text))
                if (m.Groups.Count >= 3) Found(m.Groups[1].Value, m.Groups[2].Value);
        }
        else if (spec.PairDelimiter is null || spec.KvDelimiter is null)
        {
            NonSpecified(text, keys, spec, Found);
        }
        else
        {
            Specified(text, keys, spec, Found);
        }
        return result;
    }

    private static void Specified(string text, IReadOnlyList<string> keys, KeyValueSpec spec, Action<string, string> found)
    {
        var pair = spec.PairDelimiter!;
        var kv = spec.KvDelimiter!;
        var pos = 0;
        while (pos < text.Length)
        {
            SkipSpaces(text, ref pos);
            if (pos >= text.Length) return;

            // key: quoted, or up to the kv delimiter / pair delimiter (whichever comes first)
            string key;
            if (IsQuoteStart(text, pos, spec))
            {
                key = ReadQuoted(text, ref pos, spec);
            }
            else
            {
                var start = pos;
                while (pos < text.Length && !text.AsSpan(pos).StartsWith(kv) && !text.AsSpan(pos).StartsWith(pair)) pos++;
                key = text[start..pos];
            }

            if (pos >= text.Length || !text.AsSpan(pos).StartsWith(kv))
            {
                // a segment without a kv delimiter (e.g. the "Doe" in "name=John Doe"): skip it
                SkipPastPair(text, ref pos, pair, spec);
                continue;
            }
            pos += kv.Length;
            SkipSpaces(text, ref pos);

            string value;
            if (IsQuoteStart(text, pos, spec))
            {
                value = ReadQuoted(text, ref pos, spec);
                SkipPastPair(text, ref pos, pair, spec);
            }
            else if (spec.Greedy)
            {
                // greedy: the value runs until the next `<pair><known key><kv>` (or the end)
                var end = text.Length;
                foreach (var k in keys)
                {
                    var idx = text.IndexOf(pair + k + kv, pos, StringComparison.Ordinal);
                    if (idx >= 0 && idx < end) end = idx;
                }
                value = text[pos..end];
                pos = Math.Min(text.Length, end + pair.Length);
            }
            else
            {
                var end = text.IndexOf(pair, pos, StringComparison.Ordinal);
                if (end < 0) end = text.Length;
                value = text[pos..end];
                pos = Math.Min(text.Length, end + pair.Length);
            }
            found(key, value);
        }
    }

    /// <summary>Moves past the next pair delimiter (outside quotes), or to the end.</summary>
    private static void SkipPastPair(string text, ref int pos, string pair, KeyValueSpec spec)
    {
        while (pos < text.Length)
        {
            if (IsQuoteStart(text, pos, spec))
            {
                ReadQuoted(text, ref pos, spec);
                continue;
            }
            if (text.AsSpan(pos).StartsWith(pair))
            {
                pos += pair.Length;
                return;
            }
            pos++;
        }
    }

    private static void NonSpecified(string text, IReadOnlyList<string> keys, KeyValueSpec spec, Action<string, string> found)
    {
        foreach (var key in keys)
        {
            for (var at = text.IndexOf(key, StringComparison.Ordinal); at >= 0; at = text.IndexOf(key, at + 1, StringComparison.Ordinal))
            {
                var before = at == 0 || !char.IsLetterOrDigit(text[at - 1]);
                var pos = at + key.Length;
                if (!before || (pos < text.Length && char.IsLetterOrDigit(text[pos]))) continue;
                SkipSpaces(text, ref pos);
                if (pos >= text.Length || char.IsLetterOrDigit(text[pos])) continue; // need a delimiter between key and value
                pos++;
                SkipSpaces(text, ref pos);
                string value;
                if (IsQuoteStart(text, pos, spec)) value = ReadQuoted(text, ref pos, spec);
                else
                {
                    var start = pos;
                    while (pos < text.Length && (char.IsLetterOrDigit(text[pos]) || text[pos] is '_' or '.' or '-')) pos++;
                    value = text[start..pos];
                }
                found(key, value);
                break;
            }
        }
    }

    private static bool IsQuoteStart(string text, int pos, KeyValueSpec spec) =>
        pos < text.Length && spec.Quotes.Any(q => q[0] == text[pos]);

    private static string ReadQuoted(string text, ref int pos, KeyValueSpec spec)
    {
        var open = text[pos];
        var quote = spec.Quotes.First(q => q[0] == open);
        var close = quote.Length > 1 ? quote[1] : quote[0];
        pos++;
        var sb = new StringBuilder();
        while (pos < text.Length && text[pos] != close)
        {
            if (spec.Escapes.Contains(text[pos]) && pos + 1 < text.Length)
            {
                sb.Append(text[pos + 1]);
                pos += 2;
                continue;
            }
            sb.Append(text[pos++]);
        }
        if (pos < text.Length) pos++; // closing quote
        return sb.ToString();
    }

    private static void SkipSpaces(string text, ref int pos)
    {
        while (pos < text.Length && char.IsWhiteSpace(text[pos])) pos++;
    }
}
