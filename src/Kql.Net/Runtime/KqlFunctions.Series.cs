namespace Kql.Net.Runtime;

// series_* functions over dynamic arrays (as produced by make-series).
public static partial class KqlFunctions
{
    private static List<object?> Series(object? value) => Dynamic.Elements(value).ToList();

    private static bool IsMissing(object? v, object? placeholder) =>
        v is null || (placeholder is not null && KqlComparer.ValueEquals(Dynamic.Normalize(v), Dynamic.Normalize(placeholder)))
                  || v is double d && double.IsNaN(d);

    [KqlFunction("series_fill_forward")]
    public static object? SeriesFillForward(object? series) => SeriesFillForward(series, null);

    [KqlFunction("series_fill_forward")]
    public static object? SeriesFillForward(object? series, object? missing)
    {
        var s = Series(series);
        object? last = null;
        for (var i = 0; i < s.Count; i++)
        {
            if (IsMissing(s[i], missing)) s[i] = last;
            else last = s[i];
        }
        return s;
    }

    [KqlFunction("series_fill_backward")]
    public static object? SeriesFillBackward(object? series) => SeriesFillBackward(series, null);

    [KqlFunction("series_fill_backward")]
    public static object? SeriesFillBackward(object? series, object? missing)
    {
        var s = Series(series);
        object? next = null;
        for (var i = s.Count - 1; i >= 0; i--)
        {
            if (IsMissing(s[i], missing)) s[i] = next;
            else next = s[i];
        }
        return s;
    }

    [KqlFunction("series_fill_const")]
    public static object? SeriesFillConst(object? series, object? constant) => SeriesFillConst(series, constant, null);

    [KqlFunction("series_fill_const")]
    public static object? SeriesFillConst(object? series, object? constant, object? missing)
    {
        var s = Series(series);
        var c = Dynamic.Normalize(constant);
        for (var i = 0; i < s.Count; i++)
            if (IsMissing(s[i], missing)) s[i] = c;
        return s;
    }

    [KqlFunction("series_fill_linear")]
    public static object? SeriesFillLinear(object? series) => SeriesFillLinear(series, null);

    /// <summary>Linear interpolation between known points; leading/trailing gaps take the nearest known value.</summary>
    [KqlFunction("series_fill_linear")]
    public static object? SeriesFillLinear(object? series, object? missing)
    {
        var s = Series(series);
        var known = Enumerable.Range(0, s.Count).Where(i => !IsMissing(s[i], missing)).ToList();
        if (known.Count == 0) return s;
        var result = new List<object?>(s.Count);
        for (var i = 0; i < s.Count; i++)
        {
            if (!IsMissing(s[i], missing))
            {
                result.Add(s[i]);
                continue;
            }
            var before = known.LastOrDefault(k => k < i, -1);
            var after = known.FirstOrDefault(k => k > i, -1);
            if (before < 0) result.Add(s[after]);
            else if (after < 0) result.Add(s[before]);
            else
            {
                var a = KqlConvert.ToDouble(s[before])!.Value;
                var b = KqlConvert.ToDouble(s[after])!.Value;
                result.Add(a + (b - a) * (i - before) / (after - before));
            }
        }
        return result;
    }

    /// <summary>Statistics of a series as a bag: min, min_idx, max, max_idx, avg, stdev, variance (nulls ignored).</summary>
    [KqlFunction("series_stats_dynamic")]
    public static object? SeriesStatsDynamic(object? series)
    {
        var values = Series(series).Select((v, i) => (Value: KqlConvert.ToDouble(v), Index: i)).Where(x => x.Value.HasValue).ToList();
        if (values.Count == 0) return new Dictionary<string, object?>();
        var min = values.MinBy(x => x.Value!.Value);
        var max = values.MaxBy(x => x.Value!.Value);
        var avg = values.Average(x => x.Value!.Value);
        var variance = values.Count > 1 ? values.Sum(x => Math.Pow(x.Value!.Value - avg, 2)) / (values.Count - 1) : 0;
        return new Dictionary<string, object?>
        {
            ["min"] = min.Value, ["min_idx"] = (long)min.Index,
            ["max"] = max.Value, ["max_idx"] = (long)max.Index,
            ["avg"] = avg, ["stdev"] = Math.Sqrt(variance), ["variance"] = variance
        };
    }

    private static object? Map(object? series, Func<double, double> f) =>
        Series(series).Select(v => KqlConvert.ToDouble(v) is { } d ? (object?)f(d) : null).ToList();

    [KqlFunction("series_abs")] public static object? SeriesAbs(object? s) => Map(s, Math.Abs);
    [KqlFunction("series_acos")] public static object? SeriesAcos(object? s) => Map(s, Math.Acos);
    [KqlFunction("series_asin")] public static object? SeriesAsin(object? s) => Map(s, Math.Asin);
    [KqlFunction("series_atan")] public static object? SeriesAtan(object? s) => Map(s, Math.Atan);
    [KqlFunction("series_ceiling")] public static object? SeriesCeiling(object? s) => Map(s, Math.Ceiling);
    [KqlFunction("series_cos")] public static object? SeriesCos(object? s) => Map(s, Math.Cos);
    [KqlFunction("series_exp")] public static object? SeriesExp(object? s) => Map(s, Math.Exp);
    [KqlFunction("series_floor")] public static object? SeriesFloor(object? s) => Map(s, Math.Floor);
    [KqlFunction("series_log")] public static object? SeriesLog(object? s) => Map(s, Math.Log);
    [KqlFunction("series_sign")] public static object? SeriesSign(object? s) => Map(s, x => Math.Sign(x));
    [KqlFunction("series_sin")] public static object? SeriesSin(object? s) => Map(s, Math.Sin);
    [KqlFunction("series_tan")] public static object? SeriesTan(object? s) => Map(s, Math.Tan);
    [KqlFunction("series_pow")] public static object? SeriesPow(object? a, object? b) => Elementwise(a, b, Math.Pow);
    [KqlFunction("series_equals")] public static object? SeriesEquals(object? a, object? b) => Compare(a, b, (x, y) => x == y);
    [KqlFunction("series_not_equals")] public static object? SeriesNotEquals(object? a, object? b) => Compare(a, b, (x, y) => x != y);
    [KqlFunction("series_greater_equals")] public static object? SeriesGreaterEquals(object? a, object? b) => Compare(a, b, (x, y) => x >= y);
    [KqlFunction("series_less_equals")] public static object? SeriesLessEquals(object? a, object? b) => Compare(a, b, (x, y) => x <= y);

    private static double[] Numbers(object? s) => Series(s).Select(v => KqlConvert.ToDouble(v)).Where(v => v.HasValue).Select(v => v!.Value).ToArray();

    [KqlFunction("series_sum")] public static double? SeriesSum(object? s) => Numbers(s).Sum();
    [KqlFunction("series_product")] public static double? SeriesProduct(object? s) => Numbers(s).Aggregate(1.0, (a, b) => a * b);
    [KqlFunction("series_magnitude")] public static double? SeriesMagnitude(object? s) => Math.Sqrt(Numbers(s).Sum(x => x * x));

    [KqlFunction("series_dot_product")]
    public static double? SeriesDotProduct(object? a, object? b)
    {
        var x = Series(a);
        var y = Series(b);
        double sum = 0;
        for (var i = 0; i < Math.Min(x.Count, y.Count); i++)
            if (KqlConvert.ToDouble(x[i]) is { } p && KqlConvert.ToDouble(y[i]) is { } q) sum += p * q;
        return sum;
    }

    [KqlFunction("series_cosine_similarity")]
    public static double? SeriesCosineSimilarity(object? a, object? b)
    {
        var den = SeriesMagnitude(a) * SeriesMagnitude(b);
        return den is null or 0 ? null : SeriesDotProduct(a, b) / den;
    }

    [KqlFunction("series_pearson_correlation")]
    public static double? SeriesPearsonCorrelation(object? a, object? b)
    {
        var x = Numbers(a);
        var y = Numbers(b);
        var n = Math.Min(x.Length, y.Length);
        if (n < 2) return null;
        var mx = x.Take(n).Average();
        var my = y.Take(n).Average();
        double sxy = 0, sxx = 0, syy = 0;
        for (var i = 0; i < n; i++)
        {
            sxy += (x[i] - mx) * (y[i] - my);
            sxx += (x[i] - mx) * (x[i] - mx);
            syy += (y[i] - my) * (y[i] - my);
        }
        return sxx == 0 || syy == 0 ? null : sxy / Math.Sqrt(sxx * syy);
    }

    /// <summary>Least-squares line over the series (x = index): rsquare, slope, variance, rvariance, interception, line_fit.</summary>
    [KqlFunction("series_fit_line_dynamic")]
    public static object? SeriesFitLineDynamic(object? series)
    {
        var y = Series(series).Select(v => KqlConvert.ToDouble(v) ?? double.NaN).ToArray();
        var n = y.Length;
        if (n == 0) return new Dictionary<string, object?>();
        double mx = (n - 1) / 2.0, my = y.Where(v => !double.IsNaN(v)).DefaultIfEmpty(0).Average();
        double sxy = 0, sxx = 0;
        for (var i = 0; i < n; i++)
        {
            if (double.IsNaN(y[i])) continue;
            sxy += (i - mx) * (y[i] - my);
            sxx += (i - mx) * (i - mx);
        }
        var slope = sxx == 0 ? 0 : sxy / sxx;
        var intercept = my - slope * mx;
        var fit = Enumerable.Range(0, n).Select(i => intercept + slope * i).ToArray();
        var variance = y.Where(v => !double.IsNaN(v)).Select(v => (v - my) * (v - my)).DefaultIfEmpty(0).Average();
        var rvariance = Enumerable.Range(0, n).Where(i => !double.IsNaN(y[i])).Select(i => (y[i] - fit[i]) * (y[i] - fit[i])).DefaultIfEmpty(0).Average();
        return new Dictionary<string, object?>
        {
            ["rsquare"] = variance == 0 ? 1.0 : 1 - rvariance / variance,
            ["slope"] = slope,
            ["variance"] = variance,
            ["rvariance"] = rvariance,
            ["interception"] = intercept,
            ["line_fit"] = fit.Select(v => (object?)v).ToList()
        };
    }

    /// <summary>Tukey-fence outlier scores: 0 inside [Q1 - 1.5·IQR, Q3 + 1.5·IQR], otherwise the distance in IQRs.</summary>
    [KqlFunction("series_outliers")]
    public static object? SeriesOutliers(object? series)
    {
        var values = Series(series).Select(KqlConvert.ToDouble).ToList();
        var sorted = values.Where(v => v.HasValue).Select(v => v!.Value).Order().ToArray();
        if (sorted.Length == 0) return values.Select(_ => (object?)0.0).ToList();
        double Q(double p) => sorted[(int)Math.Clamp(Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1)];
        var q1 = Q(0.25);
        var q3 = Q(0.75);
        var iqr = q3 - q1;
        return values.Select(v => (object?)(v is null || iqr == 0 ? 0.0
            : v < q1 - 1.5 * iqr ? (v.Value - q1) / iqr
            : v > q3 + 1.5 * iqr ? (v.Value - q3) / iqr
            : 0.0)).ToList();
    }

    [KqlFunction("series_add")] public static object? SeriesAdd(object? a, object? b) => Elementwise(a, b, (x, y) => x + y);
    [KqlFunction("series_subtract")] public static object? SeriesSubtract(object? a, object? b) => Elementwise(a, b, (x, y) => x - y);
    [KqlFunction("series_multiply")] public static object? SeriesMultiply(object? a, object? b) => Elementwise(a, b, (x, y) => x * y);
    [KqlFunction("series_divide")] public static object? SeriesDivide(object? a, object? b) => Elementwise(a, b, (x, y) => y == 0 ? double.NaN : x / y);
    [KqlFunction("series_greater")] public static object? SeriesGreater(object? a, object? b) => Compare(a, b, (x, y) => x > y);
    [KqlFunction("series_less")] public static object? SeriesLess(object? a, object? b) => Compare(a, b, (x, y) => x < y);

    /// <summary>Element-wise arithmetic; either argument may be a scalar (broadcast). Result length is the longer series.</summary>
    private static object? Elementwise(object? a, object? b, Func<double, double, double> op)
    {
        var (sa, sb, n) = Pair(a, b);
        var result = new List<object?>(n);
        for (var i = 0; i < n; i++)
        {
            var x = KqlConvert.ToDouble(sa(i));
            var y = KqlConvert.ToDouble(sb(i));
            result.Add(x is null || y is null ? null : op(x.Value, y.Value));
        }
        return result;
    }

    private static object? Compare(object? a, object? b, Func<double, double, bool> op)
    {
        var (sa, sb, n) = Pair(a, b);
        var result = new List<object?>(n);
        for (var i = 0; i < n; i++)
        {
            var x = KqlConvert.ToDouble(sa(i));
            var y = KqlConvert.ToDouble(sb(i));
            result.Add(x is null || y is null ? null : op(x.Value, y.Value));
        }
        return result;
    }

    private static (Func<int, object?> A, Func<int, object?> B, int Length) Pair(object? a, object? b)
    {
        var na = Dynamic.Normalize(a);
        var nb = Dynamic.Normalize(b);
        Func<int, object?> Accessor(object? v) => v is List<object?> l ? i => i < l.Count ? l[i] : null : _ => v;
        var length = Math.Max(na is List<object?> la ? la.Count : 0, nb is List<object?> lb ? lb.Count : 0);
        return (Accessor(na), Accessor(nb), length);
    }

    /// <summary>
    /// Finite impulse response filter (e.g. a moving average with <c>series_fir(s, dynamic([1,1,1]), true, true)</c>).
    /// Values outside the series count as 0. Optionally normalises the filter to sum to 1 and centres it on each point.
    /// </summary>
    [KqlFunction("series_fir")]
    public static object? SeriesFir(object? series, object? filter) => SeriesFir(series, filter, true, false);

    [KqlFunction("series_fir")]
    public static object? SeriesFir(object? series, object? filter, bool? normalize) => SeriesFir(series, filter, normalize, false);

    [KqlFunction("series_fir")]
    public static object? SeriesFir(object? series, object? filter, bool? normalize, bool? center)
    {
        var s = Series(series).Select(v => KqlConvert.ToDouble(v) ?? 0).ToArray();
        var f = Series(filter).Select(v => KqlConvert.ToDouble(v) ?? 0).ToArray();
        if (f.Length == 0) return s.Cast<object?>().ToList();
        if (normalize != false)
        {
            var sum = f.Sum();
            if (sum != 0) f = f.Select(x => x / sum).ToArray();
        }
        var shift = center == true ? f.Length / 2 : 0;
        var result = new List<object?>(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            double acc = 0;
            for (var j = 0; j < f.Length; j++)
            {
                var k = i - j + shift;
                if (k >= 0 && k < s.Length) acc += f[j] * s[k];
            }
            result.Add(acc);
        }
        return result;
    }
}
