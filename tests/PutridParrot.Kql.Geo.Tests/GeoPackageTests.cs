using System.Net;
using PutridParrot.Kql.Geo;

namespace PutridParrot.Kql.Geo.Tests;

public class GeoPackageTests
{
    private readonly KqlContext _kql = new KqlContext().UseGeo(ip =>
        ip.Equals(IPAddress.Parse("20.53.203.50")) ? new GeoIpInfo("Australia", "New South Wales", "Sydney", -33.87, 151.21) : null);

    private object? Eval(string expression) => _kql.Execute("print r = " + expression).Scalar();

    private static List<object?> L(object? value) => (List<object?>)value!;

    private const double Degree = 111_195.0797; // one degree of a great circle, in metres

    private static string Square(double west, double south, double east, double north) =>
        $$"""dynamic({"type":"Polygon","coordinates":[[[{{west}},{{south}}],[{{east}},{{south}}],[{{east}},{{north}}],[{{west}},{{north}}],[{{west}},{{south}}]]]})""";

    private static readonly string Unit = Square(0, 0, 1, 1);

    private double Area(string geometry) => (double)Eval($"geo_polygon_area({geometry})")!;

    // ------------------------------------------------------------------ H3

    [Fact]
    public void PointToH3Cell()
    {
        Assert.Equal("87283472bffffff", Eval("geo_point_to_h3cell(-122.0553238, 37.3615593, 7)"));  // H3 reference example
        Assert.Equal("862a1072fffffff", Eval("geo_point_to_h3cell(-74.04450446039874, 40.689250859314974, 6)"));
        Assert.Equal(6, Eval("geo_h3cell_level(geo_point_to_h3cell(10, 20))"));                     // default resolution
        Assert.Equal("", Eval("geo_point_to_h3cell(0, 100)"));
        Assert.Equal("", Eval("geo_point_to_h3cell(0, 0, 16)"));
        Assert.Null(Eval("geo_h3cell_level('not a cell')"));
    }

    [Fact]
    public void CellGeometry()
    {
        const string cell = "'87283472bffffff'";
        var center = (List<object?>)((Dictionary<string, object?>)Eval($"geo_h3cell_to_central_point({cell})")!)["coordinates"]!;
        Assert.Equal(-122.06, (double)center[0]!, 1);
        Assert.Equal(37.36, (double)center[1]!, 1);
        var polygon = (Dictionary<string, object?>)Eval($"geo_h3cell_to_polygon({cell})")!;
        Assert.Equal(7, L(L(polygon["coordinates"])[0]).Count);  // a closed hexagon
        Assert.Equal(true, Eval("geo_point_in_polygon(-122.0553238, 37.3615593, geo_h3cell_to_polygon('87283472bffffff'))"));
    }

    [Fact]
    public void Hierarchy()
    {
        Assert.Equal("86283472fffffff", Eval("geo_h3cell_parent('87283472bffffff')"));
        Assert.Equal(5, Eval("geo_h3cell_level(geo_h3cell_parent('87283472bffffff', 5))"));
        Assert.Null(Eval("geo_h3cell_parent('87283472bffffff', 9)"));
        var children = L(Eval("geo_h3cell_children('86283472fffffff')"));
        Assert.Equal(7, children.Count);
        Assert.Contains("87283472bffffff", children);
        Assert.Equal(49, L(Eval("geo_h3cell_children('86283472fffffff', 8)")).Count);
    }

    [Fact]
    public void NeighborsAndRings()
    {
        var neighbors = L(Eval("geo_h3cell_neighbors('87283472bffffff')"));
        Assert.Equal(6, neighbors.Count);
        Assert.All(neighbors, n => Assert.Contains("87283472bffffff", L(Eval($"geo_h3cell_neighbors('{n}')"))));

        var rings = L(Eval("geo_h3cell_rings('87283472bffffff', 2)"));
        Assert.Equal([1, 6, 12], rings.Select(r => L(r).Count));
        Assert.Equal("87283472bffffff", L(rings[0])[0]);
        Assert.Equal(neighbors.Order(), L(rings[1]).Order());
    }

    [Fact]
    public void PolygonToH3Cells()
    {
        var cells = L(Eval($"geo_polygon_to_h3cells({Unit}, 4)"));
        Assert.Contains(Eval("geo_point_to_h3cell(0.5, 0.5, 4)"), cells);
        Assert.Contains(Eval("geo_point_to_h3cell(0.01, 0.99, 4)"), cells);   // a corner
        Assert.DoesNotContain(Eval("geo_point_to_h3cell(3, 3, 4)"), cells);
        // the cells together cover the square: total area at least the square's
        var cellArea = cells.Sum(c => Area($"geo_h3cell_to_polygon('{c}')"));
        Assert.True(cellArea >= Area(Unit));

        var wider = L(Eval($"geo_polygon_to_h3cells({Unit}, 4, 100000)"));
        Assert.True(wider.Count > cells.Count);
        Assert.Contains(Eval("geo_point_to_h3cell(1.5, 0.5, 4)"), wider);
    }

    // ------------------------------------------------------------------ buffers

    [Fact]
    public void PointBuffer_IsACircle()
    {
        var area = Area("geo_point_buffer(10, 50, 1000)");
        Assert.InRange(area / (Math.PI * 1000 * 1000), 0.995, 1.0);
        Assert.Equal(true, Eval("geo_point_in_polygon(10, 50.008, geo_point_buffer(10, 50, 1000))"));   // ~890 m north
        Assert.Equal(false, Eval("geo_point_in_polygon(10, 50.01, geo_point_buffer(10, 50, 1000))"));   // ~1110 m north
        Assert.Null(Eval("geo_point_buffer(10, 50, -1)"));
    }

    [Fact]
    public void LineBuffer_IsACapsule()
    {
        const string line = """dynamic({"type":"LineString","coordinates":[[0,0],[1,0]]})""";
        var area = Area($"geo_line_buffer({line}, 1000)");
        var expected = 2 * 1000 * Degree + Math.PI * 1000 * 1000;
        Assert.InRange(area / expected, 0.995, 1.005);
        Assert.Equal(true, Eval($"geo_point_in_polygon(0.5, 0.008, geo_line_buffer({line}, 1000))"));
        Assert.Equal(false, Eval($"geo_point_in_polygon(0.5, 0.01, geo_line_buffer({line}, 1000))"));
    }

    [Fact]
    public void PolygonBuffer_GrowsAndShrinks()
    {
        var area = Area(Unit);
        var perimeter = (double)Eval($"geo_polygon_perimeter({Unit})")!;
        var grown = Area($"geo_polygon_buffer({Unit}, 1000)");
        Assert.InRange(grown / (area + perimeter * 1000 + Math.PI * 1000 * 1000), 0.995, 1.005);
        var shrunk = Area($"geo_polygon_buffer({Unit}, -1000)");
        Assert.InRange(shrunk / (area - perimeter * 1000 + 4 * 1000 * 1000), 0.995, 1.005);
    }

    // ------------------------------------------------------------------ intersections and unions

    [Fact]
    public void IntersectionOfPolygons()
    {
        var overlap = $"geo_intersection_2polygons({Unit}, {Square(0.5, 0.5, 2, 2)})";
        Assert.InRange(Area(overlap) / Area(Square(0.5, 0.5, 1, 1)), 0.999, 1.001);
        var none = (Dictionary<string, object?>)Eval($"geo_intersection_2polygons({Unit}, {Square(5, 5, 6, 6)})")!;
        Assert.Equal("GeometryCollection", none["type"]);
    }

    [Fact]
    public void IntersectionOfLineWithPolygon()
    {
        const string line = """dynamic({"type":"LineString","coordinates":[[-3,0.5],[3,0.5]]})""";
        var inside = (Dictionary<string, object?>)Eval($"geo_intersection_line_with_polygon({line}, {Unit})")!;
        Assert.Equal("LineString", inside["type"]);
        Assert.InRange((double)Eval($"geo_line_length(geo_intersection_line_with_polygon({line}, {Unit}))")! / Degree, 0.99, 1.0);
    }

    [Fact]
    public void UnionOfPolygons()
    {
        var union = $"geo_union_polygons_array(pack_array({Unit}, {Square(0.5, 0.5, 1.5, 1.5)}))";
        Assert.Equal("Polygon", ((Dictionary<string, object?>)Eval(union)!)["type"]);
        var expected = 2 * Area(Unit) - Area(Square(0.5, 0.5, 1, 1));
        Assert.InRange(Area(union) / expected, 0.999, 1.001);

        var apart = (Dictionary<string, object?>)Eval($"geo_union_polygons_array(pack_array({Unit}, {Square(5, 5, 6, 6)}))")!;
        Assert.Equal("MultiPolygon", apart["type"]);
    }

    [Fact]
    public void UnionAcrossTheAntimeridian()
    {
        var union = $"geo_union_polygons_array(pack_array({Square(179, 0, 180, 1)}, {Square(-180, 0, -179, 1)}))";
        Assert.InRange(Area(union) / (2 * Area(Unit)), 0.999, 1.001);
        Assert.Equal(true, Eval($"geo_point_in_polygon(-179.5, 0.5, {union})"));
    }

    [Fact]
    public void UnionOfLines_MergesOverlaps()
    {
        const string a = """dynamic({"type":"LineString","coordinates":[[0,0],[2,0]]})""";
        const string b = """dynamic({"type":"LineString","coordinates":[[1,0],[3,0]]})""";
        var union = (Dictionary<string, object?>)Eval($"geo_union_lines_array(pack_array({a}, {b}))")!;
        Assert.Equal("LineString", union["type"]);
        Assert.InRange((double)Eval($"geo_line_length(geo_union_lines_array(pack_array({a}, {b})))")! / (3 * Degree), 0.999, 1.001);
    }

    [Fact]
    public void SimplifyPolygonsArray()
    {
        // a square with a 1 m bump on one side: the bump disappears at a 10 m tolerance
        const string bumpy = """dynamic({"type":"Polygon","coordinates":[[[0,0],[0.5,-0.000009],[1,0],[1,1],[0,1],[0,0]]]})""";
        var simple = (Dictionary<string, object?>)Eval($"geo_simplify_polygons_array(pack_array({bumpy}), 10)")!;
        Assert.Equal(5, L(L(simple["coordinates"])[0]).Count);
    }

    // ------------------------------------------------------------------ IP addresses and registration

    [Fact]
    public void InfoFromIpAddress_UsesTheLookup()
    {
        var info = (Dictionary<string, object?>)Eval("geo_info_from_ip_address('20.53.203.50')")!;
        Assert.Equal("Sydney", info["city"]);
        Assert.Equal(-33.87, info["latitude"]);
        Assert.Null(Eval("geo_info_from_ip_address('10.0.0.1')"));
        Assert.Null(Eval("geo_info_from_ip_address('not an ip')"));
    }

    [Fact]
    public void WithoutUseGeo_TheErrorNamesThePackage()
    {
        var ex = Assert.Throws<KqlNotSupportedException>(() => new KqlContext().Execute("print geo_point_to_h3cell(0, 0)"));
        Assert.Contains("UseGeo", ex.Message);
        // and without an IP lookup, geo_info_from_ip_address isn't available
        Assert.Throws<KqlNotSupportedException>(() => new KqlContext().UseGeo().Execute("print geo_info_from_ip_address('1.2.3.4')"));
    }

    [Fact]
    public void WorksOverRows()
    {
        var r = _kql.Execute("""
            datatable(name:string, lng:real, lat:real)
            [
                "a", 0.10, 0.10,
                "b", 0.11, 0.10,
                "c", 5.00, 5.00,
            ]
            | extend cell = geo_point_to_h3cell(lng, lat, 5)
            | summarize names = make_list(name) by cell
            """);
        Assert.Equal(2, r.Rows.Count);
    }
}
