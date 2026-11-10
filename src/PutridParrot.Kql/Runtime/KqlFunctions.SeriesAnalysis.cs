namespace PutridParrot.Kql.Runtime;

// Series analysis functions: decomposition, anomalies, forecasting, periods, FFT, filters and fits.
// Functions that KQL declares as returning several columns (e.g. series_decompose) return an object?[] with one
// element per column; extend/project unpack it into the columns the KQL parser names.
public static partial class KqlFunctions
{
    /// <summary>The series as doubles; nulls (and non-numeric values) become NaN.</summary>
    private static double[] Doubles(object? series) =>
        Series(series).Select(v => KqlConvert.ToDouble(v) ?? double.NaN).ToArray();

    private static List<object?> ToList(IEnumerable<double> values) =>
        values.Select(v => double.IsNaN(v) ? null : (object?)v).ToList();

    private static double Median(List<double> values)
    {
        if (values.Count == 0) return double.NaN;
        values.Sort();
        var mid = values.Count / 2;
        return values.Count % 2 == 1 ? values[mid] : (values[mid - 1] + values[mid]) / 2;
    }

    // ------------------------------------------------------------------ series_stats

    /// <summary>series_stats: (min, min_idx, max, max_idx, avg, stdev, variance) as separate columns.</summary>
    [KqlFunction("series_stats")]
    public static object?[] SeriesStats(object? series) => SeriesStats(series, false);

    [KqlFunction("series_stats")]
    public static object?[] SeriesStats(object? series, bool? ignoreNonFinite)
    {
        var values = Doubles(series);
        if (ignoreNonFinite != true && values.Any(v => !double.IsFinite(v)))
            return [double.NaN, null, double.NaN, null, double.NaN, double.NaN, double.NaN];
        var stats = (Dictionary<string, object?>)SeriesStatsDynamic(series)!;
        return stats.Count == 0
            ? [null, null, null, null, null, null, null]
            : [stats["min"], stats["min_idx"], stats["max"], stats["max_idx"], stats["avg"], stats["stdev"], stats["variance"]];
    }

    [KqlFunction("series_stats_dynamic")]
    public static object? SeriesStatsDynamic(object? series, bool? ignoreNonFinite)
    {
        var values = SeriesStats(series, ignoreNonFinite);
        string[] keys = ["min", "min_idx", "max", "max_idx", "avg", "stdev", "variance"];
        return keys.Select((k, i) => (k, values[i])).ToDictionary(x => x.k, x => x.Item2);
    }

    // ------------------------------------------------------------------ line and polynomial fits

    /// <summary>series_fit_line: (rsquare, slope, variance, rvariance, interception, line_fit).</summary>
    [KqlFunction("series_fit_line")]
    public static object?[] SeriesFitLine(object? series)
    {
        var fit = (Dictionary<string, object?>)SeriesFitLineDynamic(series)!;
        return fit.Count == 0
            ? [null, null, null, null, null, new List<object?>()]
            : [fit["rsquare"], fit["slope"], fit["variance"], fit["rvariance"], fit["interception"], fit["line_fit"]];
    }

    private readonly record struct LineFit(double Slope, double Intercept, double Variance, double RVariance)
    {
        public double RSquare => Variance == 0 ? 1.0 : 1 - RVariance / Variance;
        public double At(double x) => Intercept + Slope * x;
    }

    /// <summary>Least squares over the points [from, to) of <paramref name="y"/> with x = index (NaN values skipped).</summary>
    private static LineFit FitLine(double[] y, int from, int to)
    {
        double n = 0, sx = 0, sy = 0;
        for (var i = from; i < to; i++)
        {
            if (double.IsNaN(y[i])) continue;
            n++;
            sx += i;
            sy += y[i];
        }
        if (n == 0) return new LineFit(0, 0, 0, 0);
        double mx = sx / n, my = sy / n, sxy = 0, sxx = 0, syy = 0;
        for (var i = from; i < to; i++)
        {
            if (double.IsNaN(y[i])) continue;
            sxy += (i - mx) * (y[i] - my);
            sxx += (i - mx) * (i - mx);
            syy += (y[i] - my) * (y[i] - my);
        }
        var slope = sxx == 0 ? 0 : sxy / sxx;
        var intercept = my - slope * mx;
        double rss = 0;
        for (var i = from; i < to; i++)
            if (!double.IsNaN(y[i])) rss += Math.Pow(y[i] - (intercept + slope * i), 2);
        return new LineFit(slope, intercept, syy / n, rss / n);
    }

    private sealed record TwoLines(int Split, LineFit Left, LineFit Right, double Variance, double RVariance, double[] Fit)
    {
        public double RSquare => Variance == 0 ? 1.0 : 1 - RVariance / Variance;
    }

    /// <summary>The split into two least-squares lines (left: [0, split), right: [split, n)) with the least residual.</summary>
    private static TwoLines? FitTwoLines(object? series)
    {
        var y = Doubles(series);
        var n = y.Length;
        if (n < 4) return null;
        var whole = FitLine(y, 0, n);
        TwoLines? best = null;
        for (var split = 2; split <= n - 2; split++)
        {
            var left = FitLine(y, 0, split);
            var right = FitLine(y, split, n);
            var fit = Enumerable.Range(0, n).Select(i => i < split ? left.At(i) : right.At(i)).ToArray();
            var known = Enumerable.Range(0, n).Where(i => !double.IsNaN(y[i])).ToList();
            var rvariance = known.Count == 0 ? 0 : known.Sum(i => Math.Pow(y[i] - fit[i], 2)) / known.Count;
            if (best is null || rvariance < best.RVariance)
                best = new TwoLines(split, left, right, whole.Variance, rvariance, fit);
        }
        return best;
    }

    /// <summary>series_fit_2lines: the best two-segment linear fit, as 15 columns.</summary>
    [KqlFunction("series_fit_2lines")]
    public static object?[] SeriesFitTwoLines(object? series)
    {
        if (FitTwoLines(series) is not { } t) return new object?[15];
        return
        [
            t.RSquare, (long)t.Split, t.Variance, t.RVariance, ToList(t.Fit),
            t.Right.RSquare, t.Right.Slope, t.Right.Intercept, t.Right.Variance, t.Right.RVariance,
            t.Left.RSquare, t.Left.Slope, t.Left.Intercept, t.Left.Variance, t.Left.RVariance
        ];
    }

    [KqlFunction("series_fit_2lines_dynamic")]
    public static object? SeriesFitTwoLinesDynamic(object? series)
    {
        if (FitTwoLines(series) is not { } t) return new Dictionary<string, object?>();
        Dictionary<string, object?> Side(LineFit f) => new()
        {
            ["rsquare"] = f.RSquare, ["slope"] = f.Slope, ["interception"] = f.Intercept,
            ["variance"] = f.Variance, ["rvariance"] = f.RVariance
        };
        return new Dictionary<string, object?>
        {
            ["rsquare"] = t.RSquare, ["split_idx"] = (long)t.Split, ["variance"] = t.Variance, ["rvariance"] = t.RVariance,
            ["line_fit"] = ToList(t.Fit), ["right"] = Side(t.Right), ["left"] = Side(t.Left)
        };
    }

    [KqlFunction("series_fit_poly")]
    public static object?[] SeriesFitPoly(object? y) => SeriesFitPoly(y, null, 1);

    [KqlFunction("series_fit_poly")]
    public static object?[] SeriesFitPoly(object? y, object? x) => SeriesFitPoly(y, x, 1);

    /// <summary>
    /// series_fit_poly: least-squares polynomial of <paramref name="degree"/> (1-6), as (rsquare, coefficients, variance,
    /// rvariance, poly_fit). Coefficients run from the highest power down. Without <paramref name="x"/>, x = 1, 2, …, n.
    /// </summary>
    [KqlFunction("series_fit_poly")]
    public static object?[] SeriesFitPoly(object? y, object? x, long? degree)
    {
        var ys = Doubles(y);
        var n = ys.Length;
        var xs = x is null || Series(x).Count == 0 ? Enumerable.Range(1, n).Select(i => (double)i).ToArray() : Doubles(x);
        var d = (int)Math.Clamp(degree ?? 1, 1, 6);
        var points = Enumerable.Range(0, Math.Min(n, xs.Length)).Where(i => !double.IsNaN(ys[i]) && !double.IsNaN(xs[i])).ToList();
        if (points.Count == 0) return [null, new List<object?>(), null, null, new List<object?>()];

        // normal equations: (XᵀX) c = Xᵀy, with c[k] the coefficient of x^k
        var m = d + 1;
        var a = new double[m, m + 1];
        foreach (var i in points)
        {
            var powers = Enumerable.Range(0, 2 * m).Select(k => Math.Pow(xs[i], k)).ToArray();
            for (var r = 0; r < m; r++)
            {
                for (var c = 0; c < m; c++) a[r, c] += powers[r + c];
                a[r, m] += powers[r] * ys[i];
            }
        }
        var coefficients = SolveLinear(a, m);
        double Eval(double v) => Enumerable.Range(0, m).Sum(k => coefficients[k] * Math.Pow(v, k));

        var mean = points.Average(i => ys[i]);
        var variance = points.Sum(i => Math.Pow(ys[i] - mean, 2)) / points.Count;
        var rvariance = points.Sum(i => Math.Pow(ys[i] - Eval(xs[i]), 2)) / points.Count;
        var fit = Enumerable.Range(0, n).Select(i => i < xs.Length && !double.IsNaN(xs[i]) ? Eval(xs[i]) : double.NaN);
        return
        [
            variance == 0 ? 1.0 : 1 - rvariance / variance,
            coefficients.Reverse().Select(c => (object?)c).ToList(),
            variance, rvariance, ToList(fit)
        ];
    }

    /// <summary>Gaussian elimination with partial pivoting on an augmented m×(m+1) matrix.</summary>
    private static double[] SolveLinear(double[,] a, int m)
    {
        for (var col = 0; col < m; col++)
        {
            var pivot = Enumerable.Range(col, m - col).MaxBy(r => Math.Abs(a[r, col]));
            if (pivot != col)
                for (var c = 0; c <= m; c++) (a[col, c], a[pivot, c]) = (a[pivot, c], a[col, c]);
            if (Math.Abs(a[col, col]) < 1e-12) continue;
            for (var r = 0; r < m; r++)
            {
                if (r == col) continue;
                var f = a[r, col] / a[col, col];
                for (var c = col; c <= m; c++) a[r, c] -= f * a[col, c];
            }
        }
        return Enumerable.Range(0, m).Select(r => Math.Abs(a[r, r]) < 1e-12 ? 0 : a[r, m] / a[r, r]).ToArray();
    }

    // ------------------------------------------------------------------ filters

    /// <summary>
    /// Infinite impulse response filter: y[i] = (Σ b[k]·x[i-k] − Σ_{k≥1} a[k]·y[i-k]) / a[0].
    /// For example <c>series_iir(s, dynamic([1]), dynamic([1,-1]))</c> is a cumulative sum.
    /// </summary>
    [KqlFunction("series_iir")]
    public static object? SeriesIir(object? series, object? numerators, object? denominators)
    {
        var x = Series(series).Select(v => KqlConvert.ToDouble(v) ?? 0).ToArray();
        var b = Doubles(numerators);
        var a = Doubles(denominators);
        if (a.Length == 0 || a[0] == 0) return x.Select(_ => (object?)null).ToList();
        var y = new double[x.Length];
        for (var i = 0; i < x.Length; i++)
        {
            double acc = 0;
            for (var k = 0; k < b.Length && k <= i; k++) acc += b[k] * x[i - k];
            for (var k = 1; k < a.Length && k <= i; k++) acc -= a[k] * y[i - k];
            y[i] = acc / a[0];
        }
        return y.Select(v => (object?)v).ToList();
    }

    // ------------------------------------------------------------------ Fourier transforms

    [KqlFunction("series_fft")]
    public static object?[] SeriesFft(object? real) => SeriesFft(real, null);

    /// <summary>Discrete Fourier transform (unnormalised): (real, imag).</summary>
    [KqlFunction("series_fft")]
    public static object?[] SeriesFft(object? real, object? imaginary) => Fourier(real, imaginary, inverse: false);

    [KqlFunction("series_ifft")]
    public static object?[] SeriesIfft(object? real) => SeriesIfft(real, null);

    /// <summary>Inverse discrete Fourier transform (scaled by 1/n): (real, imag).</summary>
    [KqlFunction("series_ifft")]
    public static object?[] SeriesIfft(object? real, object? imaginary) => Fourier(real, imaginary, inverse: true);

    private static object?[] Fourier(object? real, object? imaginary, bool inverse)
    {
        var re = Series(real).Select(v => KqlConvert.ToDouble(v) ?? 0).ToArray();
        var imIn = imaginary is null ? [] : Series(imaginary).Select(v => KqlConvert.ToDouble(v) ?? 0).ToArray();
        var n = re.Length;
        var im = Enumerable.Range(0, n).Select(i => i < imIn.Length ? imIn[i] : 0).ToArray();
        var outRe = new double[n];
        var outIm = new double[n];
        var sign = inverse ? 1 : -1;
        for (var k = 0; k < n; k++)
        {
            double sr = 0, si = 0;
            for (var t = 0; t < n; t++)
            {
                var angle = sign * 2 * Math.PI * ((long)k * t % n) / n;
                var (sin, cos) = Math.SinCos(angle);
                sr += re[t] * cos - im[t] * sin;
                si += re[t] * sin + im[t] * cos;
            }
            outRe[k] = inverse ? sr / n : sr;
            outIm[k] = inverse ? si / n : si;
        }
        return [outRe.Select(v => (object?)v).ToList(), outIm.Select(v => (object?)v).ToList()];
    }

    // ------------------------------------------------------------------ periods

    /// <summary>
    /// How strongly the series repeats every <paramref name="period"/> points: the correlation between the series and
    /// itself shifted by the period (after removing a linear trend), clamped to [0, 1].
    /// </summary>
    private static double PeriodScore(double[] detrended, double period)
    {
        var lag = (int)Math.Round(period);
        var n = detrended.Length;
        if (lag < 1 || lag > n / 2) return 0;
        var pairs = Enumerable.Range(0, n - lag)
            .Where(i => !double.IsNaN(detrended[i]) && !double.IsNaN(detrended[i + lag]))
            .Select(i => (detrended[i], detrended[i + lag])).ToList();
        if (pairs.Count < 2) return 0;
        var ma = pairs.Average(p => p.Item1);
        var mb = pairs.Average(p => p.Item2);
        double sab = 0, saa = 0, sbb = 0;
        foreach (var (a, b) in pairs)
        {
            sab += (a - ma) * (b - mb);
            saa += (a - ma) * (a - ma);
            sbb += (b - mb) * (b - mb);
        }
        return saa == 0 || sbb == 0 ? 0 : Math.Clamp(sab / Math.Sqrt(saa * sbb), 0, 1);
    }

    /// <summary>
    /// The series prepared for period scoring: a linear trend removed, then values replaced by their ranks (ties share
    /// the average rank) so a few extreme points (the anomalies being looked for) can't drown out the pattern.
    /// </summary>
    private static double[] Detrend(double[] y)
    {
        var line = FitLine(y, 0, y.Length);
        var detrended = y.Select((v, i) => double.IsNaN(v) ? v : v - line.At(i)).ToArray();
        var order = Enumerable.Range(0, y.Length).Where(i => !double.IsNaN(detrended[i])).OrderBy(i => detrended[i]).ToArray();
        var ranks = Enumerable.Repeat(double.NaN, y.Length).ToArray();
        for (var start = 0; start < order.Length;)
        {
            var end = start;
            while (end + 1 < order.Length && Math.Abs(detrended[order[end + 1]] - detrended[order[start]]) < 1e-9) end++;
            for (var k = start; k <= end; k++) ranks[order[k]] = (start + end) / 2.0;
            start = end + 1;
        }
        return ranks;
    }

    /// <summary>
    /// The strongest periods in [<paramref name="min"/>, <paramref name="max"/>]: lags where the autocorrelation peaks,
    /// best first, leaving out multiples of a period already found.
    /// </summary>
    private static List<(double Period, double Score)> DetectPeriods(double[] y, double min, double max, int count)
    {
        var detrended = Detrend(y);
        var lo = Math.Max(2, (int)Math.Ceiling(min));
        var hi = Math.Min(y.Length / 2, max <= 0 ? y.Length / 2 : (int)Math.Floor(max));
        if (hi < lo) return [];
        var scores = new double[hi + 2];
        for (var p = Math.Max(1, lo - 1); p <= Math.Min(hi + 1, y.Length / 2); p++) scores[p] = PeriodScore(detrended, p);

        var peaks = Enumerable.Range(lo, hi - lo + 1)
            .Where(p => scores[p] > 0 && scores[p] >= scores[p - 1] && (p + 1 > y.Length / 2 || scores[p] >= scores[p + 1]))
            .ToList();

        // A multiple of a period repeats too; keep the shorter period unless the multiple scores clearly better.
        return peaks
            .Where(p => !peaks.Any(q => q < p && p % q == 0 && scores[q] >= 0.9 * scores[p]))
            .OrderByDescending(p => scores[p]).ThenBy(p => p)
            .Take(count)
            .Select(p => ((double)p, scores[p]))
            .ToList();
    }

    /// <summary>series_periods_detect(series, min_period, max_period, num_periods): (periods, scores).</summary>
    [KqlFunction("series_periods_detect")]
    public static object?[] SeriesPeriodsDetect(object? series, double? minPeriod, double? maxPeriod, long? numPeriods)
    {
        var found = DetectPeriods(Doubles(series), minPeriod ?? 0, maxPeriod ?? 0, (int)Math.Max(1, numPeriods ?? 1));
        return [found.Select(f => (object?)f.Period).ToList(), found.Select(f => (object?)f.Score).ToList()];
    }

    /// <summary>series_periods_validate(series, period1, …): the score (0-1) of each given period, as (periods, scores).</summary>
    [KqlFunction("series_periods_validate")]
    public static object?[] SeriesPeriodsValidate(object? series, params double?[] periods)
    {
        var detrended = Detrend(Doubles(series));
        var valid = periods.Where(p => p.HasValue).Select(p => p!.Value).ToList();
        return [valid.Select(p => (object?)p).ToList(), valid.Select(p => (object?)PeriodScore(detrended, p)).ToList()];
    }

    // ------------------------------------------------------------------ decomposition

    /// <summary>The period to use: a positive value as given, 0 for none, negative to auto-detect (score above the threshold).</summary>
    private static int ResolvePeriod(double[] training, long? seasonality, double threshold)
    {
        var s = seasonality ?? -1;
        if (s > 0) return (int)s;
        if (s == 0) return 0;
        var found = DetectPeriods(training, 0, 0, 1);
        return found.Count == 1 && found[0].Score > threshold ? (int)found[0].Period : 0;
    }

    private sealed record Decomposition(double[] Seasonal, double[] Trend, double[] Baseline, double[] Residual);

    /// <summary>
    /// Splits the series into seasonal (median of each position within the period), trend (avg, linefit or none, on
    /// the deseasoned series) and residual. The last <paramref name="testPoints"/> points are not used for learning;
    /// the seasonal and trend components are extrapolated over them.
    /// </summary>
    private static Decomposition Decompose(object? series, long? seasonality, string? trend, long? testPoints, double? threshold)
    {
        var y = Doubles(series);
        var n = y.Length;
        var learn = (int)Math.Clamp(n - (testPoints ?? 0), 0, n);
        var training = y.Take(learn).ToArray();
        var period = ResolvePeriod(training, seasonality, threshold ?? 0.6);

        var trendKind = (trend ?? "avg").ToLowerInvariant();

        // The seasonal pattern is estimated around a rough trend (the line or the mean), so the trend's own slope or
        // level doesn't leak into it; with trend 'none' it is taken from the raw values.
        var seasonal = new double[n];
        if (period > 0)
        {
            var mean = training.Where(v => !double.IsNaN(v)).DefaultIfEmpty(0).Average();
            Func<int, double> rough = trendKind switch
            {
                "none" => _ => 0,
                "linefit" => FitLine(training, 0, learn) is var l ? i => l.At(i) : _ => 0,
                _ => _ => mean
            };
            var medians = Enumerable.Range(0, period)
                .Select(k => Median(Enumerable.Range(0, learn).Where(i => i % period == k && !double.IsNaN(y[i])).Select(i => y[i] - rough(i)).ToList()))
                .Select(m => double.IsNaN(m) ? 0 : m).ToArray();
            for (var i = 0; i < n; i++) seasonal[i] = medians[i % period];
        }

        var deseasoned = training.Select((v, i) => v - seasonal[i]).ToArray();
        var trendValues = new double[n];
        switch (trendKind)
        {
            case "linefit":
            {
                var line = FitLine(deseasoned, 0, learn);
                for (var i = 0; i < n; i++) trendValues[i] = line.At(i);
                break;
            }
            case "none":
                break;
            default:
            {
                var known = deseasoned.Where(v => !double.IsNaN(v)).ToList();
                var avg = known.Count == 0 ? 0 : known.Average();
                Array.Fill(trendValues, avg);
                break;
            }
        }

        var baseline = Enumerable.Range(0, n).Select(i => seasonal[i] + trendValues[i]).ToArray();
        var residual = Enumerable.Range(0, n).Select(i => y[i] - baseline[i]).ToArray();
        return new Decomposition(seasonal, trendValues, baseline, residual);
    }

    private static object?[] Components(Decomposition d) =>
        [ToList(d.Baseline), ToList(d.Seasonal), ToList(d.Trend), ToList(d.Residual)];

    [KqlFunction("series_decompose")]
    public static object?[] SeriesDecompose(object? series) => SeriesDecompose(series, -1, "avg", 0, 0.6);

    [KqlFunction("series_decompose")]
    public static object?[] SeriesDecompose(object? series, long? seasonality) => SeriesDecompose(series, seasonality, "avg", 0, 0.6);

    [KqlFunction("series_decompose")]
    public static object?[] SeriesDecompose(object? series, long? seasonality, string? trend) => SeriesDecompose(series, seasonality, trend, 0, 0.6);

    [KqlFunction("series_decompose")]
    public static object?[] SeriesDecompose(object? series, long? seasonality, string? trend, long? testPoints) =>
        SeriesDecompose(series, seasonality, trend, testPoints, 0.6);

    /// <summary>series_decompose: (baseline, seasonal, trend, residual).</summary>
    [KqlFunction("series_decompose")]
    public static object?[] SeriesDecompose(object? series, long? seasonality, string? trend, long? testPoints, double? seasonalityThreshold) =>
        Components(Decompose(series, seasonality, trend, testPoints, seasonalityThreshold));

    [KqlFunction("series_decompose_forecast")]
    public static object?[] SeriesDecomposeForecast(object? series, long? points) =>
        SeriesDecomposeForecast(series, points, -1, "linefit", 0.6);

    [KqlFunction("series_decompose_forecast")]
    public static object?[] SeriesDecomposeForecast(object? series, long? points, long? seasonality) =>
        SeriesDecomposeForecast(series, points, seasonality, "linefit", 0.6);

    [KqlFunction("series_decompose_forecast")]
    public static object?[] SeriesDecomposeForecast(object? series, long? points, long? seasonality, string? trend) =>
        SeriesDecomposeForecast(series, points, seasonality, trend, 0.6);

    /// <summary>
    /// series_decompose_forecast: learns from all but the last <paramref name="points"/> points and extrapolates over
    /// them. The baseline column is the forecast; the result is (baseline, seasonal, trend, residual).
    /// </summary>
    [KqlFunction("series_decompose_forecast")]
    public static object?[] SeriesDecomposeForecast(object? series, long? points, long? seasonality, string? trend, double? seasonalityThreshold) =>
        Components(Decompose(series, seasonality, trend ?? "linefit", points, seasonalityThreshold));

    [KqlFunction("series_decompose_anomalies")]
    public static object?[] SeriesDecomposeAnomalies(object? series) => SeriesDecomposeAnomalies(series, 1.5, -1, "avg", 0, "ctukey", 0.6);

    [KqlFunction("series_decompose_anomalies")]
    public static object?[] SeriesDecomposeAnomalies(object? series, double? threshold) =>
        SeriesDecomposeAnomalies(series, threshold, -1, "avg", 0, "ctukey", 0.6);

    [KqlFunction("series_decompose_anomalies")]
    public static object?[] SeriesDecomposeAnomalies(object? series, double? threshold, long? seasonality) =>
        SeriesDecomposeAnomalies(series, threshold, seasonality, "avg", 0, "ctukey", 0.6);

    [KqlFunction("series_decompose_anomalies")]
    public static object?[] SeriesDecomposeAnomalies(object? series, double? threshold, long? seasonality, string? trend) =>
        SeriesDecomposeAnomalies(series, threshold, seasonality, trend, 0, "ctukey", 0.6);

    [KqlFunction("series_decompose_anomalies")]
    public static object?[] SeriesDecomposeAnomalies(object? series, double? threshold, long? seasonality, string? trend, long? testPoints) =>
        SeriesDecomposeAnomalies(series, threshold, seasonality, trend, testPoints, "ctukey", 0.6);

    [KqlFunction("series_decompose_anomalies")]
    public static object?[] SeriesDecomposeAnomalies(object? series, double? threshold, long? seasonality, string? trend, long? testPoints,
        string? method) => SeriesDecomposeAnomalies(series, threshold, seasonality, trend, testPoints, method, 0.6);

    /// <summary>
    /// series_decompose_anomalies: decomposes the series and scores the residual with Tukey's fences ('ctukey' uses the
    /// 10th/90th percentiles, 'tukey' the 25th/75th). Returns (ad_flag: +1/-1/0, ad_score, baseline).
    /// </summary>
    [KqlFunction("series_decompose_anomalies")]
    public static object?[] SeriesDecomposeAnomalies(object? series, double? threshold, long? seasonality, string? trend, long? testPoints,
        string? method, double? seasonalityThreshold)
    {
        var d = Decompose(series, seasonality, trend, testPoints, seasonalityThreshold);
        var (lo, hi) = string.Equals(method, "tukey", StringComparison.OrdinalIgnoreCase) ? (25.0, 75.0) : (10.0, 90.0);
        var scores = OutlierScores(d.Residual.Select(v => double.IsNaN(v) ? (double?)null : v).ToList(), lo, hi);
        var t = threshold ?? 1.5;
        var flags = scores.Select(s => (object?)(s > t ? 1L : s < -t ? -1L : 0L)).ToList();
        return [flags, scores.Select(s => (object?)s).ToList(), ToList(d.Baseline)];
    }

    // ------------------------------------------------------------------ seasonal component and outliers

    [KqlFunction("series_seasonal")]
    public static object? SeriesSeasonal(object? series) => SeriesSeasonal(series, -1);

    /// <summary>The seasonal component: each point is the median of the points at the same position in the period.</summary>
    [KqlFunction("series_seasonal")]
    public static object? SeriesSeasonal(object? series, long? period) => ToList(Decompose(series, period, "none", 0, 0.6).Seasonal);

    [KqlFunction("series_outliers")]
    public static object? SeriesOutliers(object? series, string? kind) => SeriesOutliers(series, kind, null, 10, 90);

    [KqlFunction("series_outliers")]
    public static object? SeriesOutliers(object? series, string? kind, object? ignoreValue) => SeriesOutliers(series, kind, ignoreValue, 10, 90);

    /// <summary>
    /// series_outliers with a kind ('tukey' uses the 25th/75th percentiles; 'ctukey' the given ones, 10th/90th by
    /// default) and a value to ignore when computing the percentiles.
    /// </summary>
    [KqlFunction("series_outliers")]
    public static object? SeriesOutliers(object? series, string? kind, object? ignoreValue, double? minPercentile, double? maxPercentile)
    {
        var values = Series(series).Select(KqlConvert.ToDouble).ToList();
        var (lo, hi) = string.Equals(kind, "tukey", StringComparison.OrdinalIgnoreCase) ? (25.0, 75.0) : (minPercentile ?? 10, maxPercentile ?? 90);
        var ignore = KqlConvert.ToDouble(ignoreValue);
        return OutlierScores(values, lo, hi, ignore).Select(s => (object?)s).ToList();
    }

    /// <summary>
    /// Tukey-fence scores: 0 within [Q_lo − 1.5·R, Q_hi + 1.5·R] (R = Q_hi − Q_lo), otherwise the distance from the
    /// nearer quantile in units of R (so a point just outside a fence scores ±1.5).
    /// </summary>
    private static List<double> OutlierScores(IReadOnlyList<double?> values, double lowPercentile, double highPercentile, double? ignore = null)
    {
        var sorted = values.Where(v => v.HasValue && v != ignore).Select(v => v!.Value).Order().ToArray();
        if (sorted.Length == 0) return values.Select(_ => 0.0).ToList();
        double Q(double p) => sorted[(int)Math.Clamp(Math.Ceiling(p / 100 * sorted.Length) - 1, 0, sorted.Length - 1)];
        var q1 = Q(lowPercentile);
        var q3 = Q(highPercentile);
        var range = q3 - q1;
        if (range == 0)
        {
            // flat quantiles (e.g. a residual that is zero except for a spike): fall back to the mean absolute deviation
            var median = Q(50);
            range = sorted.Average(v => Math.Abs(v - median));
        }
        return values.Select(v => v is null || range == 0 ? 0.0
            : v < q1 - 1.5 * range ? (v.Value - q1) / range
            : v > q3 + 1.5 * range ? (v.Value - q3) / range
            : 0.0).ToList();
    }
}
