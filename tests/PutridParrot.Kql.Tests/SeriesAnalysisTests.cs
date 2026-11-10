namespace PutridParrot.Kql.Tests;

/// <summary>Series analysis functions and functions that return several columns (tuples).</summary>
public class SeriesAnalysisTests
{
    private readonly KqlContext _kql = new();

    private static List<object?> L(params object?[] items) => [.. items];

    private static double[] D(object? series) => ((List<object?>)series!).Select(v => Convert.ToDouble(v)).ToArray();

    // a weekly pattern repeated over 8 weeks, with a linear trend and one spike at index 40
    private static string Seasonal(bool spike = true, bool trend = false)
    {
        int[] week = [10, 12, 14, 30, 14, 12, 10];
        var values = Enumerable.Range(0, 56).Select(i => week[i % 7] + (trend ? i * 0.5 : 0) + (spike && i == 40 ? 60 : 0));
        return $"let T = datatable(id:long, y:dynamic)[1, dynamic([{string.Join(", ", values)}])];\n";
    }

    [Fact]
    public void ExtendUnpacksTupleIntoGeneratedColumnNames()
    {
        var r = _kql.Execute("datatable(y:dynamic)[dynamic([1, 5, 3])] | extend series_stats(y)");
        Assert.Equal(["y", "series_stats_y_min", "series_stats_y_min_idx", "series_stats_y_max", "series_stats_y_max_idx",
            "series_stats_y_avg", "series_stats_y_stdev", "series_stats_y_variance"], r.Columns.Select(c => c.Name));
        Assert.Equal(1.0, r[0, "series_stats_y_min"]);
        Assert.Equal(1L, r[0, "series_stats_y_max_idx"]);
        Assert.Equal(3.0, r[0, "series_stats_y_avg"]);
        Assert.Equal(4.0, r[0, "series_stats_y_variance"]);
    }

    [Fact]
    public void ExtendTupleAssignmentMixedWithOtherColumns()
    {
        var r = _kql.Execute("datatable(y:dynamic)[dynamic([2, 4, 6, 8])] | extend k = 1, (rsq, slope, v, rv, icpt, fit) = series_fit_line(y), z = 2");
        Assert.Equal(["y", "k", "rsq", "slope", "v", "rv", "icpt", "fit", "z"], r.Columns.Select(c => c.Name));
        Assert.Equal(1.0, (double)r[0, "rsq"]!, 9);
        Assert.Equal(2.0, (double)r[0, "slope"]!, 9);
        Assert.Equal(2.0, (double)r[0, "icpt"]!, 9);
        Assert.Equal(2L, r[0, "z"]);
    }

    [Fact]
    public void ProjectWithTuple()
    {
        var r = _kql.Execute("datatable(y:dynamic)[dynamic([1, 0, -1, 0])] | project a = 1, (re, im) = series_fft(y)");
        Assert.Equal(["a", "re", "im"], r.Columns.Select(c => c.Name));
        // DFT of [1, 0, -1, 0] = [0, 2, 0, 2]
        Assert.Equal([0, 2, 0, 2], D(r[0, "re"]).Select(v => Math.Round(v, 9)));
        Assert.All(D(r[0, "im"]), v => Assert.Equal(0, v, 9));
    }

    [Fact]
    public void FftThenIfftRoundTrips()
    {
        var r = _kql.Execute("""
            datatable(y:dynamic)[dynamic([3, 1, 4, 1, 5])]
            | extend (re, im) = series_fft(y)
            | extend (back, backIm) = series_ifft(re, im)
            """);
        Assert.Equal([3, 1, 4, 1, 5], D(r[0, "back"]).Select(v => Math.Round(v, 9)));
    }

    [Fact]
    public void Iir_CumulativeSum()
    {
        var r = _kql.Execute("print r = series_iir(dynamic([1, 2, 3, 4]), dynamic([1]), dynamic([1, -1]))");
        Assert.Equal(L(1.0, 3.0, 6.0, 10.0), r.Scalar());
    }

    [Fact]
    public void FitPoly_RecoversQuadratic()
    {
        // y = 2x² - 3x + 1 at x = 1..5
        var r = _kql.Execute("datatable(y:dynamic)[dynamic([0, 3, 10, 21, 36])] | extend (rsq, coeff, v, rv, fit) = series_fit_poly(y, dynamic(null), int(2))");
        Assert.Equal(1.0, (double)r[0, "rsq"]!, 9);
        Assert.Equal([2, -3, 1], D(r[0, "coeff"]).Select(c => Math.Round(c, 6)));
    }

    [Fact]
    public void FitTwoLines_FindsTheBreak()
    {
        var r = _kql.Execute("datatable(y:dynamic)[dynamic([1, 2, 3, 4, 5, 5, 4, 3, 2, 1])] | extend series_fit_2lines(y)");
        Assert.Equal(5L, r[0, "series_fit_2lines_y_split_idx"]);
        Assert.Equal(1.0, (double)r[0, "series_fit_2lines_y_left_slope"]!, 9);
        Assert.Equal(-1.0, (double)r[0, "series_fit_2lines_y_right_slope"]!, 9);

        var bag = (Dictionary<string, object?>)_kql.Execute("print r = series_fit_2lines_dynamic(dynamic([1, 2, 3, 4, 5, 5, 4, 3, 2, 1]))").Scalar()!;
        Assert.Equal(5L, bag["split_idx"]);
    }

    [Fact]
    public void PeriodsDetectAndValidate()
    {
        var r = _kql.Execute(Seasonal(spike: false) + "T | extend series_periods_detect(y, 0, 20, 2), series_periods_validate(y, 7, 5)");
        Assert.Equal(7.0, ((List<object?>)r[0, "series_periods_detect_y_periods"]!)[0]);
        Assert.True((double)((List<object?>)r[0, "series_periods_detect_y_scores"]!)[0]! > 0.9);
        var scores = D(r[0, "series_periods_validate_y_scores"]);
        Assert.True(scores[0] > 0.9);
        Assert.True(scores[1] < 0.5);
    }

    [Fact]
    public void Decompose_SeasonalAndResidual()
    {
        var r = _kql.Execute(Seasonal() + "T | extend (baseline, seasonal, trend, residual) = series_decompose(y)");
        var seasonal = D(r[0, "seasonal"]);
        var residual = D(r[0, "residual"]);
        Assert.Equal(20, seasonal[3] - seasonal[0], 9);   // median of each weekday position
        Assert.Equal(seasonal[3], seasonal[10], 9);
        // the spike is what's left over (less its share of the average trend, 60/56)
        Assert.Equal(60 - 60.0 / 56, residual[40], 6);
        Assert.Equal(-60.0 / 56, residual[41], 6);
    }

    [Fact]
    public void Decompose_LinefitTrend()
    {
        var r = _kql.Execute(Seasonal(spike: false, trend: true) + "T | extend (b, s, t, res) = series_decompose(y, 7, 'linefit')");
        var trend = D(r[0, "t"]);
        Assert.Equal(0.5, trend[11] - trend[10], 6);
        Assert.All(D(r[0, "res"]), v => Assert.True(Math.Abs(v) < 2.0));
    }

    [Fact]
    public void DecomposeAnomalies_FlagsTheSpike()
    {
        var r = _kql.Execute(Seasonal() + "T | extend (flag, score, baseline) = series_decompose_anomalies(y)");
        var flags = (List<object?>)r[0, "flag"]!;
        Assert.Equal(1L, flags[40]);
        Assert.Equal(1, flags.Count(f => (long)f! != 0));
        Assert.Equal(56, ((List<object?>)r[0, "baseline"]!).Count);
    }

    [Fact]
    public void DecomposeForecast_ExtrapolatesTheLastPoints()
    {
        var r = _kql.Execute(Seasonal(spike: false, trend: true) + "T | extend (forecast, s, t, res) = series_decompose_forecast(y, 7)");
        var forecast = D(r[0, "forecast"]);
        // the weekly peak (30) plus the trend (0.5 per point) at index 52
        Assert.Equal(30 + 52 * 0.5, forecast[52], 1);
    }

    [Fact]
    public void Seasonal_MedianPerPosition()
    {
        var r = _kql.Execute("print r = series_seasonal(dynamic([1, 5, 3, 7, 1, 5]), 2)");
        Assert.Equal(L(1.0, 5.0, 1.0, 5.0, 1.0, 5.0), r.Scalar());
    }

    [Fact]
    public void OutliersWithKind()
    {
        var scores = (List<object?>)_kql.Execute("print r = series_outliers(dynamic([1, 2, 1, 2, 1, 2, 1, 2, 1, 50]), 'tukey')").Scalar()!;
        Assert.True((double)scores[^1]! > 1.5);
        Assert.Equal(0.0, scores[0]);
    }
}
