namespace PutridParrot.Kql.Tests;

public class GeoTests
{
    private readonly KqlContext _kql = new();

    private object? Eval(string expression) => _kql.Execute("print r = " + expression).Scalar();

    private const double Degree = 111_195.0797; // one degree of a great circle, in metres

    private const string Square = """dynamic({"type":"Polygon","coordinates":[[[-1,-1],[1,-1],[1,1],[-1,1],[-1,-1]]]})""";
    private const string SquareWithHole =
        """dynamic({"type":"Polygon","coordinates":[[[-2,-2],[2,-2],[2,2],[-2,2],[-2,-2]],[[-1,-1],[1,-1],[1,1],[-1,1],[-1,-1]]]})""";
    private const string Equator = """dynamic({"type":"LineString","coordinates":[[0,0],[2,0]]})""";

    private static (double Lng, double Lat) Coordinates(object? point)
    {
        var c = (List<object?>)((Dictionary<string, object?>)point!)["coordinates"]!;
        return (Convert.ToDouble(c[0]), Convert.ToDouble(c[1]));
    }

    [Fact]
    public void Distance2Points()
    {
        // documented example (Seattle to Los Angeles)
        Assert.Equal(1_546_754.35, (double)Eval("geo_distance_2points(-122.407628, 47.578557, -118.275287, 34.019056)")!, 0);
        Assert.Equal(Degree, (double)Eval("geo_distance_2points(0, 0, 1, 0)")!, 2);
        Assert.Equal(111_319.49, (double)Eval("geo_distance_2points(0, 0, 1, 0, true)")!, 1); // WGS-84 equator
        Assert.Null(Eval("geo_distance_2points(0, 0, 1, 95)"));                              // invalid latitude
    }

    [Fact]
    public void AzimuthAngleAndCircle()
    {
        Assert.Equal(Math.PI / 2, (double)Eval("geo_azimuth(0, 0, 1, 0)")!, 9);   // due east
        Assert.Equal(0, (double)Eval("geo_azimuth(0, 0, 0, 1)")!, 9);             // due north
        Assert.Equal(Math.PI / 2, (double)Eval("geo_angle(0, 1, 0, 0, 1, 0)")!, 9); // north line to east line, clockwise
        Assert.Equal(true, Eval("geo_point_in_circle(0, 0.5, 0, 0, 60000)"));
        Assert.Equal(false, Eval("geo_point_in_circle(0, 0.6, 0, 0, 60000)"));
    }

    [Fact]
    public void PointInPolygon()
    {
        Assert.Equal(true, Eval($"geo_point_in_polygon(0, 0, {Square})"));
        Assert.Equal(false, Eval($"geo_point_in_polygon(3, 0, {Square})"));
        Assert.Equal(false, Eval($"geo_point_in_polygon(0, 0, {SquareWithHole})")); // in the hole
        Assert.Equal(true, Eval($"geo_point_in_polygon(1.5, 0, {SquareWithHole})"));
        Assert.Null(Eval($"geo_point_in_polygon(0, 0, {Equator})"));                 // not a polygon
    }

    [Fact]
    public void Distances()
    {
        Assert.Equal(Degree, (double)Eval($"geo_distance_point_to_line(1, 1, {Equator})")!, 0);
        Assert.Equal(Degree, (double)Eval($"geo_distance_point_to_line(3, 0, {Equator})")!, 0); // past the end
        Assert.Equal(0.0, Eval($"geo_distance_point_to_polygon(0, 0, {Square})"));
        Assert.Equal(Degree, (double)Eval($"geo_distance_point_to_polygon(2, 0, {Square})")!, 0);
        var (lng, lat) = Coordinates(Eval($"geo_closest_point_on_line(1, 1, {Equator})"));
        Assert.Equal(1, lng, 9);
        Assert.Equal(0, lat, 9);
        Assert.Equal((0.0, 0.0), Coordinates(Eval($"geo_closest_point_on_polygon(0, 0, {Square})")));
    }

    [Fact]
    public void Measures()
    {
        Assert.Equal(2 * Degree, (double)Eval($"geo_line_length({Equator})")!, 1);
        Assert.InRange((double)Eval($"geo_polygon_perimeter({Square})")! / (8 * Degree), 0.999, 1.001);
        // a 2°x2° square around the origin is ~ (2·Degree)² m²
        var area = (double)Eval($"geo_polygon_area({Square})")!;
        Assert.InRange(area / Math.Pow(2 * Degree, 2), 0.999, 1.001);
        var ring = (double)Eval($"geo_polygon_area({SquareWithHole})")!;
        Assert.InRange(ring / area, 2.99, 3.01);

        Assert.Equal((1.0, 0.0), Round(Coordinates(Eval($"geo_line_centroid({Equator})"))));
        Assert.Equal((0.0, 0.0), Round(Coordinates(Eval($"geo_polygon_centroid({Square})"))));
    }

    private static (double, double) Round((double Lng, double Lat) p) => (Math.Round(p.Lng, 9) + 0.0, Math.Round(p.Lat, 9) + 0.0);

    [Fact]
    public void InterpolateAndLocate()
    {
        Assert.Equal((1.0, 0.0), Round(Coordinates(Eval($"geo_line_interpolate_point({Equator}, 0.5)"))));
        Assert.Equal(0.25, (double)Eval($"geo_line_locate_point({Equator}, 0.5, 0.2)")!, 6);
    }

    [Fact]
    public void DensifyAndSimplify()
    {
        // a long east-west geodesic bows towards the pole, so straight lng/lat chords need intermediate points
        var dense = (Dictionary<string, object?>)Eval("""geo_line_densify(dynamic({"type":"LineString","coordinates":[[-60,40],[60,40]]}), 1000)""")!;
        var points = (List<object?>)dense["coordinates"]!;
        Assert.True(points.Count > 10);
        Assert.True(Convert.ToDouble(((List<object?>)points[points.Count / 2]!)[1]) > 50);

        var simple = (Dictionary<string, object?>)Eval("""geo_line_simplify(dynamic({"type":"LineString","coordinates":[[0,0],[1,0.00001],[2,0],[3,1]]}), 10)""")!;
        Assert.Equal(3, ((List<object?>)simple["coordinates"]!).Count);
    }

    [Fact]
    public void Intersections()
    {
        const string cross = """dynamic({"type":"LineString","coordinates":[[1,-1],[1,1]]})""";
        Assert.Equal(true, Eval($"geo_intersects_2lines({Equator}, {cross})"));
        const string farLine = """dynamic({"type":"LineString","coordinates":[[5,-1],[5,1]]})""";
        const string farPolygon = """dynamic({"type":"Polygon","coordinates":[[[5,5],[6,5],[6,6],[5,5]]]})""";
        Assert.Equal(false, Eval($"geo_intersects_2lines({Equator}, {farLine})"));
        Assert.Equal(true, Eval($"geo_intersects_line_with_polygon({Equator}, {Square})"));
        Assert.Equal(true, Eval($"geo_intersects_2polygons({Square}, {SquareWithHole})"));
        Assert.Equal(false, Eval($"geo_intersects_2polygons({Square}, {farPolygon})"));
        var (lng, lat) = Coordinates(Eval($"geo_intersection_2lines({Equator}, {cross})"));
        Assert.Equal(1, lng, 9);
        Assert.Equal(0, lat, 9);
    }

    [Fact]
    public void FromWkt()
    {
        var polygon = (Dictionary<string, object?>)Eval("geo_from_wkt('POLYGON((0 0, 1 0, 1 1, 0 0))')")!;
        Assert.Equal("Polygon", polygon["type"]);
        Assert.Equal(4, ((List<object?>)((List<object?>)polygon["coordinates"]!)[0]!).Count);
        var multi = (Dictionary<string, object?>)Eval("geo_from_wkt('MULTIPOINT((1 2), (3 4))')")!;
        Assert.Equal(2, ((List<object?>)multi["coordinates"]!).Count);
        Assert.Equal(true, Eval("geo_point_in_polygon(0.5, 0.25, geo_from_wkt('POLYGON((0 0, 1 0, 1 1, 0 1, 0 0))'))"));
        Assert.Null(Eval("geo_from_wkt('POLYGON((0 0, 1 0')"));
    }

    [Fact]
    public void Geohash()
    {
        Assert.Equal("ezs42", Eval("geo_point_to_geohash(-5.6, 42.6)"));
        Assert.Equal("dhwfz15h", Eval("geo_point_to_geohash(-80.195829, 25.802215, 8)"));
        var (lng, lat) = Coordinates(Eval("geo_geohash_to_central_point('ezs42')"));
        Assert.Equal(-5.6, lng, 1);
        Assert.Equal(42.6, lat, 1);
        Assert.Equal(true, Eval("geo_point_in_polygon(-5.6, 42.6, geo_geohash_to_polygon('ezs42'))"));
        var neighbors = (List<object?>)Eval("geo_geohash_neighbors('ezs42')")!;
        Assert.Equal(8, neighbors.Count);
        Assert.Contains("ezs43", neighbors);
        Assert.Equal("", Eval("geo_point_to_geohash(0, 100)"));
        Assert.Null(Eval("geo_geohash_to_central_point('a')"));   // 'a' isn't a geohash character
    }

    [Fact]
    public void S2Cells()
    {
        var token = (string)Eval("geo_point_to_s2cell(-80.195829, 25.802215, 8)")!;
        Assert.Equal("88d9b", token);
        Assert.Equal(true, Eval($"geo_point_in_polygon(-80.195829, 25.802215, geo_s2cell_to_polygon('{token}'))"));
        Assert.Equal(token, Eval($"geo_point_to_s2cell(toreal(geo_s2cell_to_central_point('{token}').coordinates[0]), toreal(geo_s2cell_to_central_point('{token}').coordinates[1]), 8)"));

        var neighbors = (List<object?>)Eval($"geo_s2cell_neighbors('{token}')")!;
        Assert.Equal(8, neighbors.Count);
        Assert.All(neighbors, n => Assert.Contains(token, (List<object?>)Eval($"geo_s2cell_neighbors('{n}')")!));

        // every level-30 leaf is in its level-8 parent
        var leaf = (string)Eval("geo_point_to_s2cell(-80.195829, 25.802215, 30)")!;
        Assert.Equal(16, leaf.Length);
        Assert.Null(Eval("geo_s2cell_to_central_point('zz')"));
    }

    [Fact]
    public void S2Coverings()
    {
        var cells = (List<object?>)Eval($"geo_polygon_to_s2cells({Square}, 5)")!;
        Assert.Contains(Eval("geo_point_to_s2cell(0, 0, 5)"), cells);
        Assert.Contains(Eval("geo_point_to_s2cell(0.9, -0.9, 5)"), cells);
        Assert.DoesNotContain(Eval("geo_point_to_s2cell(3, 3, 5)"), cells);

        var line = (List<object?>)Eval($"geo_line_to_s2cells({Equator}, 8)")!;
        Assert.Contains(Eval("geo_point_to_s2cell(1, 0, 8)"), line);
        Assert.DoesNotContain(Eval("geo_point_to_s2cell(1, 1, 8)"), line);
        var wider = (List<object?>)Eval($"geo_line_to_s2cells({Equator}, 8, 150000)")!;
        Assert.Contains(Eval("geo_point_to_s2cell(1, 1, 8)"), wider);
    }

    [Fact]
    public void WorksOverRows()
    {
        var r = _kql.Execute("""
            datatable(name:string, lng:real, lat:real)
            [
                "inside", 0.5, 0.5,
                "outside", 5.0, 5.0,
            ]
            | where geo_point_in_polygon(lng, lat, dynamic({"type":"Polygon","coordinates":[[[-1,-1],[1,-1],[1,1],[-1,1],[-1,-1]]]}))
            | extend hash = geo_point_to_geohash(lng, lat, 6)
            """);
        Assert.Single(r.Rows);
        Assert.Equal("inside", r[0, "name"]);
        Assert.Equal(6, ((string)r[0, "hash"]!).Length);
    }
}
