using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Kql.Net.Runtime;

/// <summary>Marks a static method as the in-memory implementation of a KQL scalar function.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class KqlFunctionAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}

/// <summary>
/// In-memory implementations of KQL operators and scalar functions. All methods are null-safe and
/// follow KQL semantics (string comparisons are ordinal / OrdinalIgnoreCase; invalid input yields null).
/// </summary>
public static partial class KqlFunctions
{
    // ---------------------------------------------------------------- string operators

    public static bool EqualsIgnoreCase(string? a, string? b) => string.Equals(a ?? "", b ?? "", StringComparison.OrdinalIgnoreCase);
    public static bool EqualsOrdinal(string? a, string? b) => string.Equals(a ?? "", b ?? "", StringComparison.Ordinal);
    public static int CompareOrdinal(string? a, string? b) => string.CompareOrdinal(a, b);

    public static bool Contains(string? s, string? v, bool cs) =>
        (s ?? "").Contains(v ?? "", cs ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    public static bool StartsWith(string? s, string? v, bool cs) =>
        (s ?? "").StartsWith(v ?? "", cs ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    public static bool EndsWith(string? s, string? v, bool cs) =>
        (s ?? "").EndsWith(v ?? "", cs ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    /// <summary>KQL <c>has</c>: the term appears with non-alphanumeric boundaries on both sides.</summary>
    public static bool Has(string? s, string? term, bool cs) => TermMatch(s, term, cs, true, true);
    public static bool HasPrefix(string? s, string? term, bool cs) => TermMatch(s, term, cs, true, false);
    public static bool HasSuffix(string? s, string? term, bool cs) => TermMatch(s, term, cs, false, true);

    private static bool TermMatch(string? s, string? term, bool cs, bool leftBoundary, bool rightBoundary)
    {
        s ??= "";
        term ??= "";
        if (term.Length == 0) return true;
        var cmp = cs ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        for (var i = s.IndexOf(term, cmp); i >= 0; i = s.IndexOf(term, i + 1, cmp))
        {
            var leftOk = !leftBoundary || i == 0 || !char.IsLetterOrDigit(s[i - 1]) || !char.IsLetterOrDigit(term[0]);
            var end = i + term.Length;
            var rightOk = !rightBoundary || end == s.Length || !char.IsLetterOrDigit(s[end]) || !char.IsLetterOrDigit(term[^1]);
            if (leftOk && rightOk) return true;
        }
        return false;
    }

    public static bool HasAny(string? s, string?[] terms) => terms.Any(t => Has(s, t, false));
    public static bool HasAll(string? s, string?[] terms) => terms.All(t => Has(s, t, false));

    public static bool InIgnoreCase(string? s, string?[] values) => values.Any(v => EqualsIgnoreCase(s, v));

    public static bool MatchesRegex(string? s, string? pattern) =>
        s is not null && pattern is not null && RegexCache.Get(pattern).IsMatch(s);

    public static bool Like(string? s, string? pattern, bool cs)
    {
        if (s is null || pattern is null) return false;
        var regex = "^" + Regex.Escape(pattern).Replace("%", ".*").Replace("_", ".") + "$";
        return Regex.IsMatch(s, regex, cs ? RegexOptions.None : RegexOptions.IgnoreCase);
    }

    // ---------------------------------------------------------------- string functions

    [KqlFunction("strlen")] public static long? Strlen(string? s) => s?.Length ?? 0;
    [KqlFunction("tolower")] public static string? ToLower(string? s) => s?.ToLowerInvariant();
    [KqlFunction("toupper")] public static string? ToUpper(string? s) => s?.ToUpperInvariant();
    [KqlFunction("reverse")] public static string? Reverse(string? s) => s is null ? null : new string(s.Reverse().ToArray());

    [KqlFunction("strcat")]
    public static string StrCat(params object?[] values) => string.Concat(values.Select(KqlConvert.ToStringValue));

    [KqlFunction("strcat_delim")]
    public static string StrCatDelim(string? delimiter, params object?[] values) =>
        string.Join(delimiter ?? "", values.Select(KqlConvert.ToStringValue));

    [KqlFunction("substring")]
    public static string? Substring(string? s, long? start, long? length)
    {
        if (s is null || start is null) return null;
        var st = (int)Math.Clamp(start.Value, 0, s.Length);
        var len = length is null ? s.Length - st : (int)Math.Clamp(length.Value, 0, s.Length - st);
        return s.Substring(st, len);
    }

    [KqlFunction("substring")] public static string? Substring(string? s, long? start) => Substring(s, start, null);

    [KqlFunction("indexof")]
    public static long? IndexOf(string? s, string? lookup) => s is null || lookup is null ? null : s.IndexOf(lookup, StringComparison.Ordinal);

    [KqlFunction("indexof")]
    public static long? IndexOf(string? s, string? lookup, long? start)
    {
        if (s is null || lookup is null || start is null) return null;
        var st = start.Value < 0 ? s.Length + (int)start.Value : (int)start.Value;
        return st < 0 || st > s.Length ? -1 : s.IndexOf(lookup, st, StringComparison.Ordinal);
    }

    [KqlFunction("replace_string")]
    public static string? ReplaceString(string? s, string? lookup, string? rewrite) =>
        s is null || string.IsNullOrEmpty(lookup) ? s : s.Replace(lookup, rewrite ?? "", StringComparison.Ordinal);

    [KqlFunction("replace_regex")]
    public static string? ReplaceRegex(string? s, string? pattern, string? rewrite) =>
        s is null || pattern is null ? s : RegexCache.Get(pattern).Replace(s, (rewrite ?? "").Replace(@"\", "$"));

    [KqlFunction("trim")]
    public static string? Trim(string? pattern, string? s) => TrimEnd(pattern, TrimStart(pattern, s));

    [KqlFunction("trim_start")]
    public static string? TrimStart(string? pattern, string? s) =>
        s is null || pattern is null ? s : Regex.Replace(s, "^(?:" + pattern + ")+", "");

    [KqlFunction("trim_end")]
    public static string? TrimEnd(string? pattern, string? s) =>
        s is null || pattern is null ? s : Regex.Replace(s, "(?:" + pattern + ")+$", "");

    [KqlFunction("split")]
    public static object? Split(string? s, string? delimiter) =>
        s is null ? null : s.Split(delimiter ?? "").Cast<object?>().ToList();

    [KqlFunction("split")]
    public static object? Split(string? s, string? delimiter, long? index)
    {
        if (s is null || index is null) return null;
        var parts = s.Split(delimiter ?? "");
        return index >= 0 && index < parts.Length ? new List<object?> { parts[index.Value] } : new List<object?>();
    }

    [KqlFunction("extract")]
    public static string? Extract(string? pattern, long? group, string? s)
    {
        if (pattern is null || group is null || s is null) return null;
        var m = RegexCache.Get(pattern).Match(s);
        return m.Success && group < m.Groups.Count ? m.Groups[(int)group.Value].Value : null;
    }

    [KqlFunction("extract_all")]
    public static object? ExtractAll(string? pattern, string? s)
    {
        if (pattern is null || s is null) return null;
        var re = RegexCache.Get(pattern);
        var list = new List<object?>();
        foreach (Match m in re.Matches(s))
        {
            if (m.Groups.Count == 1) list.Add(m.Value);
            else if (m.Groups.Count == 2) list.Add(m.Groups[1].Value);
            else list.Add(m.Groups.Cast<Group>().Skip(1).Select(g => (object?)g.Value).ToList());
        }
        return list.Count == 0 ? null : list;
    }

    [KqlFunction("countof")]
    public static long? CountOf(string? s, string? search)
    {
        if (s is null || string.IsNullOrEmpty(search)) return 0;
        long n = 0;
        for (var i = s.IndexOf(search, StringComparison.Ordinal); i >= 0; i = s.IndexOf(search, i + search.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    [KqlFunction("countof")]
    public static long? CountOf(string? s, string? search, string? kind) =>
        kind == "regex" ? (s is null || search is null ? 0 : RegexCache.Get(search).Matches(s).Count) : CountOf(s, search);

    [KqlFunction("strrep")]
    public static string? StrRep(string? s, long? count) =>
        s is null || count is null ? null : string.Concat(Enumerable.Repeat(s, (int)Math.Max(0, count.Value)));

    [KqlFunction("strrep")]
    public static string? StrRep(string? s, long? count, string? delimiter) =>
        s is null || count is null ? null : string.Join(delimiter ?? "", Enumerable.Repeat(s, (int)Math.Max(0, count.Value)));

    [KqlFunction("hash_md5")]
    public static string? HashMd5(string? s) => s is null ? null : Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(s)));

    [KqlFunction("hash_sha256")]
    public static string? HashSha256(string? s) => s is null ? null : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    [KqlFunction("base64_encode_tostring")]
    public static string? Base64Encode(string? s) => s is null ? null : Convert.ToBase64String(Encoding.UTF8.GetBytes(s));

    [KqlFunction("base64_decode_tostring")]
    public static string? Base64Decode(string? s)
    {
        try { return s is null ? null : Encoding.UTF8.GetString(Convert.FromBase64String(s)); }
        catch (FormatException) { return null; }
    }

    [KqlFunction("url_encode")] public static string? UrlEncode(string? s) => s is null ? null : Uri.EscapeDataString(s);
    [KqlFunction("url_decode")] public static string? UrlDecode(string? s) => s is null ? null : Uri.UnescapeDataString(s);

    // ---------------------------------------------------------------- emptiness

    [KqlFunction("isempty")]
    public static bool IsEmpty(object? v) => v is null || v is string { Length: 0 };

    [KqlFunction("isnotempty")]
    public static bool IsNotEmpty(object? v) => !IsEmpty(v);

    // ---------------------------------------------------------------- conversions

    [KqlFunction("tostring")] public static string? ToStringFn(object? v) => KqlConvert.ToStringValue(v) ?? "";
    [KqlFunction("toint")] public static int? ToInt(object? v) => KqlConvert.ToInt(v);
    [KqlFunction("tolong")] public static long? ToLong(object? v) => KqlConvert.ToLong(v);
    [KqlFunction("todouble"), KqlFunction("toreal")] public static double? ToDouble(object? v) => KqlConvert.ToDouble(v);
    [KqlFunction("todecimal")] public static decimal? ToDecimal(object? v) => KqlConvert.ToDecimal(v);
    [KqlFunction("tobool"), KqlFunction("toboolean")] public static bool? ToBool(object? v) => KqlConvert.ToBool(v);
    [KqlFunction("todatetime")] public static DateTime? ToDateTime(object? v) => KqlConvert.ToDateTime(v);
    [KqlFunction("totimespan")] public static TimeSpan? ToTimeSpan(object? v) => KqlConvert.ToTimeSpan(v);
    [KqlFunction("toguid")] public static Guid? ToGuid(object? v) => KqlConvert.ToGuid(v);
    [KqlFunction("parse_json"), KqlFunction("todynamic"), KqlFunction("toobject")]
    public static object? ParseJson(object? v) => v is string s ? Dynamic.Parse(s) : Dynamic.Normalize(v);

    // ---------------------------------------------------------------- dynamic

    [KqlFunction("array_length")]
    public static long? ArrayLength(object? v) => Dynamic.Normalize(v) is List<object?> l ? l.Count : null;

    [KqlFunction("pack_array")]
    public static object? PackArray(params object?[] values) => values.Select(Dynamic.Normalize).ToList();

    [KqlFunction("pack"), KqlFunction("bag_pack")]
    public static object? Pack(params object?[] keysAndValues)
    {
        var d = new Dictionary<string, object?>();
        for (var i = 0; i + 1 < keysAndValues.Length; i += 2) d[KqlConvert.ToStringValue(keysAndValues[i]) ?? ""] = Dynamic.Normalize(keysAndValues[i + 1]);
        return d;
    }

    [KqlFunction("bag_keys")]
    public static object? BagKeys(object? v) => Dynamic.Normalize(v) is Dictionary<string, object?> d ? d.Keys.Cast<object?>().ToList() : null;

    [KqlFunction("array_concat")]
    public static object? ArrayConcat(params object?[] arrays) => arrays.SelectMany(Dynamic.Elements).ToList();

    [KqlFunction("array_index_of")]
    public static long? ArrayIndexOf(object? array, object? value)
    {
        var list = Dynamic.Elements(array).ToList();
        var nv = Dynamic.Normalize(value);
        return list.FindIndex(x => KqlComparer.ValueEquals(x, nv));
    }

    [KqlFunction("set_has_element")]
    public static bool SetHasElement(object? array, object? value) => ArrayIndexOf(array, value) >= 0;

    [KqlFunction("array_sum")]
    public static double? ArraySum(object? array) => Dynamic.Elements(array).Select(KqlConvert.ToDouble).Sum();

    [KqlFunction("gettype")]
    public static string GetKqlType(object? v) => Dynamic.Normalize(v) switch
    {
        null => "null",
        string => "string",
        bool => "bool",
        long => "long",
        double => "real",
        decimal => "decimal",
        DateTime => "datetime",
        TimeSpan => "timespan",
        Guid => "guid",
        List<object?> => "array",
        Dictionary<string, object?> => "dictionary",
        _ => "object"
    };

    // ---------------------------------------------------------------- math

    [KqlFunction("abs")] public static long? Abs(long? v) => v is null ? null : Math.Abs(v.Value);
    [KqlFunction("abs")] public static double? Abs(double? v) => v is null ? null : Math.Abs(v.Value);
    [KqlFunction("abs")] public static TimeSpan? Abs(TimeSpan? v) => v?.Duration();
    [KqlFunction("sqrt")] public static double? Sqrt(double? v) => v is null ? null : Math.Sqrt(v.Value);
    [KqlFunction("pow")] public static double? Pow(double? a, double? b) => a is null || b is null ? null : Math.Pow(a.Value, b.Value);
    [KqlFunction("exp")] public static double? Exp(double? v) => v is null ? null : Math.Exp(v.Value);
    [KqlFunction("exp2")] public static double? Exp2(double? v) => v is null ? null : Math.Pow(2, v.Value);
    [KqlFunction("exp10")] public static double? Exp10(double? v) => v is null ? null : Math.Pow(10, v.Value);
    [KqlFunction("log")] public static double? Log(double? v) => v is null ? null : Math.Log(v.Value);
    [KqlFunction("log2")] public static double? Log2(double? v) => v is null ? null : Math.Log2(v.Value);
    [KqlFunction("log10")] public static double? Log10(double? v) => v is null ? null : Math.Log10(v.Value);
    [KqlFunction("sign")] public static double? Sign(double? v) => v is null ? null : Math.Sign(v.Value);
    [KqlFunction("ceiling")] public static double? Ceiling(double? v) => v is null ? null : Math.Ceiling(v.Value);
    [KqlFunction("ceiling")] public static long? Ceiling(long? v) => v;
    [KqlFunction("pi")] public static double Pi() => Math.PI;
    [KqlFunction("rand")] public static double Rand() => Random.Shared.NextDouble();
    [KqlFunction("rand")] public static long? Rand(long? max) => max is null ? null : Random.Shared.NextInt64(max.Value);
    [KqlFunction("sin")] public static double? Sin(double? v) => v is null ? null : Math.Sin(v.Value);
    [KqlFunction("cos")] public static double? Cos(double? v) => v is null ? null : Math.Cos(v.Value);
    [KqlFunction("tan")] public static double? Tan(double? v) => v is null ? null : Math.Tan(v.Value);
    [KqlFunction("isnan")] public static bool IsNaN(double? v) => v is { } d && double.IsNaN(d);
    [KqlFunction("isinf")] public static bool IsInf(double? v) => v is { } d && double.IsInfinity(d);
    [KqlFunction("isfinite")] public static bool IsFinite(double? v) => v is { } d && double.IsFinite(d);

    [KqlFunction("round")] public static double? Round(double? v) => Round(v, 0);

    [KqlFunction("round")]
    public static double? Round(double? v, long? digits) =>
        v is null || digits is null ? null : Math.Round(v.Value, (int)digits.Value, MidpointRounding.AwayFromZero);

    [KqlFunction("round")]
    public static decimal? Round(decimal? v, long? digits) =>
        v is null || digits is null ? null : Math.Round(v.Value, (int)digits.Value, MidpointRounding.AwayFromZero);

    [KqlFunction("bin"), KqlFunction("floor")]
    public static long? Bin(long? v, long? size) =>
        v is null || size is null || size == 0 ? null : (long)Math.Floor((double)v.Value / size.Value) * size.Value;

    [KqlFunction("bin"), KqlFunction("floor")]
    public static double? Bin(double? v, double? size) =>
        v is null || size is null || size == 0 ? null : Math.Floor(v.Value / size.Value) * size.Value;

    [KqlFunction("bin"), KqlFunction("floor")]
    public static decimal? Bin(decimal? v, decimal? size) =>
        v is null || size is null || size == 0 ? null : Math.Floor(v.Value / size.Value) * size.Value;

    [KqlFunction("bin"), KqlFunction("floor")]
    public static DateTime? Bin(DateTime? v, TimeSpan? size) =>
        v is null || size is null || size.Value.Ticks == 0 ? null
            : new DateTime((long)Math.Floor((double)v.Value.Ticks / size.Value.Ticks) * size.Value.Ticks, v.Value.Kind);

    [KqlFunction("bin"), KqlFunction("floor")]
    public static TimeSpan? Bin(TimeSpan? v, TimeSpan? size) =>
        v is null || size is null || size.Value.Ticks == 0 ? null
            : TimeSpan.FromTicks((long)Math.Floor((double)v.Value.Ticks / size.Value.Ticks) * size.Value.Ticks);

    [KqlFunction("bin_at")]
    public static long? BinAt(long? v, long? size, long? at) => v is null || at is null ? null : Bin(v - at, size) + at;

    [KqlFunction("bin_at")]
    public static double? BinAt(double? v, double? size, double? at) => v is null || at is null ? null : Bin(v - at, size) + at;

    [KqlFunction("bin_at")]
    public static DateTime? BinAt(DateTime? v, TimeSpan? size, DateTime? at) =>
        v is null || at is null ? null : Bin(new DateTime((v.Value - at.Value).Ticks), size) is { } b ? at.Value.AddTicks(b.Ticks) : null;

    [KqlFunction("floor")] public static double? Floor(double? v) => v is null ? null : Math.Floor(v.Value);

    // ---------------------------------------------------------------- datetime

    [KqlFunction("now")] public static DateTime Now() => DateTime.UtcNow;
    [KqlFunction("now")] public static DateTime? Now(TimeSpan? offset) => offset is null ? null : DateTime.UtcNow + offset.Value;
    [KqlFunction("ago")] public static DateTime? Ago(TimeSpan? span) => span is null ? null : DateTime.UtcNow - span.Value;

    [KqlFunction("startofday")] public static DateTime? StartOfDay(DateTime? d) => d?.Date;
    [KqlFunction("startofday")] public static DateTime? StartOfDay(DateTime? d, long? offset) => d?.Date.AddDays(offset ?? 0);
    [KqlFunction("endofday")] public static DateTime? EndOfDay(DateTime? d) => d?.Date.AddDays(1).AddTicks(-1);
    [KqlFunction("startofweek")] public static DateTime? StartOfWeek(DateTime? d) => d?.Date.AddDays(-(int)d.Value.DayOfWeek);
    [KqlFunction("endofweek")] public static DateTime? EndOfWeek(DateTime? d) => StartOfWeek(d)?.AddDays(7).AddTicks(-1);
    [KqlFunction("startofmonth")] public static DateTime? StartOfMonth(DateTime? d) => d is null ? null : new DateTime(d.Value.Year, d.Value.Month, 1, 0, 0, 0, d.Value.Kind);
    [KqlFunction("endofmonth")] public static DateTime? EndOfMonth(DateTime? d) => StartOfMonth(d)?.AddMonths(1).AddTicks(-1);
    [KqlFunction("startofyear")] public static DateTime? StartOfYear(DateTime? d) => d is null ? null : new DateTime(d.Value.Year, 1, 1, 0, 0, 0, d.Value.Kind);
    [KqlFunction("endofyear")] public static DateTime? EndOfYear(DateTime? d) => StartOfYear(d)?.AddYears(1).AddTicks(-1);

    [KqlFunction("getyear")] public static long? GetYear(DateTime? d) => d?.Year;
    [KqlFunction("getmonth"), KqlFunction("monthofyear")] public static long? GetMonth(DateTime? d) => d?.Month;
    [KqlFunction("dayofmonth")] public static long? DayOfMonth(DateTime? d) => d?.Day;
    [KqlFunction("dayofyear")] public static long? DayOfYear(DateTime? d) => d?.DayOfYear;
    [KqlFunction("hourofday")] public static long? HourOfDay(DateTime? d) => d?.Hour;
    [KqlFunction("dayofweek")] public static TimeSpan? DayOfWeek(DateTime? d) => d is null ? null : TimeSpan.FromDays((int)d.Value.DayOfWeek);
    [KqlFunction("week_of_year")] public static long? WeekOfYear(DateTime? d) => d is null ? null : ISOWeek.GetWeekOfYear(d.Value);
    [KqlFunction("make_datetime")] public static DateTime? MakeDateTime(long? y, long? m, long? d) => y is null || m is null || d is null ? null : new DateTime((int)y, (int)m, (int)d, 0, 0, 0, DateTimeKind.Utc);
    [KqlFunction("make_timespan")] public static TimeSpan? MakeTimeSpan(long? h, long? m) => h is null || m is null ? null : new TimeSpan((int)h, (int)m, 0);
    [KqlFunction("unixtime_seconds_todatetime")] public static DateTime? UnixSecondsToDateTime(double? s) => s is null ? null : DateTime.UnixEpoch.AddSeconds(s.Value);
    [KqlFunction("unixtime_milliseconds_todatetime")] public static DateTime? UnixMillisToDateTime(double? s) => s is null ? null : DateTime.UnixEpoch.AddMilliseconds(s.Value);

    [KqlFunction("datetime_part")]
    public static long? DateTimePart(string? part, DateTime? d)
    {
        if (d is null || part is null) return null;
        var v = d.Value;
        return part.ToLowerInvariant() switch
        {
            "year" => v.Year,
            "quarter" => (v.Month - 1) / 3 + 1,
            "month" => v.Month,
            "week_of_year" or "weekofyear" => ISOWeek.GetWeekOfYear(v),
            "day" => v.Day,
            "dayofyear" => v.DayOfYear,
            "hour" => v.Hour,
            "minute" => v.Minute,
            "second" => v.Second,
            "millisecond" => v.Millisecond,
            "microsecond" => v.Ticks / 10 % 1_000_000,
            "nanosecond" => v.Ticks % 10_000_000 * 100,
            _ => null
        };
    }

    [KqlFunction("datetime_add")]
    public static DateTime? DateTimeAdd(string? part, long? amount, DateTime? d)
    {
        if (d is null || amount is null || part is null) return null;
        var n = amount.Value;
        var v = d.Value;
        return part.ToLowerInvariant() switch
        {
            "year" => v.AddYears((int)n),
            "quarter" => v.AddMonths((int)n * 3),
            "month" => v.AddMonths((int)n),
            "week" => v.AddDays(7 * n),
            "day" => v.AddDays(n),
            "hour" => v.AddHours(n),
            "minute" => v.AddMinutes(n),
            "second" => v.AddSeconds(n),
            "millisecond" => v.AddMilliseconds(n),
            "microsecond" => v.AddTicks(n * 10),
            "nanosecond" => v.AddTicks(n / 100),
            _ => null
        };
    }

    [KqlFunction("datetime_diff")]
    public static long? DateTimeDiff(string? part, DateTime? d1, DateTime? d2)
    {
        if (d1 is null || d2 is null || part is null) return null;
        var a = d1.Value;
        var b = d2.Value;
        return part.ToLowerInvariant() switch
        {
            "year" => a.Year - b.Year,
            "quarter" => (a.Year - b.Year) * 4 + ((a.Month - 1) / 3 - (b.Month - 1) / 3),
            "month" => (a.Year - b.Year) * 12 + a.Month - b.Month,
            "week" => (long)Math.Floor((a.Date.AddDays(-(int)a.DayOfWeek) - b.Date.AddDays(-(int)b.DayOfWeek)).TotalDays / 7),
            "day" => (long)(a.Date - b.Date).TotalDays,
            "hour" => (long)Math.Floor(a.Ticks / (double)TimeSpan.TicksPerHour) - (long)Math.Floor(b.Ticks / (double)TimeSpan.TicksPerHour),
            "minute" => a.Ticks / TimeSpan.TicksPerMinute - b.Ticks / TimeSpan.TicksPerMinute,
            "second" => a.Ticks / TimeSpan.TicksPerSecond - b.Ticks / TimeSpan.TicksPerSecond,
            "millisecond" => a.Ticks / TimeSpan.TicksPerMillisecond - b.Ticks / TimeSpan.TicksPerMillisecond,
            "microsecond" => a.Ticks / 10 - b.Ticks / 10,
            "nanosecond" => (a.Ticks - b.Ticks) * 100,
            _ => null
        };
    }

    [KqlFunction("format_datetime")]
    public static string? FormatDateTime(DateTime? d, string? format) =>
        d is null || format is null ? null : d.Value.ToString(format, CultureInfo.InvariantCulture);

    [KqlFunction("format_timespan")]
    public static string? FormatTimeSpan(TimeSpan? t, string? format) =>
        t is null || format is null ? null : t.Value.ToString(Regex.Replace(format, "([:.\\-/ ])", "\\$1"), CultureInfo.InvariantCulture);

    // ---------------------------------------------------------------- misc

    [KqlFunction("new_guid")] public static Guid NewGuid() => Guid.NewGuid();

    [KqlFunction("hash")]
    public static long? Hash(object? v) => v is null ? null : (long)(uint)KqlComparer.ValueHash(v);

    [KqlFunction("min_of")] public static long? MinOf(params long?[] v) => v.Where(x => x.HasValue).Min();
    [KqlFunction("min_of")] public static double? MinOf(params double?[] v) => v.Where(x => x.HasValue).Min();
    [KqlFunction("min_of")] public static DateTime? MinOf(params DateTime?[] v) => v.Where(x => x.HasValue).Min();
    [KqlFunction("max_of")] public static long? MaxOf(params long?[] v) => v.Where(x => x.HasValue).Max();
    [KqlFunction("max_of")] public static double? MaxOf(params double?[] v) => v.Where(x => x.HasValue).Max();
    [KqlFunction("max_of")] public static DateTime? MaxOf(params DateTime?[] v) => v.Where(x => x.HasValue).Max();

    private static class RegexCache
    {
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Regex> Cache = new();
        public static Regex Get(string pattern) => Cache.GetOrAdd(pattern, p => new Regex(p, RegexOptions.Compiled | RegexOptions.CultureInvariant));
    }
}
