using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PutridParrot.Kql.Runtime;

/// <summary>Conversion helpers implementing KQL conversion semantics (invalid conversions yield null).</summary>
public static class KqlConvert
{
    public static string? ToStringValue(object? value) => value switch
    {
        null => null,
        string s => s,
        bool b => b ? "true" : "false",
        DateTime dt => dt.ToString("yyyy-MM-ddTHH:mm:ss.FFFFFFFZ", CultureInfo.InvariantCulture),
        TimeSpan ts => ts.ToString("c", CultureInfo.InvariantCulture),
        double d => d.ToString("R", CultureInfo.InvariantCulture),
        float f => f.ToString("R", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        JsonValue jv => jv.TryGetValue<string>(out var s) ? s : jv.ToJsonString(),
        JsonNode jn => jn.ToJsonString(),
        JsonElement je => je.ValueKind == JsonValueKind.String ? je.GetString() : je.GetRawText(),
        IDictionary or IList => ToJson(value),
        _ => value.ToString()
    };

    /// <summary>Serialises a dynamic value to JSON text.</summary>
    public static string ToJson(object? value) => JsonSerializer.Serialize(value);

    private static object? Unwrap(object? value) => value switch
    {
        JsonValue jv => jv.TryGetValue<JsonElement>(out var e) ? Unwrap(e) : jv.GetValue<object>(),
        JsonElement { ValueKind: JsonValueKind.String } e => e.GetString(),
        JsonElement { ValueKind: JsonValueKind.Number } e => e.TryGetInt64(out var l) ? (object)l : e.GetDouble(),
        JsonElement { ValueKind: JsonValueKind.True } => true,
        JsonElement { ValueKind: JsonValueKind.False } => false,
        JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => null,
        _ => value
    };

    public static long? ToLong(object? value)
    {
        value = Unwrap(value);
        try
        {
            return value switch
            {
                null => null,
                long l => l,
                bool b => b ? 1 : 0,
                string s => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var r) ? r
                    : double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? (long)d : null,
                DateTime dt => dt.Ticks,
                TimeSpan ts => ts.Ticks,
                IConvertible c => System.Convert.ToInt64(c, CultureInfo.InvariantCulture),
                _ => null
            };
        }
        catch (Exception e) when (e is FormatException or InvalidCastException or OverflowException)
        {
            return null;
        }
    }

    public static int? ToInt(object? value) => ToLong(value) is { } l && l is >= int.MinValue and <= int.MaxValue ? (int)l : null;

    public static double? ToDouble(object? value)
    {
        value = Unwrap(value);
        try
        {
            return value switch
            {
                null => null,
                double d => d,
                bool b => b ? 1 : 0,
                string s => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var r) ? r : null,
                TimeSpan ts => ts.Ticks,
                DateTime dt => dt.Ticks,
                IConvertible c => System.Convert.ToDouble(c, CultureInfo.InvariantCulture),
                _ => null
            };
        }
        catch (Exception e) when (e is FormatException or InvalidCastException or OverflowException)
        {
            return null;
        }
    }

    public static decimal? ToDecimal(object? value)
    {
        value = Unwrap(value);
        try
        {
            return value switch
            {
                null => null,
                decimal d => d,
                string s => decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var r) ? r : null,
                IConvertible c => System.Convert.ToDecimal(c, CultureInfo.InvariantCulture),
                _ => null
            };
        }
        catch (Exception e) when (e is FormatException or InvalidCastException or OverflowException)
        {
            return null;
        }
    }

    public static bool? ToBool(object? value)
    {
        value = Unwrap(value);
        return value switch
        {
            null => null,
            bool b => b,
            string s when bool.TryParse(s, out var r) => r,
            string s when long.TryParse(s, out var l) => l != 0,
            string => null,
            IConvertible c => System.Convert.ToDouble(c, CultureInfo.InvariantCulture) != 0,
            _ => null
        };
    }

    public static DateTime? ToDateTime(object? value)
    {
        value = Unwrap(value);
        return value switch
        {
            null => null,
            DateTime dt => dt,
            DateTimeOffset dto => dto.UtcDateTime,
            string s when DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var r) => DateTime.SpecifyKind(r, DateTimeKind.Utc),
            long l => new DateTime(l, DateTimeKind.Utc),
            _ => null
        };
    }

    public static TimeSpan? ToTimeSpan(object? value)
    {
        value = Unwrap(value);
        return value switch
        {
            null => null,
            TimeSpan ts => ts,
            long l => TimeSpan.FromTicks(l),
            string s when TimeSpan.TryParse(s, CultureInfo.InvariantCulture, out var r) => r,
            _ => null
        };
    }

    public static Guid? ToGuid(object? value)
    {
        value = Unwrap(value);
        return value switch
        {
            null => null,
            Guid g => g,
            string s when Guid.TryParse(s, out var g) => g,
            _ => null
        };
    }

    /// <summary>Converts a value (typically dynamic) to the requested runtime CLR type.</summary>
    public static object? ChangeType(object? value, Type target)
    {
        var core = Nullable.GetUnderlyingType(target) ?? target;
        if (target == typeof(object)) return value;
        if (core == typeof(string)) return ToStringValue(value);
        if (core == typeof(long)) return ToLong(value);
        if (core == typeof(int)) return ToInt(value);
        if (core == typeof(double)) return ToDouble(value);
        if (core == typeof(decimal)) return ToDecimal(value);
        if (core == typeof(bool)) return ToBool(value);
        if (core == typeof(DateTime)) return ToDateTime(value);
        if (core == typeof(TimeSpan)) return ToTimeSpan(value);
        if (core == typeof(Guid)) return ToGuid(value);
        return value is null || target.IsInstanceOfType(value) ? value : System.Convert.ChangeType(value, core, CultureInfo.InvariantCulture);
    }

    /// <summary>Generic form used by compiled expressions: <c>KqlConvert.To&lt;long?&gt;(obj)</c>.</summary>
    public static T To<T>(object? value) => (T)ChangeType(value, typeof(T))!;

    /// <summary>Reads a property from a dynamic value (dictionary, JSON object or POCO).</summary>
    public static object? GetMember(object? value, string name)
    {
        switch (value)
        {
            case null:
                return null;
            case JsonObject jo:
                return jo.TryGetPropertyValue(name, out var n) ? n : null;
            case JsonElement { ValueKind: JsonValueKind.Object } je:
                return je.TryGetProperty(name, out var p) ? p : null;
            case IDictionary<string, object?> d:
                return d.TryGetValue(name, out var v) ? v : null;
            case IDictionary d:
                return d.Contains(name) ? d[name] : null;
            case string s:
                return Dynamic.Parse(s) is var parsed and not string ? GetMember(parsed, name) : null;
        }

        var type = value.GetType();
        if (Schema.KqlTypes.IsScalarClrType(type)) return null;
        return MemberReaders.GetOrAdd((type, name), static key => BuildMemberReader(key.Type, key.Name))?.Invoke(value);
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(Type Type, string Name), Func<object, object?>?> MemberReaders = new();

    private static Func<object, object?>? BuildMemberReader(Type type, string name)
    {
        var prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        MemberInfo? member = prop is not null && prop.GetIndexParameters().Length == 0 && prop.GetMethod is not null
            ? prop
            : type.GetField(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
        if (member is null) return null;
        var p = System.Linq.Expressions.Expression.Parameter(typeof(object), "o");
        var read = System.Linq.Expressions.Expression.MakeMemberAccess(System.Linq.Expressions.Expression.Convert(p, type), member);
        return System.Linq.Expressions.Expression.Lambda<Func<object, object?>>(
            System.Linq.Expressions.Expression.Convert(read, typeof(object)), p).Compile();
    }

    /// <summary>Indexes a dynamic value: by string key (bag) or by integer position (array, negative counts from the end).</summary>
    public static object? GetElement(object? value, object? index)
    {
        if (index is string key) return GetMember(value, key);
        var i = ToLong(index);
        if (i is null) return null;
        switch (value)
        {
            case JsonArray ja:
                return Pick(ja.Count, i.Value) is { } a ? ja[a] : null;
            case JsonElement { ValueKind: JsonValueKind.Array } je:
                return Pick(je.GetArrayLength(), i.Value) is { } b ? je[b] : null;
            case string s:
                return Dynamic.Parse(s) is var parsedArray and not string ? GetElement(parsedArray, index) : null;
            case IList list:
                return Pick(list.Count, i.Value) is { } c ? list[c] : null;
            case IEnumerable e when value is not IDictionary:
                var items = e.Cast<object?>().ToList();
                return Pick(items.Count, i.Value) is { } d ? items[d] : null;
            default:
                return null;
        }

        static int? Pick(int count, long index)
        {
            var idx = index < 0 ? count + index : index;
            return idx >= 0 && idx < count ? (int)idx : null;
        }
    }
}
