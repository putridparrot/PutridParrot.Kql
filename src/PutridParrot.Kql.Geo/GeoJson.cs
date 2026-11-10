using PutridParrot.Kql.Runtime;
using NetTopologySuite.Geometries;

namespace PutridParrot.Kql.Geo;

/// <summary>Converts between KQL GeoJSON dynamic values and NetTopologySuite geometries (x = longitude, y = latitude).</summary>
internal static class GeoJson
{
    public static readonly GeometryFactory Factory = new();

    /// <summary>Reads a GeoJSON geometry; null when the value isn't one or a coordinate is out of range.</summary>
    public static Geometry? Read(object? value)
    {
        var v = Dynamic.Normalize(value);
        if (v is string s) v = Dynamic.Parse(s);
        try
        {
            return v is Dictionary<string, object?> bag ? Read(bag) : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static Geometry? Read(Dictionary<string, object?> bag)
    {
        var type = Get(bag, "type") as string;
        var c = Get(bag, "coordinates");
        return type?.ToLowerInvariant() switch
        {
            "point" => Position(c) is { } p ? Factory.CreatePoint(p) : null,
            "multipoint" => Many(c, Position) is { } ps ? Factory.CreateMultiPointFromCoords(ps) : null,
            "linestring" => Line(c),
            "multilinestring" => Many(c, Line) is { } ls ? Factory.CreateMultiLineString(ls) : null,
            "polygon" => Polygon(c),
            "multipolygon" => Many(c, Polygon) is { } polys ? Factory.CreateMultiPolygon(polys) : null,
            "geometrycollection" => Many(Get(bag, "geometries"), g => g is Dictionary<string, object?> child ? Read(child) : null) is { } gs
                ? Factory.CreateGeometryCollection(gs)
                : null,
            _ => null
        };
    }

    private static object? Get(Dictionary<string, object?> bag, string key) =>
        bag.FirstOrDefault(kv => kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;

    private static T[]? Many<T>(object? array, Func<object?, T?> read) where T : class
    {
        if (array is not List<object?> items) return null;
        var result = items.Select(read).ToArray();
        return result.All(r => r is not null) ? result.Select(r => r!).ToArray() : null;
    }

    private static Coordinate? Position(object? c) =>
        c is List<object?> { Count: >= 2 } xy && KqlConvert.ToDouble(xy[0]) is { } lng && KqlConvert.ToDouble(xy[1]) is { } lat
            && lng is >= -180 and <= 180 && lat is >= -90 and <= 90
            ? new Coordinate(lng, lat)
            : null;

    private static Coordinate[]? Positions(object? c) => Many(c, Position);

    private static LineString? Line(object? c) => Positions(c) is { Length: >= 2 } ps ? Factory.CreateLineString(ps) : null;

    private static LinearRing? Ring(object? c) =>
        Positions(c) is { Length: >= 4 } ps && ps[0].Equals2D(ps[^1]) ? Factory.CreateLinearRing(ps) : null;

    private static Polygon? Polygon(object? c) =>
        Many(c, Ring) is { Length: >= 1 } rings ? Factory.CreatePolygon(rings[0], rings[1..]) : null;

    // ------------------------------------------------------------------ writing

    public static Dictionary<string, object?> Write(Geometry g) => g switch
    {
        Point p => Shape("Point", Coord(p.Coordinate)),
        LineString l => Shape("LineString", Coords(l.Coordinates)),
        Polygon p => Shape("Polygon", Rings(p)),
        MultiPoint mp => Shape("MultiPoint", mp.Geometries.Select(x => (object?)Coord(x.Coordinate)).ToList()),
        MultiLineString ml => Shape("MultiLineString", ml.Geometries.Select(x => (object?)Coords(x.Coordinates)).ToList()),
        MultiPolygon mp => Shape("MultiPolygon", mp.Geometries.Select(x => (object?)Rings((Polygon)x)).ToList()),
        GeometryCollection gc => new Dictionary<string, object?>
        {
            ["type"] = "GeometryCollection",
            ["geometries"] = gc.Geometries.Select(x => (object?)Write(x)).ToList()
        },
        _ => throw new NotSupportedException(g.GeometryType)
    };

    /// <summary>A single geometry as itself, several as their Multi* form, nothing as an empty GeometryCollection.</summary>
    public static Dictionary<string, object?> WriteSimplest(Geometry g)
    {
        if (g.IsEmpty) return Write(Factory.CreateGeometryCollection());
        return g is GeometryCollection { NumGeometries: 1 } single ? Write(single.GetGeometryN(0)) : Write(g);
    }

    private static Dictionary<string, object?> Shape(string type, object? coordinates) =>
        new() { ["type"] = type, ["coordinates"] = coordinates };

    private static List<object?> Coord(Coordinate c) => [c.X, c.Y];

    private static List<object?> Coords(IEnumerable<Coordinate> cs) => cs.Select(c => (object?)Coord(c)).ToList();

    private static List<object?> Rings(Polygon p) =>
        new[] { p.Shell }.Concat(p.Holes).Select(r => (object?)Coords(r.Coordinates)).ToList();
}
