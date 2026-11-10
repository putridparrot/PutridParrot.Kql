namespace Kql.Net.Runtime;

// geo_* functions. Geometries are GeoJSON dynamic values ({"type": "Polygon", "coordinates": [...]}), coordinates are
// [longitude, latitude] in degrees, edges are geodesics on a sphere, and distances are in metres.
public static partial class KqlFunctions
{
    private static LngLat? GeoPoint(double? lng, double? lat) =>
        lng is { } x && lat is { } y && new LngLat(x, y) is { IsValid: true } p ? p : null;

    private static Geometry? GeoLines(object? value) =>
        Geometry.Parse(value) is { Lines.Count: > 0, Points.Count: 0, Polygons.Count: 0 } g ? g : null;

    private static Geometry? GeoPolygons(object? value) =>
        Geometry.Parse(value) is { Polygons.Count: > 0, Points.Count: 0, Lines.Count: 0 } g ? g : null;

    // ------------------------------------------------------------------ points

    [KqlFunction("geo_distance_2points")]
    public static double? GeoDistance2Points(double? lng1, double? lat1, double? lng2, double? lat2) =>
        GeoDistance2Points(lng1, lat1, lng2, lat2, false);

    /// <summary>Shortest distance in metres; on a sphere, or on the WGS-84 ellipsoid with <paramref name="useSpheroid"/>.</summary>
    [KqlFunction("geo_distance_2points")]
    public static double? GeoDistance2Points(double? lng1, double? lat1, double? lng2, double? lat2, bool? useSpheroid)
    {
        if (GeoPoint(lng1, lat1) is not { } a || GeoPoint(lng2, lat2) is not { } b) return null;
        return useSpheroid == true ? Vincenty(a, b) : Geo.Distance(a, b);
    }

    /// <summary>Vincenty's inverse formula on the WGS-84 ellipsoid (falls back to the sphere if it doesn't converge).</summary>
    private static double Vincenty(LngLat p1, LngLat p2)
    {
        const double a = 6378137.0, f = 1 / 298.257223563, b = a * (1 - f);
        var L = (p2.Lng - p1.Lng) * Math.PI / 180;
        var U1 = Math.Atan((1 - f) * Math.Tan(p1.Lat * Math.PI / 180));
        var U2 = Math.Atan((1 - f) * Math.Tan(p2.Lat * Math.PI / 180));
        double sinU1 = Math.Sin(U1), cosU1 = Math.Cos(U1), sinU2 = Math.Sin(U2), cosU2 = Math.Cos(U2);
        double λ = L, sinσ, cosσ, σ, cos2α, cos2σm;
        for (var iter = 0; ; iter++)
        {
            var sinλ = Math.Sin(λ);
            var cosλ = Math.Cos(λ);
            sinσ = Math.Sqrt(Math.Pow(cosU2 * sinλ, 2) + Math.Pow(cosU1 * sinU2 - sinU1 * cosU2 * cosλ, 2));
            if (sinσ == 0) return 0;
            cosσ = sinU1 * sinU2 + cosU1 * cosU2 * cosλ;
            σ = Math.Atan2(sinσ, cosσ);
            var sinα = cosU1 * cosU2 * sinλ / sinσ;
            cos2α = 1 - sinα * sinα;
            cos2σm = cos2α == 0 ? 0 : cosσ - 2 * sinU1 * sinU2 / cos2α;
            var C = f / 16 * cos2α * (4 + f * (4 - 3 * cos2α));
            var prev = λ;
            λ = L + (1 - C) * f * sinα * (σ + C * sinσ * (cos2σm + C * cosσ * (-1 + 2 * cos2σm * cos2σm)));
            if (Math.Abs(λ - prev) < 1e-12) break;
            if (iter > 200) return Geo.Distance(p1, p2);
        }
        var u2 = cos2α * (a * a - b * b) / (b * b);
        var A = 1 + u2 / 16384 * (4096 + u2 * (-768 + u2 * (320 - 175 * u2)));
        var B = u2 / 1024 * (256 + u2 * (-128 + u2 * (74 - 47 * u2)));
        var Δσ = B * sinσ * (cos2σm + B / 4 * (cosσ * (-1 + 2 * cos2σm * cos2σm) - B / 6 * cos2σm * (-3 + 4 * sinσ * sinσ) * (-3 + 4 * cos2σm * cos2σm)));
        return b * A * (σ - Δσ);
    }

    /// <summary>Bearing from p1 to p2 in radians, clockwise from north, in [0, 2π); null for identical points.</summary>
    [KqlFunction("geo_azimuth")]
    public static double? GeoAzimuth(double? lng1, double? lat1, double? lng2, double? lat2) =>
        GeoPoint(lng1, lat1) is { } a && GeoPoint(lng2, lat2) is { } b && a != b ? Geo.Azimuth(a, b) : null;

    /// <summary>The clockwise angle at p2 from the line p2-p1 to the line p2-p3, in radians [0, 2π).</summary>
    [KqlFunction("geo_angle")]
    public static double? GeoAngle(double? lng1, double? lat1, double? lng2, double? lat2, double? lng3, double? lat3)
    {
        if (GeoPoint(lng1, lat1) is not { } p1 || GeoPoint(lng2, lat2) is not { } p2 || GeoPoint(lng3, lat3) is not { } p3) return null;
        if (p1 == p2 || p2 == p3) return null;
        var angle = Geo.Azimuth(p2, p3) - Geo.Azimuth(p2, p1);
        return (angle + 2 * Math.PI) % (2 * Math.PI);
    }

    [KqlFunction("geo_point_in_circle")]
    public static bool? GeoPointInCircle(double? lng, double? lat, double? centerLng, double? centerLat, double? radius)
    {
        if (GeoPoint(lng, lat) is not { } p || GeoPoint(centerLng, centerLat) is not { } c || radius is not >= 0) return null;
        return Geo.Distance(p, c) <= radius;
    }

    // ------------------------------------------------------------------ points against lines and polygons

    [KqlFunction("geo_point_in_polygon")]
    public static bool? GeoPointInPolygon(double? lng, double? lat, object? polygon) =>
        GeoPoint(lng, lat) is { } p && GeoPolygons(polygon) is { } g ? Geo.Contains(g, p.ToVec()) : null;

    [KqlFunction("geo_distance_point_to_line")]
    public static double? GeoDistancePointToLine(double? lng, double? lat, object? line) =>
        GeoPoint(lng, lat) is { } p && GeoLines(line) is { } g ? Geo.MinDistance(g, p.ToVec(), out _) * Geo.EarthRadius : null;

    /// <summary>Distance to the polygon's boundary, or 0 for a point inside it.</summary>
    [KqlFunction("geo_distance_point_to_polygon")]
    public static double? GeoDistancePointToPolygon(double? lng, double? lat, object? polygon)
    {
        if (GeoPoint(lng, lat) is not { } p || GeoPolygons(polygon) is not { } g) return null;
        var v = p.ToVec();
        return Geo.Contains(g, v) ? 0 : Geo.MinDistance(g, v, out _) * Geo.EarthRadius;
    }

    [KqlFunction("geo_closest_point_on_line")]
    public static object? GeoClosestPointOnLine(double? lng, double? lat, object? line)
    {
        if (GeoPoint(lng, lat) is not { } p || GeoLines(line) is not { } g) return null;
        Geo.MinDistance(g, p.ToVec(), out var closest);
        return Geo.Point(LngLat.FromVec(closest));
    }

    /// <summary>The nearest point of the polygon: the point itself when it's inside.</summary>
    [KqlFunction("geo_closest_point_on_polygon")]
    public static object? GeoClosestPointOnPolygon(double? lng, double? lat, object? polygon)
    {
        if (GeoPoint(lng, lat) is not { } p || GeoPolygons(polygon) is not { } g) return null;
        var v = p.ToVec();
        if (Geo.Contains(g, v)) return Geo.Point(p);
        Geo.MinDistance(g, v, out var closest);
        return Geo.Point(LngLat.FromVec(closest));
    }

    // ------------------------------------------------------------------ measures

    [KqlFunction("geo_line_length")]
    public static double? GeoLineLength(object? line) =>
        GeoLines(line) is { } g ? g.LineSegments().Sum(s => s.A.Angle(s.B)) * Geo.EarthRadius : null;

    [KqlFunction("geo_polygon_area")]
    public static double? GeoPolygonArea(object? polygon) =>
        GeoPolygons(polygon) is { } g
            ? g.Polygons.Sum(p => Geo.RingArea(p[0]) - p.Skip(1).Sum(Geo.RingArea)) * Geo.EarthRadius * Geo.EarthRadius
            : null;

    [KqlFunction("geo_polygon_perimeter")]
    public static double? GeoPolygonPerimeter(object? polygon) =>
        GeoPolygons(polygon) is { } g ? g.PolygonEdges().Sum(s => s.A.Angle(s.B)) * Geo.EarthRadius : null;

    [KqlFunction("geo_line_centroid")]
    public static object? GeoLineCentroid(object? line)
    {
        if (GeoLines(line) is not { } g) return null;
        var sum = new Vec3(0, 0, 0);
        foreach (var (a, b) in g.LineSegments()) sum += Geo.Interpolate(a, b, 0.5) * a.Angle(b);
        return Geo.Point(sum.Norm < 1e-15 ? g.Lines[0][0] : LngLat.FromVec(sum));
    }

    [KqlFunction("geo_polygon_centroid")]
    public static object? GeoPolygonCentroid(object? polygon)
    {
        if (GeoPolygons(polygon) is not { } g) return null;
        var sum = new Vec3(0, 0, 0);
        foreach (var p in g.Polygons)
        {
            sum += Geo.RingMoment(p[0]);
            foreach (var hole in p.Skip(1)) sum -= Geo.RingMoment(hole);
        }
        return sum.Norm < 1e-15 ? null : Geo.Point(LngLat.FromVec(sum));
    }

    /// <summary>The point a <paramref name="fraction"/> (0-1) of the way along a LineString.</summary>
    [KqlFunction("geo_line_interpolate_point")]
    public static object? GeoLineInterpolatePoint(object? line, double? fraction)
    {
        if (GeoLines(line) is not { Lines.Count: 1 } g || fraction is not (>= 0 and <= 1)) return null;
        var segments = g.LineSegments().ToList();
        var total = segments.Sum(s => s.A.Angle(s.B));
        var target = fraction.Value * total;
        foreach (var (a, b) in segments)
        {
            var length = a.Angle(b);
            if (target <= length && length > 0) return Geo.Point(LngLat.FromVec(Geo.Interpolate(a, b, target / length)));
            target -= length;
        }
        return Geo.Point(g.Lines[0][^1]);
    }

    [KqlFunction("geo_line_locate_point")]
    public static double? GeoLineLocatePoint(object? line, double? lng, double? lat) => GeoLineLocatePoint(line, lng, lat, false);

    /// <summary>How far along a LineString (as a fraction 0-1) the point nearest to (lng, lat) lies.</summary>
    [KqlFunction("geo_line_locate_point")]
    public static double? GeoLineLocatePoint(object? line, double? lng, double? lat, bool? useSpheroid)
    {
        if (GeoLines(line) is not { Lines.Count: 1 } g || GeoPoint(lng, lat) is not { } p) return null;
        var v = p.ToVec();
        var segments = g.LineSegments().ToList();
        var total = segments.Sum(s => s.A.Angle(s.B));
        if (total == 0) return 0;
        double best = double.MaxValue, along = 0, travelled = 0;
        foreach (var (a, b) in segments)
        {
            var d = Geo.ToSegment(v, a, b, out var closest);
            if (d < best)
            {
                best = d;
                along = travelled + a.Angle(closest);
            }
            travelled += a.Angle(b);
        }
        return Math.Clamp(along / total, 0, 1);
    }

    // ------------------------------------------------------------------ densify and simplify

    private static List<LngLat> Densify(List<LngLat> points, double tolerance)
    {
        var result = new List<LngLat> { points[0] };
        for (var i = 0; i + 1 < points.Count; i++) DensifyEdge(points[i], points[i + 1], tolerance / Geo.EarthRadius, result, 0);
        return result;
    }

    /// <summary>Adds points along the geodesic a-b until the straight (planar lng/lat) chords stay within the tolerance.</summary>
    private static void DensifyEdge(LngLat a, LngLat b, double tolerance, List<LngLat> output, int depth)
    {
        var bl = b.Lng - a.Lng > 180 ? b.Lng - 360 : b.Lng - a.Lng < -180 ? b.Lng + 360 : b.Lng;
        var midLng = (a.Lng + bl) / 2;
        if (midLng > 180) midLng -= 360;
        if (midLng < -180) midLng += 360;
        var planar = new LngLat(midLng, (a.Lat + b.Lat) / 2);
        var geodesic = LngLat.FromVec(Geo.Interpolate(a.ToVec(), b.ToVec(), 0.5));
        if (depth < 16 && planar.ToVec().Angle(geodesic.ToVec()) > tolerance)
        {
            DensifyEdge(a, geodesic, tolerance, output, depth + 1);
            DensifyEdge(geodesic, b, tolerance, output, depth + 1);
        }
        else
        {
            output.Add(b);
        }
    }

    /// <summary>Douglas-Peucker: drops points that lie within the tolerance of the geodesic between the kept ones.</summary>
    private static List<LngLat> Simplify(List<LngLat> points, double tolerance)
    {
        var keep = new bool[points.Count];
        keep[0] = keep[^1] = true;
        var tol = tolerance / Geo.EarthRadius;
        var stack = new Stack<(int, int)>([(0, points.Count - 1)]);
        while (stack.Count > 0)
        {
            var (i, j) = stack.Pop();
            if (j - i < 2) continue;
            var (a, b) = (points[i].ToVec(), points[j].ToVec());
            var (far, farDistance) = (-1, -1.0);
            for (var k = i + 1; k < j; k++)
            {
                var d = Geo.ToSegment(points[k].ToVec(), a, b, out _);
                if (d > farDistance) (far, farDistance) = (k, d);
            }
            if (farDistance <= tol) continue;
            keep[far] = true;
            stack.Push((i, far));
            stack.Push((far, j));
        }
        return points.Where((_, k) => keep[k]).ToList();
    }

    private static object? Lines(Geometry g) =>
        g.Lines.Count == 1 ? Geo.LineString(g.Lines[0]) : Geo.MultiLineString(g.Lines);

    private static object? Polygons(List<List<List<LngLat>>> polygons) =>
        polygons.Count == 1 ? Geo.Polygon(polygons[0]) : Geo.MultiPolygon(polygons);

    private static bool ValidTolerance(double? tolerance) => tolerance is >= 0.1 and <= 10_000;

    [KqlFunction("geo_line_densify")]
    public static object? GeoLineDensify(object? line) => GeoLineDensify(line, 10, false);

    [KqlFunction("geo_line_densify")]
    public static object? GeoLineDensify(object? line, double? tolerance) => GeoLineDensify(line, tolerance, false);

    /// <summary>Adds points so that drawing straight lng/lat lines between them stays within the tolerance (metres) of the geodesic edges.</summary>
    [KqlFunction("geo_line_densify")]
    public static object? GeoLineDensify(object? line, double? tolerance, bool? preserveCrossing)
    {
        if (GeoLines(line) is not { } g || !ValidTolerance(tolerance ?? 10)) return null;
        var dense = new Geometry();
        dense.Lines.AddRange(g.Lines.Select(l => Densify(l, tolerance ?? 10)));
        return Lines(dense);
    }

    [KqlFunction("geo_polygon_densify")]
    public static object? GeoPolygonDensify(object? polygon) => GeoPolygonDensify(polygon, 10, false);

    [KqlFunction("geo_polygon_densify")]
    public static object? GeoPolygonDensify(object? polygon, double? tolerance) => GeoPolygonDensify(polygon, tolerance, false);

    [KqlFunction("geo_polygon_densify")]
    public static object? GeoPolygonDensify(object? polygon, double? tolerance, bool? preserveCrossing)
    {
        if (GeoPolygons(polygon) is not { } g || !ValidTolerance(tolerance ?? 10)) return null;
        return Polygons(g.Polygons.Select(p => p.Select(r => Densify(r, tolerance ?? 10)).ToList()).ToList());
    }

    [KqlFunction("geo_line_simplify")]
    public static object? GeoLineSimplify(object? line) => GeoLineSimplify(line, 10);

    [KqlFunction("geo_line_simplify")]
    public static object? GeoLineSimplify(object? line, double? tolerance)
    {
        if (GeoLines(line) is not { } g || tolerance is < 0) return null;
        var simple = new Geometry();
        simple.Lines.AddRange(g.Lines.Select(l => Simplify(l, tolerance ?? 10)));
        return Lines(simple);
    }

    [KqlFunction("geo_polygon_simplify")]
    public static object? GeoPolygonSimplify(object? polygon) => GeoPolygonSimplify(polygon, 10);

    /// <summary>Simplifies each ring; holes that collapse are dropped, and a polygon whose shell collapses is dropped.</summary>
    [KqlFunction("geo_polygon_simplify")]
    public static object? GeoPolygonSimplify(object? polygon, double? tolerance)
    {
        if (GeoPolygons(polygon) is not { } g || tolerance is < 0) return null;
        var polygons = g.Polygons
            .Select(p => p.Select(r => Simplify(r, tolerance ?? 10)).ToList())
            .Where(p => p[0].Count >= 4)
            .Select(p => p.Take(1).Concat(p.Skip(1).Where(r => r.Count >= 4)).ToList())
            .ToList();
        return polygons.Count == 0 ? null : Polygons(polygons);
    }

    // ------------------------------------------------------------------ intersection tests

    [KqlFunction("geo_intersects_2lines")]
    public static bool? GeoIntersects2Lines(object? line1, object? line2) =>
        GeoLines(line1) is { } a && GeoLines(line2) is { } b ? Geo.Intersects(a, b) : null;

    [KqlFunction("geo_intersects_line_with_polygon")]
    public static bool? GeoIntersectsLineWithPolygon(object? line, object? polygon) =>
        GeoLines(line) is { } a && GeoPolygons(polygon) is { } b ? Geo.Intersects(a, b) : null;

    [KqlFunction("geo_intersects_2polygons")]
    public static bool? GeoIntersects2Polygons(object? polygon1, object? polygon2) =>
        GeoPolygons(polygon1) is { } a && GeoPolygons(polygon2) is { } b ? Geo.Intersects(a, b) : null;

    /// <summary>The points where two lines cross: a Point, a MultiPoint, or an empty GeometryCollection.</summary>
    [KqlFunction("geo_intersection_2lines")]
    public static object? GeoIntersection2Lines(object? line1, object? line2)
    {
        if (GeoLines(line1) is not { } a || GeoLines(line2) is not { } b) return null;
        var points = new List<LngLat>();
        foreach (var (p, q) in a.LineSegments())
            foreach (var (r, s) in b.LineSegments())
                if (Geo.Crossing(p, q, r, s) is { } x && LngLat.FromVec(x) is var ll && !points.Any(e => e.ToVec().Angle(x) < 1e-12))
                    points.Add(ll);
        return points.Count switch
        {
            0 => new Dictionary<string, object?> { ["type"] = "GeometryCollection", ["geometries"] = new List<object?>() },
            1 => Geo.Point(points[0]),
            _ => new Dictionary<string, object?> { ["type"] = "MultiPoint", ["coordinates"] = points.Select(p => (object?)new List<object?> { p.Lng, p.Lat }).ToList() }
        };
    }

    // ------------------------------------------------------------------ WKT

    [KqlFunction("geo_from_wkt")]
    public static object? GeoFromWkt(string? wkt) => Geo.FromWkt(wkt);

    // ------------------------------------------------------------------ geohash

    [KqlFunction("geo_point_to_geohash")]
    public static string GeoPointToGeohash(double? lng, double? lat) => GeoPointToGeohash(lng, lat, 5);

    /// <summary>The geohash (accuracy 1-18 characters) of the cell containing the point; empty for invalid input.</summary>
    [KqlFunction("geo_point_to_geohash")]
    public static string GeoPointToGeohash(double? lng, double? lat, long? accuracy) =>
        GeoPoint(lng, lat) is { } p && accuracy is >= 1 and <= 18 ? Geohash.Encode(p.Lng, p.Lat, (int)accuracy) : "";

    [KqlFunction("geo_geohash_to_central_point")]
    public static object? GeoGeohashToCentralPoint(string? geohash) =>
        Geohash.Decode(geohash) is { } b ? Geo.Point(new LngLat((b.W + b.E) / 2, (b.S + b.N) / 2)) : null;

    [KqlFunction("geo_geohash_to_polygon")]
    public static object? GeoGeohashToPolygon(string? geohash) =>
        Geohash.Decode(geohash) is { } b
            ? Geo.Polygon([[new(b.W, b.S), new(b.E, b.S), new(b.E, b.N), new(b.W, b.N), new(b.W, b.S)]])
            : null;

    /// <summary>The geohashes of the (up to 8) cells around this one, at the same accuracy.</summary>
    [KqlFunction("geo_geohash_neighbors")]
    public static object? GeoGeohashNeighbors(string? geohash)
    {
        if (Geohash.Decode(geohash) is not { } b) return null;
        var (w, h) = (b.E - b.W, b.N - b.S);
        var (lng, lat) = ((b.W + b.E) / 2, (b.S + b.N) / 2);
        var result = new List<object?>();
        foreach (var (dx, dy) in new[] { (0, 1), (1, 1), (1, 0), (1, -1), (0, -1), (-1, -1), (-1, 0), (-1, 1) })
        {
            var y = lat + dy * h;
            if (y is > 90 or < -90) continue;
            var x = lng + dx * w;
            if (x > 180) x -= 360;
            if (x < -180) x += 360;
            var n = Geohash.Encode(x, y, geohash!.Length);
            if (n != geohash.ToLowerInvariant() && !result.Contains(n)) result.Add(n);
        }
        return result;
    }

    // ------------------------------------------------------------------ S2 cells

    [KqlFunction("geo_point_to_s2cell")]
    public static string GeoPointToS2Cell(double? lng, double? lat) => GeoPointToS2Cell(lng, lat, 11);

    /// <summary>The token of the S2 cell (level 0-30, default 11) containing the point; empty for invalid input.</summary>
    [KqlFunction("geo_point_to_s2cell")]
    public static string GeoPointToS2Cell(double? lng, double? lat, long? level) =>
        GeoPoint(lng, lat) is { } p && level is >= 0 and <= S2.MaxLevel ? S2.ToToken(S2.FromPoint(p, (int)level)) : "";

    [KqlFunction("geo_s2cell_to_central_point")]
    public static object? GeoS2CellToCentralPoint(string? s2cell) =>
        S2.FromToken(s2cell) is { } id ? Geo.Point(S2.Center(id)) : null;

    [KqlFunction("geo_s2cell_to_polygon")]
    public static object? GeoS2CellToPolygon(string? s2cell) =>
        S2.FromToken(s2cell) is { } id ? Geo.Polygon([S2.Vertices(id)]) : null;

    [KqlFunction("geo_s2cell_neighbors")]
    public static object? GeoS2CellNeighbors(string? s2cell) =>
        S2.FromToken(s2cell) is { } id ? S2.Neighbors(id).Select(n => (object?)S2.ToToken(n)).ToList() : null;

    [KqlFunction("geo_polygon_to_s2cells")]
    public static object? GeoPolygonToS2Cells(object? polygon) => GeoPolygonToS2Cells(polygon, 11, 0);

    [KqlFunction("geo_polygon_to_s2cells")]
    public static object? GeoPolygonToS2Cells(object? polygon, long? level) => GeoPolygonToS2Cells(polygon, level, 0);

    /// <summary>The tokens of the S2 cells (at one level) that cover the polygon, optionally within a radius (metres) of it.</summary>
    [KqlFunction("geo_polygon_to_s2cells")]
    public static object? GeoPolygonToS2Cells(object? polygon, long? level, double? radius) =>
        GeoPolygons(polygon) is { } g ? S2Cover(g, level, radius) : null;

    [KqlFunction("geo_line_to_s2cells")]
    public static object? GeoLineToS2Cells(object? line) => GeoLineToS2Cells(line, 11, 0);

    [KqlFunction("geo_line_to_s2cells")]
    public static object? GeoLineToS2Cells(object? line, long? level) => GeoLineToS2Cells(line, level, 0);

    [KqlFunction("geo_line_to_s2cells")]
    public static object? GeoLineToS2Cells(object? line, long? level, double? radius) =>
        GeoLines(line) is { } g ? S2Cover(g, level, radius) : null;

    private const int MaxCoveringCells = 65_536;

    /// <summary>Subdivides from the six face cells, keeping cells that touch the geometry; null if the covering is too large.</summary>
    private static object? S2Cover(Geometry g, long? level, double? radius)
    {
        if (level is not (>= 0 and <= S2.MaxLevel) || radius is < 0) return null;
        var within = (radius ?? 0) / Geo.EarthRadius;
        var result = new List<ulong>();
        var pending = new Stack<ulong>(S2.FaceCells());
        while (pending.Count > 0)
        {
            var cell = pending.Pop();
            var shape = new Geometry();
            shape.Polygons.Add([S2.Vertices(cell)]);
            if (within > 0 ? Geo.Distance(shape, g) > within : !Geo.Intersects(shape, g)) continue;
            if (S2.Level(cell) == level)
            {
                result.Add(cell);
                if (result.Count > MaxCoveringCells) return null;
            }
            else
            {
                foreach (var child in S2.Children(cell)) pending.Push(child);
            }
        }
        return result.Order().Select(c => (object?)S2.ToToken(c)).ToList();
    }
}
