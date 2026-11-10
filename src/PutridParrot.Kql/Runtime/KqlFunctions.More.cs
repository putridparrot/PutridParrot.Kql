using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;

namespace PutridParrot.Kql.Runtime;

// Commonly used functions: arrays, bags, sets, parsing, IP addresses, encoding, time zones, formatting.
public static partial class KqlFunctions
{
    private static List<object?> Arr(object? v) => Dynamic.Elements(v).ToList();

    // ---------------------------------------------------------------- math

    [KqlFunction("acos")] public static double? Acos(double? v) => v is null ? null : Math.Acos(v.Value);
    [KqlFunction("asin")] public static double? Asin(double? v) => v is null ? null : Math.Asin(v.Value);
    [KqlFunction("atan")] public static double? Atan(double? v) => v is null ? null : Math.Atan(v.Value);
    [KqlFunction("atan2")] public static double? Atan2(double? y, double? x) => y is null || x is null ? null : Math.Atan2(y.Value, x.Value);
    [KqlFunction("cot")] public static double? Cot(double? v) => v is null ? null : 1 / Math.Tan(v.Value);
    [KqlFunction("degrees")] public static double? Degrees(double? v) => v is null ? null : v * 180 / Math.PI;
    [KqlFunction("radians")] public static double? Radians(double? v) => v is null ? null : v * Math.PI / 180;

    [KqlFunction("around")]
    public static bool? Around(double? value, double? center, double? delta) =>
        value is null || center is null || delta is null ? null : value >= center - delta && value <= center + delta;

    [KqlFunction("around")]
    public static bool? Around(DateTime? value, DateTime? center, TimeSpan? delta) =>
        value is null || center is null || delta is null ? null : value >= center - delta && value <= center + delta;

    // ---------------------------------------------------------------- strings

    [KqlFunction("indexof_regex")]
    public static long? IndexOfRegex(string? s, string? pattern) =>
        s is null || pattern is null ? null : RegexCache.Get(pattern).Match(s) is { Success: true } m ? m.Index : -1;

    [KqlFunction("regex_quote")] public static string? RegexQuote(string? s) => s is null ? null : Regex.Escape(s);

    [KqlFunction("replace_strings")]
    public static string? ReplaceStrings(string? text, object? lookups, object? rewrites)
    {
        if (text is null) return null;
        var from = Arr(lookups).Select(KqlConvert.ToStringValue).ToList();
        var to = Arr(rewrites).Select(KqlConvert.ToStringValue).ToList();
        var sb = new StringBuilder();
        for (var i = 0; i < text.Length;)
        {
            // longest matching lookup wins at each position (no re-replacement of replaced text)
            var best = -1;
            for (var k = 0; k < from.Count; k++)
                if (!string.IsNullOrEmpty(from[k]) && string.CompareOrdinal(text, i, from[k], 0, from[k]!.Length) == 0 && (best < 0 || from[k]!.Length > from[best]!.Length))
                    best = k;
            if (best < 0) sb.Append(text[i++]);
            else
            {
                sb.Append(best < to.Count ? to[best] : "");
                i += from[best]!.Length;
            }
        }
        return sb.ToString();
    }

    [KqlFunction("strcat_array"), KqlFunction("array_strcat")]
    public static string? StrcatArray(object? array, string? delimiter) => string.Join(delimiter ?? "", Arr(array).Select(KqlConvert.ToStringValue));

    [KqlFunction("strcmp")] public static long? StrCmp(string? a, string? b) => Math.Sign(string.CompareOrdinal(a ?? "", b ?? ""));
    [KqlFunction("string_size")] public static long? StringSize(string? s) => s is null ? 0 : Encoding.UTF8.GetByteCount(s);
    [KqlFunction("isascii")] public static bool? IsAscii(string? s) => s is null ? null : s.All(char.IsAscii);
    [KqlFunction("isutf8")] public static bool? IsUtf8(string? s) => s is not null;
    [KqlFunction("url_encode_component")] public static string? UrlEncodeComponent(string? s) => s is null ? null : Uri.EscapeDataString(s);

    [KqlFunction("translate")]
    public static string? Translate(string? searchList, string? replacementList, string? text)
    {
        if (text is null || searchList is null) return text;
        replacementList ??= "";
        var sb = new StringBuilder();
        foreach (var ch in text)
        {
            var i = searchList.IndexOf(ch);
            if (i < 0) sb.Append(ch);
            else if (replacementList.Length > 0) sb.Append(replacementList[Math.Min(i, replacementList.Length - 1)]);
        }
        return sb.ToString();
    }

    [KqlFunction("tohex")] public static string? ToHex(long? v) => v is null ? null : v.Value.ToString("x");
    [KqlFunction("tohex")] public static string? ToHex(long? v, long? minLength) => v is null ? null : v.Value.ToString("x" + Math.Max(0, minLength ?? 0));

    [KqlFunction("dynamic_to_json")] public static string? DynamicToJson(object? v) => KqlConvert.ToJson(Dynamic.Normalize(v));

    /// <summary>Extracts a value with a simple JSONPath ($.a.b[0], $['a']).</summary>
    [KqlFunction("extract_json"), KqlFunction("extractjson")]
    public static object? ExtractJson(string? path, string? json)
    {
        if (path is null || json is null) return null;
        object? current = Dynamic.Parse(json);
        foreach (Match m in Regex.Matches(path, @"\.(?<name>[^.\[]+)|\[(?<index>-?\d+)\]|\['(?<quoted>[^']*)'\]|\[""(?<quoted>[^""]*)""\]"))
        {
            current = m.Groups["index"].Success
                ? KqlConvert.GetElement(current, long.Parse(m.Groups["index"].Value))
                : KqlConvert.GetMember(current, m.Groups["name"].Success ? m.Groups["name"].Value : m.Groups["quoted"].Value);
            if (current is null) return null;
        }
        return current is List<object?> or Dictionary<string, object?> ? KqlConvert.ToJson(current) : KqlConvert.ToStringValue(current);
    }

    [KqlFunction("unicode_codepoints_from_string")]
    public static object? CodepointsFromString(string? s)
    {
        if (s is null) return null;
        var result = new List<object?>();
        for (var e = StringInfo.GetTextElementEnumerator(s); e.MoveNext();)
            result.Add((long)char.ConvertToUtf32((string)e.Current, 0));
        return result;
    }

    [KqlFunction("unicode_codepoints_to_string")]
    public static string? CodepointsToString(params object?[] values) =>
        string.Concat(values.SelectMany(v => v is List<object?> l ? l : [v]).Select(KqlConvert.ToLong).Where(x => x.HasValue).Select(x => char.ConvertFromUtf32((int)x!.Value)));

    // ---------------------------------------------------------------- punycode

    private static readonly IdnMapping Idn = new();
    [KqlFunction("punycode_from_string")] public static string? PunycodeFromString(string? s) => s is null ? null : Idn.GetAscii(s)[(Idn.GetAscii(s).StartsWith("xn--") ? 4 : 0)..];
    [KqlFunction("punycode_to_string")] public static string? PunycodeToString(string? s) => s is null ? null : Idn.GetUnicode("xn--" + s);
    [KqlFunction("punycode_domain_from_string")] public static string? PunycodeDomainFromString(string? s) => s is null ? null : Idn.GetAscii(s);
    [KqlFunction("punycode_domain_to_string")] public static string? PunycodeDomainToString(string? s) => s is null ? null : Idn.GetUnicode(s);

    // ---------------------------------------------------------------- arrays

    [KqlFunction("array_reverse")] public static object? ArrayReverse(object? a) => Arr(a).AsEnumerable().Reverse().ToList();

    [KqlFunction("array_rotate_left")] public static object? ArrayRotateLeft(object? a, long? n) => Rotate(Arr(a), n ?? 0);
    [KqlFunction("array_rotate_right")] public static object? ArrayRotateRight(object? a, long? n) => Rotate(Arr(a), -(n ?? 0));

    private static List<object?> Rotate(List<object?> list, long n)
    {
        if (list.Count == 0) return list;
        var k = (int)(((n % list.Count) + list.Count) % list.Count);
        return list.Skip(k).Concat(list.Take(k)).ToList();
    }

    [KqlFunction("array_shift_left")] public static object? ArrayShiftLeft(object? a, long? n) => Shift(Arr(a), n ?? 0, null);
    [KqlFunction("array_shift_left")] public static object? ArrayShiftLeft(object? a, long? n, object? fill) => Shift(Arr(a), n ?? 0, fill);
    [KqlFunction("array_shift_right")] public static object? ArrayShiftRight(object? a, long? n) => Shift(Arr(a), -(n ?? 0), null);
    [KqlFunction("array_shift_right")] public static object? ArrayShiftRight(object? a, long? n, object? fill) => Shift(Arr(a), -(n ?? 0), fill);

    private static List<object?> Shift(List<object?> list, long n, object? fill)
    {
        var result = new List<object?>(list.Count);
        for (var i = 0; i < list.Count; i++)
        {
            var src = i + n;
            result.Add(src >= 0 && src < list.Count ? list[(int)src] : Dynamic.Normalize(fill));
        }
        return result;
    }

    [KqlFunction("array_slice")]
    public static object? ArraySlice(object? a, long? start, long? end)
    {
        var list = Arr(a);
        if (start is null || end is null) return null;
        var s = start < 0 ? list.Count + start.Value : start.Value;
        var e = end < 0 ? list.Count + end.Value : end.Value;
        s = Math.Max(0, s);
        e = Math.Min(list.Count - 1, e);
        return s > e ? new List<object?>() : list.GetRange((int)s, (int)(e - s + 1));
    }

    [KqlFunction("array_split")]
    public static object? ArraySplit(object? a, object? indices)
    {
        var list = Arr(a);
        var cuts = (indices is List<object?> or System.Collections.IList ? Arr(indices) : [indices])
            .Select(KqlConvert.ToLong).Where(x => x.HasValue).Select(x => (int)Math.Clamp(x!.Value < 0 ? list.Count + x.Value : x.Value, 0, list.Count))
            .Order().ToList();
        var result = new List<object?>();
        var prev = 0;
        foreach (var c in cuts.Append(list.Count))
        {
            result.Add(list.GetRange(prev, Math.Max(0, c - prev)));
            prev = Math.Max(prev, c);
        }
        return result;
    }

    [KqlFunction("array_sort_asc")] public static object? ArraySortAsc(object? a) => ArraySort(a, false, true);
    [KqlFunction("array_sort_asc")] public static object? ArraySortAsc(object? a, bool? nullsLast) => ArraySort(a, false, nullsLast ?? true);
    [KqlFunction("array_sort_desc")] public static object? ArraySortDesc(object? a) => ArraySort(a, true, true);
    [KqlFunction("array_sort_desc")] public static object? ArraySortDesc(object? a, bool? nullsLast) => ArraySort(a, true, nullsLast ?? true);

    private static object? ArraySort(object? a, bool descending, bool nullsLast)
    {
        var list = Arr(a);
        var values = list.Where(v => v is not null).ToList();
        values.Sort(KqlComparer.For<object?>());
        if (descending) values.Reverse();
        var nulls = Enumerable.Repeat<object?>(null, list.Count - values.Count);
        return (nullsLast ? values.Concat(nulls) : nulls.Concat(values)).ToList();
    }

    [KqlFunction("array_iff"), KqlFunction("array_iif")]
    public static object? ArrayIff(object? conditions, object? whenTrue, object? whenFalse)
    {
        var c = Arr(conditions);
        var t = Dynamic.Normalize(whenTrue);
        var f = Dynamic.Normalize(whenFalse);
        object? At(object? v, int i) => v is List<object?> l ? (i < l.Count ? l[i] : null) : v;
        return c.Select((x, i) => KqlConvert.ToBool(x) switch { true => At(t, i), false => At(f, i), null => null }).ToList();
    }

    [KqlFunction("zip")]
    public static object? Zip(params object?[] arrays)
    {
        var lists = arrays.Select(Arr).ToList();
        var n = lists.Count == 0 ? 0 : lists.Max(l => l.Count);
        return Enumerable.Range(0, n).Select(i => (object?)lists.Select(l => i < l.Count ? l[i] : null).ToList()).ToList();
    }

    [KqlFunction("repeat")]
    public static object? Repeat(object? value, long? count) =>
        count is null ? null : Enumerable.Repeat(Dynamic.Normalize(value), (int)Math.Max(0, count.Value)).ToList();

    [KqlFunction("range")] public static object? RangeArray(long? start, long? stop) => RangeArray(start, stop, 1);

    [KqlFunction("range")]
    public static object? RangeArray(long? start, long? stop, long? step)
    {
        if (start is null || stop is null || step is null or 0) return null;
        var result = new List<object?>();
        for (var x = start.Value; step > 0 ? x <= stop : x >= stop; x += step.Value)
        {
            result.Add(x);
            if (result.Count > 1_048_576) break;
        }
        return result;
    }

    [KqlFunction("range")]
    public static object? RangeArray(double? start, double? stop, double? step)
    {
        if (start is null || stop is null || step is null or 0) return null;
        var result = new List<object?>();
        for (var x = start.Value; step > 0 ? x <= stop : x >= stop; x += step.Value) result.Add(x);
        return result;
    }

    [KqlFunction("range")]
    public static object? RangeArray(DateTime? start, DateTime? stop, TimeSpan? step)
    {
        if (start is null || stop is null || step is null || step.Value == TimeSpan.Zero) return null;
        var result = new List<object?>();
        for (var x = start.Value; step > TimeSpan.Zero ? x <= stop : x >= stop; x += step.Value) result.Add(x);
        return result;
    }

    [KqlFunction("jaccard_index")]
    public static double? JaccardIndex(object? a, object? b)
    {
        var sa = Distinct(Arr(a));
        var sb = Distinct(Arr(b));
        var union = Distinct(sa.Concat(sb)).Count;
        var inB = Set(sb);
        return union == 0 ? 0 : (double)sa.Count(inB.Contains) / union;
    }

    // ---------------------------------------------------------------- sets

    private static List<object?> Distinct(IEnumerable<object?> values)
    {
        var seen = new HashSet<object?>(KqlComparer.Values);
        var result = new List<object?>();
        foreach (var v in values)
            if (seen.Add(v)) result.Add(v);
        return result;
    }

    private static HashSet<object?> Set(IEnumerable<object?> values) => new(values, KqlComparer.Values);

    [KqlFunction("set_union")] public static object? SetUnion(params object?[] arrays) => Distinct(arrays.SelectMany(Arr));

    [KqlFunction("set_intersect")]
    public static object? SetIntersect(params object?[] arrays)
    {
        if (arrays.Length == 0) return new List<object?>();
        var others = arrays.Skip(1).Select(a => Set(Arr(a))).ToList();
        return Distinct(Arr(arrays[0])).Where(v => others.All(o => o.Contains(v))).ToList();
    }

    [KqlFunction("set_difference")]
    public static object? SetDifference(params object?[] arrays)
    {
        if (arrays.Length == 0) return new List<object?>();
        var others = Set(arrays.Skip(1).SelectMany(Arr));
        return Distinct(Arr(arrays[0])).Where(v => !others.Contains(v)).ToList();
    }

    [KqlFunction("set_equals")]
    public static bool? SetEquals(object? a, object? b) => Set(Arr(a)).SetEquals(Set(Arr(b)));

    // ---------------------------------------------------------------- bags

    private static Dictionary<string, object?>? Bag(object? v) => Dynamic.Normalize(v) as Dictionary<string, object?>;

    [KqlFunction("bag_has_key")]
    public static bool? BagHasKey(object? bag, string? key) => key is not null && Bag(bag) is { } b && b.ContainsKey(key);

    [KqlFunction("bag_merge")]
    public static object? BagMerge(params object?[] bags)
    {
        var result = new Dictionary<string, object?>();
        foreach (var b in bags.Select(Bag).OfType<Dictionary<string, object?>>())
            foreach (var (k, v) in b) result.TryAdd(k, v); // the first bag wins on duplicate keys
        return result;
    }

    [KqlFunction("bag_remove_keys")]
    public static object? BagRemoveKeys(object? bag, object? keys)
    {
        if (Bag(bag) is not { } b) return null;
        foreach (var k in Arr(keys).Select(KqlConvert.ToStringValue)) if (k is not null) b.Remove(k);
        return b;
    }

    [KqlFunction("bag_set_key")]
    public static object? BagSetKey(object? bag, string? key, object? value)
    {
        if (Bag(bag) is not { } b || key is null) return null;
        b[key] = Dynamic.Normalize(value);
        return b;
    }

    [KqlFunction("bag_zip")]
    public static object? BagZip(object? keys, object? values)
    {
        var k = Arr(keys);
        var v = Arr(values);
        var result = new Dictionary<string, object?>();
        for (var i = 0; i < k.Count; i++)
            if (k[i] is string key) result[key] = i < v.Count ? v[i] : null;
        return result;
    }

    // ---------------------------------------------------------------- parsing

    [KqlFunction("parse_csv")]
    public static object? ParseCsv(string? s) =>
        s is null ? null : CsvTableSource.Records(new StringReader(s)).FirstOrDefault()?.Cast<object?>().ToList() ?? new List<object?> { "" };

    [KqlFunction("parse_url")]
    public static object? ParseUrl(string? s)
    {
        if (s is null || !Uri.TryCreate(s, UriKind.Absolute, out var u)) return new Dictionary<string, object?>();
        var userInfo = u.UserInfo.Split(':', 2);
        var query = HttpUtility.ParseQueryString(u.Query);
        return new Dictionary<string, object?>
        {
            ["Scheme"] = u.Scheme,
            ["Host"] = u.Host,
            ["Port"] = u.IsDefaultPort && !s.Contains($":{u.Port}") ? "" : u.Port.ToString(CultureInfo.InvariantCulture),
            ["Path"] = u.AbsolutePath,
            ["Username"] = userInfo.Length > 0 ? userInfo[0] : "",
            ["Password"] = userInfo.Length > 1 ? userInfo[1] : "",
            ["Query Parameters"] = query.AllKeys.Where(k => k is not null).ToDictionary(k => k!, k => (object?)query[k]),
            ["Fragment"] = u.Fragment.TrimStart('#')
        };
    }

    [KqlFunction("parse_urlquery")]
    public static object? ParseUrlQuery(string? s)
    {
        if (s is null) return null;
        var query = HttpUtility.ParseQueryString(s.TrimStart('?'));
        return new Dictionary<string, object?>
        {
            ["Query Parameters"] = query.AllKeys.Where(k => k is not null).ToDictionary(k => k!, k => (object?)query[k])
        };
    }

    [KqlFunction("parse_path")]
    public static object? ParsePath(string? s)
    {
        if (s is null) return null;
        var isUnc = s.StartsWith(@"\\");
        var sep = s.LastIndexOfAny(['\\', '/']);
        var dir = sep >= 0 ? s[..sep] : "";
        var file = sep >= 0 ? s[(sep + 1)..] : s;
        var dot = file.LastIndexOf('.');
        var root = s.Length >= 2 && s[1] == ':' ? s[..2] : isUnc ? s.Split('\\', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "" : "";
        return new Dictionary<string, object?>
        {
            ["Scheme"] = Uri.TryCreate(s, UriKind.Absolute, out var u) && !u.IsFile ? u.Scheme : "",
            ["RootPath"] = root,
            ["DirectoryPath"] = dir,
            ["DirectoryName"] = dir.Split('\\', '/').LastOrDefault() ?? "",
            ["Filename"] = file,
            ["Extension"] = dot >= 0 ? file[(dot + 1)..] : "",
            ["AlternateDataStreamName"] = ""
        };
    }

    /// <summary>Version string to a comparable decimal: each of up to 4 parts padded to 8 digits.</summary>
    [KqlFunction("parse_version")]
    public static decimal? ParseVersion(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var parts = s.Trim().Split('.');
        if (parts.Length > 4 || parts.Any(p => p.Length is 0 or > 8 || !p.All(char.IsDigit))) return null;
        var padded = string.Concat(parts.Concat(Enumerable.Repeat("0", 4 - parts.Length)).Select(p => p.PadLeft(8, '0')));
        return decimal.Parse(padded, CultureInfo.InvariantCulture);
    }

    [KqlFunction("parse_command_line")]
    public static object? ParseCommandLine(string? s, string? parser)
    {
        if (s is null) return null;
        var args = new List<object?>();
        var current = new StringBuilder();
        var inQuotes = false;
        var any = false;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '\\')
            {
                var slashes = 0;
                while (i < s.Length && s[i] == '\\') { slashes++; i++; }
                if (i < s.Length && s[i] == '"')
                {
                    current.Append('\\', slashes / 2);
                    if (slashes % 2 == 1) current.Append('"');
                    else { inQuotes = !inQuotes; }
                }
                else
                {
                    current.Append('\\', slashes);
                    i--;
                }
                any = true;
                continue;
            }
            if (c == '"') { inQuotes = !inQuotes; any = true; continue; }
            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (any) args.Add(current.ToString());
                current.Clear();
                any = false;
                continue;
            }
            current.Append(c);
            any = true;
        }
        if (any) args.Add(current.ToString());
        return args;
    }

    // ---------------------------------------------------------------- IPv4

    private static (uint Address, int Prefix)? Ipv4(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var slash = s.IndexOf('/');
        var prefix = 32;
        if (slash >= 0 && !int.TryParse(s[(slash + 1)..], out prefix)) return null;
        var text = slash >= 0 ? s[..slash] : s;
        var parts = text.Trim().Split('.');
        if (parts.Length != 4) return null;
        uint value = 0;
        foreach (var p in parts)
        {
            if (!byte.TryParse(p, out var b)) return null;
            value = (value << 8) | b;
        }
        return prefix is < 0 or > 32 ? null : (value, prefix);
    }

    private static uint Mask(int prefix) => prefix == 0 ? 0u : uint.MaxValue << (32 - prefix);

    private static string FormatIpv4(uint v) => $"{v >> 24}.{(v >> 16) & 255}.{(v >> 8) & 255}.{v & 255}";

    [KqlFunction("parse_ipv4")] public static long? ParseIpv4(string? s) => Ipv4(s) is { } ip ? ip.Address & Mask(ip.Prefix) : null;

    [KqlFunction("parse_ipv4_mask")]
    public static long? ParseIpv4Mask(string? s, long? prefix) => Ipv4(s) is { } ip && prefix is >= 0 and <= 32 ? ip.Address & Mask((int)prefix) : null;

    [KqlFunction("format_ipv4")] public static string? FormatIpv4(object? ip) => FormatIpv4Mask(ip, 32);
    [KqlFunction("format_ipv4")] public static string? FormatIpv4(object? ip, long? prefix) => FormatIpv4Mask(ip, prefix, includePrefix: false);

    [KqlFunction("format_ipv4_mask")] public static string? FormatIpv4Mask(object? ip) => FormatIpv4Mask(ip, 32, includePrefix: true);
    [KqlFunction("format_ipv4_mask")] public static string? FormatIpv4Mask(object? ip, long? prefix) => FormatIpv4Mask(ip, prefix, includePrefix: true);

    private static string? FormatIpv4Mask(object? ip, long? prefix, bool includePrefix = false)
    {
        uint? address = ip is string s ? Ipv4(s)?.Address : KqlConvert.ToLong(ip) is { } l and >= 0 and <= uint.MaxValue ? (uint)l : null;
        if (address is null || prefix is null or < 0 or > 32) return null;
        var masked = FormatIpv4(address.Value & Mask((int)prefix));
        return includePrefix ? $"{masked}/{prefix}" : masked;
    }

    [KqlFunction("ipv4_compare")] public static long? Ipv4Compare(string? a, string? b) => Ipv4Compare(a, b, 32);

    [KqlFunction("ipv4_compare")]
    public static long? Ipv4Compare(string? a, string? b, long? prefix)
    {
        if (Ipv4(a) is not { } x || Ipv4(b) is not { } y || prefix is null) return null;
        var mask = Mask(Math.Min((int)prefix, Math.Min(x.Prefix, y.Prefix)));
        return ((x.Address & mask).CompareTo(y.Address & mask)) switch { < 0 => -1, > 0 => 1, _ => 0 };
    }

    [KqlFunction("ipv4_is_match")] public static bool? Ipv4IsMatch(string? a, string? b) => Ipv4Compare(a, b) is { } c ? c == 0 : null;
    [KqlFunction("ipv4_is_match")] public static bool? Ipv4IsMatch(string? a, string? b, long? prefix) => Ipv4Compare(a, b, prefix) is { } c ? c == 0 : null;

    [KqlFunction("ipv4_is_in_range")]
    public static bool? Ipv4IsInRange(string? ip, string? range) =>
        Ipv4(ip) is { } a && Ipv4(range) is { } r ? (a.Address & Mask(r.Prefix)) == (r.Address & Mask(r.Prefix)) : null;

    [KqlFunction("ipv4_is_in_any_range")]
    public static bool? Ipv4IsInAnyRange(string? ip, params object?[] ranges) =>
        Ipv4(ip) is null ? null : ranges.SelectMany(r => r is List<object?> l ? l : [r]).Any(r => Ipv4IsInRange(ip, KqlConvert.ToStringValue(r)) == true);

    [KqlFunction("ipv4_is_private")]
    public static bool? Ipv4IsPrivate(string? ip) =>
        Ipv4(ip) is null ? null : new[] { "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16" }.Any(r => Ipv4IsInRange(ip, r) == true);

    [KqlFunction("ipv4_netmask_suffix")] public static long? Ipv4NetmaskSuffix(string? ip) => Ipv4(ip)?.Prefix;

    [KqlFunction("ipv4_range_to_cidr_list")]
    public static object? Ipv4RangeToCidrList(string? start, string? end)
    {
        if (Ipv4(start) is not { } s || Ipv4(end) is not { } e || s.Address > e.Address) return null;
        var result = new List<object?>();
        ulong current = s.Address;
        while (current <= e.Address)
        {
            var size = 32;
            while (size > 0)
            {
                var mask = Mask(size - 1);
                if ((current & mask) != current) break;
                if (current + (1UL << (32 - (size - 1))) - 1 > e.Address) break;
                size--;
            }
            result.Add($"{FormatIpv4((uint)current)}/{size}");
            current += 1UL << (32 - size);
        }
        return result;
    }

    private static readonly Regex Ipv4InText = new(@"(?<![\w.])(\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3})(?![\w.])", RegexOptions.Compiled);

    [KqlFunction("has_ipv4")]
    public static bool? HasIpv4(string? text, string? ip) =>
        text is not null && Ipv4(ip) is { } target && Ipv4InText.Matches(text).Any(m => Ipv4(m.Value)?.Address == target.Address);

    [KqlFunction("has_ipv4_prefix")]
    public static bool? HasIpv4Prefix(string? text, string? prefix) =>
        text is not null && prefix is not null && Ipv4InText.Matches(text).Any(m => m.Value.StartsWith(prefix, StringComparison.Ordinal));

    [KqlFunction("has_any_ipv4")]
    public static bool? HasAnyIpv4(string? text, params object?[] ips) =>
        ips.SelectMany(i => i is List<object?> l ? l : [i]).Any(i => HasIpv4(text, KqlConvert.ToStringValue(i)) == true);

    [KqlFunction("has_any_ipv4_prefix")]
    public static bool? HasAnyIpv4Prefix(string? text, params object?[] prefixes) =>
        prefixes.SelectMany(i => i is List<object?> l ? l : [i]).Any(i => HasIpv4Prefix(text, KqlConvert.ToStringValue(i)) == true);

    // ---------------------------------------------------------------- IPv6

    private static (BigInteger Address, int Prefix)? Ipv6(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        var slash = s.IndexOf('/');
        var text = slash >= 0 ? s[..slash] : s;
        if (!IPAddress.TryParse(text.Trim(), out var ip)) return null;
        if (ip.AddressFamily == AddressFamily.InterNetwork) ip = ip.MapToIPv6();
        var prefix = slash >= 0 && int.TryParse(s[(slash + 1)..], out var p) ? p : 128;
        if (Ipv4(text) is not null && slash >= 0) prefix += 96; // IPv4 prefixes are relative to the mapped address
        return (new BigInteger(ip.GetAddressBytes(), isUnsigned: true, isBigEndian: true), Math.Clamp(prefix, 0, 128));
    }

    private static BigInteger Mask6(int prefix) => ((BigInteger.One << 128) - 1) ^ ((BigInteger.One << (128 - prefix)) - 1);

    [KqlFunction("ipv6_compare")] public static long? Ipv6Compare(string? a, string? b) => Ipv6Compare(a, b, 128);

    [KqlFunction("ipv6_compare")]
    public static long? Ipv6Compare(string? a, string? b, long? prefix)
    {
        if (Ipv6(a) is not { } x || Ipv6(b) is not { } y || prefix is null) return null;
        var mask = Mask6(Math.Min((int)prefix, Math.Min(x.Prefix, y.Prefix)));
        return (x.Address & mask).CompareTo(y.Address & mask) switch { < 0 => -1, > 0 => 1, _ => 0 };
    }

    [KqlFunction("ipv6_is_match")] public static bool? Ipv6IsMatch(string? a, string? b) => Ipv6Compare(a, b) is { } c ? c == 0 : null;
    [KqlFunction("ipv6_is_match")] public static bool? Ipv6IsMatch(string? a, string? b, long? prefix) => Ipv6Compare(a, b, prefix) is { } c ? c == 0 : null;

    [KqlFunction("ipv6_is_in_range")]
    public static bool? Ipv6IsInRange(string? ip, string? range) =>
        Ipv6(ip) is { } a && Ipv6(range) is { } r ? (a.Address & Mask6(r.Prefix)) == (r.Address & Mask6(r.Prefix)) : null;

    [KqlFunction("ipv6_is_in_any_range")]
    public static bool? Ipv6IsInAnyRange(string? ip, params object?[] ranges) =>
        Ipv6(ip) is null ? null : ranges.SelectMany(r => r is List<object?> l ? l : [r]).Any(r => Ipv6IsInRange(ip, KqlConvert.ToStringValue(r)) == true);

    [KqlFunction("parse_ipv6")]
    public static string? ParseIpv6(string? s) => Ipv6(s) is { } ip ? FormatIpv6(ip.Address & Mask6(ip.Prefix)) : null;

    [KqlFunction("parse_ipv6_mask")]
    public static string? ParseIpv6Mask(string? s, long? prefix) =>
        Ipv6(s) is { } ip && prefix is >= 0 and <= 128 ? FormatIpv6(ip.Address & Mask6(Math.Min(ip.Prefix, (int)prefix + (Ipv4(s?.Split('/')[0]) is null ? 0 : 96)))) : null;

    private static string FormatIpv6(BigInteger value)
    {
        var bytes = value.ToByteArray(isUnsigned: true, isBigEndian: true);
        var full = new byte[16];
        Array.Copy(bytes, 0, full, 16 - bytes.Length, bytes.Length);
        var groups = Enumerable.Range(0, 8).Select(i => ((full[i * 2] << 8) | full[i * 2 + 1]).ToString("x4"));
        return string.Join(":", groups);
    }

    // ---------------------------------------------------------------- encoding, hashing, binary

    [KqlFunction("base64_encode_fromguid")] public static string? Base64EncodeFromGuid(Guid? g) => g is null ? null : Convert.ToBase64String(g.Value.ToByteArray());

    [KqlFunction("base64_decode_toguid")]
    public static Guid? Base64DecodeToGuid(string? s)
    {
        try { return s is null ? null : new Guid(Convert.FromBase64String(s)); }
        catch (Exception e) when (e is FormatException or ArgumentException) { return null; }
    }

    [KqlFunction("base64_decode_toarray")]
    public static object? Base64DecodeToArray(string? s)
    {
        try { return s is null ? null : Convert.FromBase64String(s).Select(b => (object?)(long)b).ToList(); }
        catch (FormatException) { return null; }
    }

    [KqlFunction("base64_encode_fromarray")]
    public static string? Base64EncodeFromArray(object? bytes) =>
        Convert.ToBase64String(Arr(bytes).Select(b => (byte)(KqlConvert.ToLong(b) ?? 0)).ToArray());

    [KqlFunction("hash_sha1")] public static string? HashSha1(string? s) => s is null ? null : Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes(s)));

    [KqlFunction("hash_combine")]
    public static long? HashCombine(params long?[] hashes) => hashes.Aggregate(17L, (acc, h) => unchecked(acc * 31 + (h ?? 0)));

    [KqlFunction("hash_many")]
    public static long? HashMany(params object?[] values) => HashCombine(values.Select(Hash).ToArray());

    [KqlFunction("binary_and")] public static long? BinaryAnd(long? a, long? b) => a & b;
    [KqlFunction("binary_or")] public static long? BinaryOr(long? a, long? b) => a | b;
    [KqlFunction("binary_xor")] public static long? BinaryXor(long? a, long? b) => a ^ b;
    [KqlFunction("binary_not")] public static long? BinaryNot(long? a) => ~a;
    [KqlFunction("binary_shift_left")] public static long? BinaryShiftLeft(long? a, long? n) => a is null || n is null ? null : a.Value << (int)n.Value;
    [KqlFunction("binary_shift_right")] public static long? BinaryShiftRight(long? a, long? n) => a is null || n is null ? null : a.Value >> (int)n.Value;
    [KqlFunction("bitset_count_ones")] public static long? BitsetCountOnes(long? a) => a is null ? null : BitOperations.PopCount((ulong)a.Value);

    [KqlFunction("gzip_compress_to_base64_string")]
    public static string? GzipCompress(string? s) => s is null ? null : Compress(s, stream => new GZipStream(stream, CompressionLevel.Optimal));

    [KqlFunction("gzip_decompress_from_base64_string")]
    public static string? GzipDecompress(string? s) => Decompress(s, stream => new GZipStream(stream, CompressionMode.Decompress));

    [KqlFunction("zlib_compress_to_base64_string")]
    public static string? ZlibCompress(string? s) => s is null ? null : Compress(s, stream => new ZLibStream(stream, CompressionLevel.Optimal));

    [KqlFunction("zlib_decompress_from_base64_string")]
    public static string? ZlibDecompress(string? s) => Decompress(s, stream => new ZLibStream(stream, CompressionMode.Decompress));

    private static string Compress(string s, Func<Stream, Stream> wrap)
    {
        using var output = new MemoryStream();
        using (var z = wrap(output)) z.Write(Encoding.UTF8.GetBytes(s));
        return Convert.ToBase64String(output.ToArray());
    }

    private static string? Decompress(string? s, Func<Stream, Stream> wrap)
    {
        if (s is null) return null;
        try
        {
            using var z = wrap(new MemoryStream(Convert.FromBase64String(s)));
            using var reader = new StreamReader(z, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch (Exception e) when (e is FormatException or InvalidDataException) { return null; }
    }

    // ---------------------------------------------------------------- time zones, unix time, formatting

    private static TimeZoneInfo? Zone(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException) { return null; }
    }

    [KqlFunction("datetime_utc_to_local")]
    public static DateTime? DateTimeUtcToLocal(DateTime? utc, string? timezone) =>
        utc is null || Zone(timezone) is not { } z ? null : DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc.Value, DateTimeKind.Utc), z), DateTimeKind.Unspecified);

    [KqlFunction("datetime_local_to_utc")]
    public static DateTime? DateTimeLocalToUtc(DateTime? local, string? timezone) =>
        local is null || Zone(timezone) is not { } z ? null : TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local.Value, DateTimeKind.Unspecified), z);

    [KqlFunction("datetime_list_timezones")]
    public static object? DateTimeListTimezones() => TimeZoneInfo.GetSystemTimeZones().Select(z => (object?)z.Id).ToList();

    [KqlFunction("unixtime_microseconds_todatetime")]
    public static DateTime? UnixMicrosToDateTime(double? us) => us is null ? null : DateTime.UnixEpoch.AddTicks((long)(us.Value * 10));

    [KqlFunction("unixtime_nanoseconds_todatetime")]
    public static DateTime? UnixNanosToDateTime(double? ns) => ns is null ? null : DateTime.UnixEpoch.AddTicks((long)(ns.Value / 100));

    [KqlFunction("format_bytes")] public static string? FormatBytes(double? size) => FormatBytes(size, 0, null, 1024);
    [KqlFunction("format_bytes")] public static string? FormatBytes(double? size, long? precision) => FormatBytes(size, precision, null, 1024);
    [KqlFunction("format_bytes")] public static string? FormatBytes(double? size, long? precision, string? units) => FormatBytes(size, precision, units, 1024);

    [KqlFunction("format_bytes")]
    public static string? FormatBytes(double? size, long? precision, string? units, long? @base)
    {
        if (size is null) return null;
        var b = @base == 1000 ? 1000.0 : 1024.0;
        string[] names = @base == 1000 ? ["Bytes", "KB", "MB", "GB", "TB", "PB", "EB"] : ["Bytes", "KB", "MB", "GB", "TB", "PB", "EB"];
        var index = string.IsNullOrEmpty(units)
            ? Math.Clamp(size.Value == 0 ? 0 : (int)Math.Floor(Math.Log(Math.Abs(size.Value)) / Math.Log(b)), 0, names.Length - 1)
            : Array.FindIndex(names, n => string.Equals(n, units, StringComparison.OrdinalIgnoreCase));
        if (index < 0) return null;
        var value = size.Value / Math.Pow(b, index);
        return value.ToString("F" + Math.Max(0, precision ?? 0), CultureInfo.InvariantCulture) + " " + names[index];
    }

    // ---------------------------------------------------------------- sketches (exact implementations)

    /// <summary>dcount of an hll() / hll_merge() sketch. PutridParrot.Kql's sketches are exact (the distinct values themselves).</summary>
    [KqlFunction("dcount_hll")]
    public static long? DcountHll(object? sketch) => Bag(sketch)?.GetValueOrDefault("kql_hll") is List<object?> l ? l.Count : 0;

    [KqlFunction("percentile_tdigest")]
    public static object? PercentileTdigest(object? digest, double? percentile) =>
        KqlAggregates.Percentile(TdigestValues(digest), percentile);

    [KqlFunction("percentile_tdigest")]
    public static object? PercentileTdigest(object? digest, double? percentile, string? _) => PercentileTdigest(digest, percentile);

    [KqlFunction("percentiles_array_tdigest")]
    public static object? PercentilesArrayTdigest(object? digest, params object?[] percentiles) =>
        percentiles.SelectMany(p => p is List<object?> l ? l : [p]).Select(p => KqlAggregates.Percentile(TdigestValues(digest), KqlConvert.ToDouble(p))).ToList();

    [KqlFunction("rank_tdigest")]
    public static long? RankTdigest(object? digest, object? value)
    {
        var cmp = KqlComparer.For<object?>();
        var v = Dynamic.Normalize(value);
        return TdigestValues(digest).Count(x => cmp.Compare(x, v) <= 0);
    }

    [KqlFunction("percentrank_tdigest")]
    public static double? PercentrankTdigest(object? digest, object? value)
    {
        var values = TdigestValues(digest).ToList();
        return values.Count == 0 ? null : 100.0 * RankTdigest(digest, value) / values.Count;
    }

    private static IEnumerable<object?> TdigestValues(object? digest) =>
        Bag(digest)?.GetValueOrDefault("kql_tdigest") is List<object?> l ? l : [];
}
