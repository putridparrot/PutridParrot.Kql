using H3;
using H3.Algorithms;
using H3.Extensions;
using H3.Model;
using PutridParrot.Kql.Runtime;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Utilities;
using NetTopologySuite.Operation.Linemerge;
using NetTopologySuite.Operation.Union;
using NetTopologySuite.Simplify;

namespace PutridParrot.Kql.Geo;

/// <summary>
/// The geo_* functions that need more than the core library: H3 cells, buffers, unions and intersections that return
/// geometry. Registered on a context by <see cref="KqlContextGeoExtensions.UseGeo"/>. Inputs and outputs are GeoJSON
/// dynamic values with geodesic edges; distances are in metres.
/// </summary>
public static class GeoFunctions
{
    private static readonly GeometryFactory Factory = GeoJson.Factory;

    // ------------------------------------------------------------------ H3

    private static H3Index? Cell(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 16) return null;
        try
        {
            var index = new H3Index(token.Trim());
            return index.IsValidCell ? index : null;
        }
        catch (Exception e) when (e is FormatException or OverflowException or ArgumentException)
        {
            return null;
        }
    }

    private static Coordinate ToCoordinate(LatLng ll) => new(ll.LongitudeDegrees, ll.LatitudeDegrees);

    private static H3Index FromPoint(double lng, double lat, int resolution) =>
        H3Index.FromLatLng(LatLng.FromCoordinate(new Coordinate(lng, lat)), resolution);

    private static Polygon CellPolygon(H3Index cell)
    {
        var ring = cell.GetCellBoundaryVertices().Select(ToCoordinate).ToList();
        ring.Add(ring[0].Copy());
        return Factory.CreatePolygon(ring.ToArray());
    }

    [KqlFunction("geo_point_to_h3cell")]
    public static string GeoPointToH3Cell(double? lng, double? lat) => GeoPointToH3Cell(lng, lat, 6);

    /// <summary>The H3 cell (resolution 0-15, default 6) containing the point; empty for invalid input.</summary>
    [KqlFunction("geo_point_to_h3cell")]
    public static string GeoPointToH3Cell(double? lng, double? lat, long? resolution) =>
        lng is >= -180 and <= 180 && lat is >= -90 and <= 90 && resolution is >= 0 and <= 15
            ? FromPoint(lng.Value, lat.Value, (int)resolution).ToString()
            : "";

    [KqlFunction("geo_h3cell_to_central_point")]
    public static object? GeoH3CellToCentralPoint(string? h3cell) =>
        Cell(h3cell) is { } c ? GeoJson.Write(Factory.CreatePoint(ToCoordinate(c.ToLatLng()))) : null;

    [KqlFunction("geo_h3cell_to_polygon")]
    public static object? GeoH3CellToPolygon(string? h3cell) =>
        Cell(h3cell) is { } c ? GeoJson.Write(CellPolygon(c)) : null;

    [KqlFunction("geo_h3cell_level")]
    public static long? GeoH3CellLevel(string? h3cell) => Cell(h3cell)?.Resolution;

    [KqlFunction("geo_h3cell_parent")]
    public static string? GeoH3CellParent(string? h3cell) => GeoH3CellParent(h3cell, null);

    /// <summary>The containing cell at a coarser resolution (default: one level up).</summary>
    [KqlFunction("geo_h3cell_parent")]
    public static string? GeoH3CellParent(string? h3cell, long? resolution)
    {
        if (Cell(h3cell) is not { } c) return null;
        var target = resolution ?? c.Resolution - 1;
        return target >= 0 && target < c.Resolution ? c.GetParentForResolution((int)target).ToString() : null;
    }

    private const int MaxCells = 65_536;

    [KqlFunction("geo_h3cell_children")]
    public static object? GeoH3CellChildren(string? h3cell) => GeoH3CellChildren(h3cell, null);

    /// <summary>The cells at a finer resolution (default: one level down) that make up this cell.</summary>
    [KqlFunction("geo_h3cell_children")]
    public static object? GeoH3CellChildren(string? h3cell, long? resolution)
    {
        if (Cell(h3cell) is not { } c) return null;
        var target = resolution ?? c.Resolution + 1;
        if (target <= c.Resolution || target > 15 || c.CellToChildrenSize((int)target) > MaxCells) return null;
        return c.GetChildrenForResolution((int)target).Select(x => (object?)x.ToString()).ToList();
    }

    /// <summary>The adjacent cells (6, or 5 around a pentagon).</summary>
    [KqlFunction("geo_h3cell_neighbors")]
    public static object? GeoH3CellNeighbors(string? h3cell) =>
        Cell(h3cell) is { } c ? c.GetNeighbours().Where(n => n != c).Select(n => (object?)n.ToString()).ToList() : null;

    /// <summary>Rings of cells around this one: element k holds the cells k steps away (element 0 is the cell itself).</summary>
    [KqlFunction("geo_h3cell_rings")]
    public static object? GeoH3CellRings(string? h3cell, long? distance)
    {
        if (Cell(h3cell) is not { } c || distance is not (>= 0 and <= 142)) return null;
        var byDistance = Rings.GridDiskDistances(c, (int)distance).ToLookup(r => r.Distance, r => r.Index);
        return Enumerable.Range(0, (int)distance + 1)
            .Select(k => (object?)byDistance[k].Select(x => (object?)x.ToString()).ToList())
            .ToList();
    }

    [KqlFunction("geo_polygon_to_h3cells")]
    public static object? GeoPolygonToH3Cells(object? polygon) => GeoPolygonToH3Cells(polygon, 6, 0);

    [KqlFunction("geo_polygon_to_h3cells")]
    public static object? GeoPolygonToH3Cells(object? polygon, long? resolution) => GeoPolygonToH3Cells(polygon, resolution, 0);

    /// <summary>
    /// The H3 cells (at one resolution) that intersect the polygon, or that lie within <paramref name="radius"/>
    /// metres of it. Null when the covering would exceed 65,536 cells.
    /// </summary>
    [KqlFunction("geo_polygon_to_h3cells")]
    public static object? GeoPolygonToH3Cells(object? polygon, long? resolution, double? radius)
    {
        if (Polygonal(polygon) is not { } g || resolution is not (>= 0 and <= 15) || radius is < 0) return null;
        if (radius > 0 && Buffer(g, radius.Value, 0.1) is { } buffered) g = buffered;
        else if (radius > 0) return null;

        var projection = Gnomonic.For(g.Coordinates);
        if (projection is null) return null;
        var shape = projection.Forward(g);
        bool Touches(H3Index cell)
        {
            var cellShape = CellPolygon(cell);
            return Gnomonic.For(cellShape.Coordinates.Concat([g.Coordinates[0]])) is not null && shape.Intersects(projection.Forward(cellShape));
        }

        // Flood outwards from the cells of the polygon's vertices; the cells that intersect a connected shape are connected.
        var res = (int)resolution;
        var found = new HashSet<H3Index>();
        var seen = new HashSet<H3Index>();
        var pending = new Queue<H3Index>();
        foreach (var c in g.Coordinates)
        {
            var seed = FromPoint(c.X, c.Y, res);
            if (seen.Add(seed)) pending.Enqueue(seed);
        }
        while (pending.Count > 0)
        {
            var cell = pending.Dequeue();
            if (!Touches(cell)) continue;
            found.Add(cell);
            if (found.Count > MaxCells) return null;
            foreach (var n in cell.GetNeighbours())
                if (seen.Add(n)) pending.Enqueue(n);
        }
        return found.Order().Select(c => (object?)c.ToString()).ToList();
    }

    // ------------------------------------------------------------------ buffers

    private static Geometry? Polygonal(object? value) => GeoJson.Read(value) is { } g && g is Polygon or MultiPolygon ? g : null;

    private static Geometry? Lineal(object? value) => GeoJson.Read(value) is { } g && g is LineString or MultiLineString ? g : null;

    private static bool ValidRadius(double? radius) => radius is > 0 and < Math.PI / 2 * Sphere.EarthRadius * 0.8;

    private static bool ValidTolerance(double? tolerance) => tolerance is > 0 and <= 10_000;

    [KqlFunction("geo_point_buffer")]
    public static object? GeoPointBuffer(double? lng, double? lat, double? radius) => GeoPointBuffer(lng, lat, radius, 0.1);

    /// <summary>A polygon approximating the geodesic circle; <paramref name="tolerance"/> (metres) bounds the approximation error.</summary>
    [KqlFunction("geo_point_buffer")]
    public static object? GeoPointBuffer(double? lng, double? lat, double? radius, double? tolerance)
    {
        if (lng is not (>= -180 and <= 180) || lat is not (>= -90 and <= 90) || !ValidRadius(radius) || !ValidTolerance(tolerance ?? 0.1))
            return null;
        var vertices = Sphere.CircleVertices(radius!.Value, tolerance ?? 0.1, 1024);
        return GeoJson.Write(Factory.CreatePolygon(Sphere.Circle(new Coordinate(lng.Value, lat.Value), radius.Value, vertices)));
    }

    [KqlFunction("geo_line_buffer")]
    public static object? GeoLineBuffer(object? line, double? radius) => GeoLineBuffer(line, radius, 0.1);

    /// <summary>The area within <paramref name="radius"/> metres of a LineString or MultiLineString.</summary>
    [KqlFunction("geo_line_buffer")]
    public static object? GeoLineBuffer(object? line, double? radius, double? tolerance) =>
        Lineal(line) is { } g && ValidRadius(radius) && ValidTolerance(tolerance ?? 0.1) && Buffer(g, radius!.Value, tolerance ?? 0.1) is { } b
            ? GeoJson.WriteSimplest(b)
            : null;

    [KqlFunction("geo_polygon_buffer")]
    public static object? GeoPolygonBuffer(object? polygon, double? radius) => GeoPolygonBuffer(polygon, radius, 0.1);

    /// <summary>Grows a polygon by <paramref name="radius"/> metres, or shrinks it when the radius is negative.</summary>
    [KqlFunction("geo_polygon_buffer")]
    public static object? GeoPolygonBuffer(object? polygon, double? radius, double? tolerance)
    {
        if (Polygonal(polygon) is not { } g || radius is null || !ValidTolerance(tolerance ?? 0.1)) return null;
        if (radius == 0) return GeoJson.WriteSimplest(g);
        if (!ValidRadius(Math.Abs(radius.Value))) return null;
        return Buffer(g, radius.Value, tolerance ?? 0.1) is { } b ? GeoJson.WriteSimplest(b) : null;
    }

    /// <summary>
    /// Buffers any geometry: the union of geodesic circles around its vertices and quadrilaterals along its edges
    /// (offset sideways by the radius), plus the polygons themselves; a negative radius subtracts that band from
    /// the polygons instead. The union runs in a gnomonic projection, where geodesic edges are straight.
    /// </summary>
    private static Geometry? Buffer(Geometry g, double radius, double tolerance)
    {
        var r = Math.Abs(radius);
        var projection = Gnomonic.For(g.Coordinates, r);
        if (projection is null) return null;

        var pieces = new List<Geometry>();
        var circleVertices = Sphere.CircleVertices(r, tolerance, 256);
        foreach (var c in g.Coordinates.Distinct())
            pieces.Add(projection.Forward(Factory.CreatePolygon(Sphere.Circle(c, r, circleVertices))));

        // A side of a quad is a geodesic, while the true edge of the buffer runs parallel to the segment; split long
        // segments so the two stay within the tolerance.
        var maxLength = Math.Sqrt(8 * Sphere.EarthRadius * tolerance / Math.Max(Math.Tan(r / Sphere.EarthRadius), 1e-12));
        foreach (var segmentLine in Lines(g))
        {
            var cs = segmentLine.Coordinates;
            for (var i = 0; i + 1 < cs.Length; i++)
            {
                var parts = (int)Math.Clamp(Math.Ceiling(Sphere.Distance(cs[i], cs[i + 1]) / maxLength), 1, 10_000);
                for (var k = 0; k < parts; k++)
                {
                    var a = Sphere.Interpolate(cs[i], cs[i + 1], (double)k / parts);
                    var b = Sphere.Interpolate(cs[i], cs[i + 1], (double)(k + 1) / parts);
                    if (Sphere.Distance(a, b) < 1e-6) continue;
                    var ba = Sphere.Bearing(a, b);
                    var bb = Sphere.Bearing(b, a) + Math.PI;
                    var quad = new[]
                    {
                        Sphere.Destination(a, ba - Math.PI / 2, r), Sphere.Destination(b, bb - Math.PI / 2, r),
                        Sphere.Destination(b, bb + Math.PI / 2, r), Sphere.Destination(a, ba + Math.PI / 2, r)
                    };
                    pieces.Add(GeometryFixer.Fix(projection.Forward(Factory.CreatePolygon([.. quad, quad[0].Copy()]))));
                }
            }
        }

        var band = UnaryUnionOp.Union(pieces);
        var polygons = PolygonsOf(g).Select(p => GeometryFixer.Fix(projection.Forward(p))).ToList();
        Geometry result = polygons.Count == 0 ? band
            : radius > 0 ? UnaryUnionOp.Union(polygons.Append(band).ToList())
            : UnaryUnionOp.Union(polygons).Difference(band);
        return projection.Inverse(result);
    }

    /// <summary>The geometry's lines plus its polygon rings, as line strings.</summary>
    private static IEnumerable<LineString> Lines(Geometry g) => g switch
    {
        LineString l => [l],
        Polygon p => new[] { p.Shell }.Concat(p.Holes),
        GeometryCollection gc => gc.Geometries.SelectMany(Lines),
        _ => []
    };

    private static IEnumerable<Polygon> PolygonsOf(Geometry g) => g switch
    {
        Polygon p => [p],
        GeometryCollection gc => gc.Geometries.SelectMany(PolygonsOf),
        _ => []
    };

    // ------------------------------------------------------------------ unions and intersections

    /// <summary>Runs a planar operation on the inputs in a shared gnomonic projection and returns GeoJSON.</summary>
    private static object? Planar(IReadOnlyList<Geometry> inputs, Func<Geometry[], Geometry> op)
    {
        var projection = Gnomonic.For(inputs.SelectMany(g => g.Coordinates));
        if (projection is null) return null;
        var projected = inputs.Select(g => GeometryFixer.Fix(projection.Forward(g))).ToArray();
        return GeoJson.WriteSimplest(projection.Inverse(op(projected)));
    }

    private static List<Geometry>? Array(object? array, Func<object?, Geometry?> read)
    {
        var items = Dynamic.Elements(array).Select(read).ToList();
        return items.Count > 0 && items.All(g => g is not null) ? items.Select(g => g!).ToList() : null;
    }

    [KqlFunction("geo_intersection_2polygons")]
    public static object? GeoIntersection2Polygons(object? polygon1, object? polygon2) =>
        Polygonal(polygon1) is { } a && Polygonal(polygon2) is { } b ? Planar([a, b], g => g[0].Intersection(g[1])) : null;

    /// <summary>The parts of the line inside the polygon.</summary>
    [KqlFunction("geo_intersection_line_with_polygon")]
    public static object? GeoIntersectionLineWithPolygon(object? line, object? polygon) =>
        Lineal(line) is { } a && Polygonal(polygon) is { } b ? Planar([a, b], g => g[0].Intersection(g[1])) : null;

    [KqlFunction("geo_union_polygons_array")]
    public static object? GeoUnionPolygonsArray(object? polygons) =>
        Array(polygons, Polygonal) is { } gs ? Planar(gs, g => UnaryUnionOp.Union(g)) : null;

    /// <summary>The union of the lines, with overlapping parts merged and touching lines joined end to end.</summary>
    [KqlFunction("geo_union_lines_array")]
    public static object? GeoUnionLinesArray(object? lines) =>
        Array(lines, Lineal) is { } gs
            ? Planar(gs, g =>
            {
                var merger = new LineMerger();
                merger.Add(UnaryUnionOp.Union(g));
                return Factory.BuildGeometry(merger.GetMergedLineStrings());
            })
            : null;

    [KqlFunction("geo_simplify_polygons_array")]
    public static object? GeoSimplifyPolygonsArray(object? polygons) => GeoSimplifyPolygonsArray(polygons, 10);

    /// <summary>The union of the polygons, simplified so no point moves more than <paramref name="tolerance"/> metres.</summary>
    [KqlFunction("geo_simplify_polygons_array")]
    public static object? GeoSimplifyPolygonsArray(object? polygons, double? tolerance) =>
        Array(polygons, Polygonal) is { } gs && tolerance is >= 0
            ? Planar(gs, g => TopologyPreservingSimplifier.Simplify(UnaryUnionOp.Union(g), tolerance.Value / Sphere.EarthRadius))
            : null;
}
