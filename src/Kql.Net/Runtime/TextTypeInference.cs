using System.Globalization;

namespace Kql.Net.Runtime;

/// <summary>
/// Infers column types from text values (CSV cells, Redis hash fields...). A column gets the narrowest type every
/// non-empty value parses as, in order: long, real, bool, datetime, timespan, guid, then string.
/// </summary>
public static class TextTypeInference
{
    private static readonly Type[] Candidates = [typeof(long), typeof(double), typeof(bool), typeof(DateTime), typeof(TimeSpan), typeof(Guid)];

    /// <summary>The inferred CLR type for a column (string when nothing narrower fits, or all values are empty).</summary>
    public static Type Infer(IEnumerable<string?> values)
    {
        var remaining = Candidates.ToList();
        var any = false;
        foreach (var v in values)
        {
            if (string.IsNullOrEmpty(v)) continue;
            any = true;
            remaining.RemoveAll(t => Parse(v, t) is null);
            if (remaining.Count == 0) return typeof(string);
        }
        return any && remaining.Count > 0 ? remaining[0] : typeof(string);
    }

    /// <summary>Parses text as <paramref name="type"/>; empty text and failures yield null (strings are returned as-is).</summary>
    public static object? Parse(string? text, Type type)
    {
        if (type == typeof(string)) return text;
        if (string.IsNullOrEmpty(text)) return null;
        var core = Nullable.GetUnderlyingType(type) ?? type;
        if (core == typeof(long)) return long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? l : null;
        if (core == typeof(double)) return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
        if (core == typeof(bool)) return bool.TryParse(text, out var b) ? b : null;
        if (core == typeof(DateTime))
        {
            // require a date-like shape so plain numbers aren't taken as dates
            if (text.Length < 8 || !(text.Contains('-') || text.Contains('/'))) return null;
            return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var dt)
                ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : null;
        }
        if (core == typeof(TimeSpan)) return text.Contains(':') && TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out var ts) ? ts : null;
        if (core == typeof(Guid)) return Guid.TryParse(text, out var g) ? g : null;
        return KqlConvert.ChangeType(text, type);
    }
}
