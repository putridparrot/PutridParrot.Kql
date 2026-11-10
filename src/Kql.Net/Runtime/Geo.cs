using System.Globalization;
using System.Text;

namespace Kql.Net.Runtime;

/// <summary>A point on the unit sphere (or any 3D vector).</summary>
internal readonly record struct Vec3(double X, double Y, double Z)
{
    public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vec3 operator *(Vec3 a, double k) => new(a.X * k, a.Y * k, a.Z * k);
    public double Dot(Vec3 b) => X * b.X + Y * b.Y + Z * b.Z;
    public Vec3 Cross(Vec3 b) => new(Y * b.Z - Z * b.Y, Z * b.X - X * b.Z, X * b.Y - Y * b.X);
    public double Norm => Math.Sqrt(Dot(this));
    public Vec3 Normalize() => Norm is var n && n == 0 ? this : this * (1 / n);

    /// <summary>The angle between two vectors, in radians.</summary>
    public double Angle(Vec3 b) => Math.Atan2(Cross(b).Norm, Dot(b));
}

/// <summary>A longitude/latitude pair in degrees.</summary>
internal readonly record struct LngLat(double Lng, double Lat)
{
    public Vec3 ToVec()
    {
        var lng = Lng * Math.PI / 180;
        var lat = Lat * Math.PI / 180;
        return new Vec3(Math.Cos(lat) * Math.Cos(lng), Math.Cos(lat) * Math.Sin(lng), Math.Sin(lat));
    }

    public static LngLat FromVec(Vec3 v)
    {
        var n = v.Normalize();
        return new LngLat(Math.Atan2(n.Y, n.X) * 180 / Math.PI, Math.Atan2(n.Z, Math.Sqrt(n.X * n.X + n.Y * n.Y)) * 180 / Math.PI);
    }

    public bool IsValid => Lng is >= -180 and <= 180 && Lat is >= -90 and <= 90;
}

/// <summary>
/// A GeoJSON geometry read from a dynamic value: Point, MultiPoint, LineString, MultiLineString, Polygon or
/// MultiPolygon (a GeometryCollection contributes all of its members). Edges are geodesics on a sphere.
/// </summary>
internal sealed class Geometry
{
    public List<LngLat> Points { get; } = [];
    public List<List<LngLat>> Lines { get; } = [];

    /// <summary>Each polygon is a list of rings: the shell first, then holes. Rings are closed (first == last).</summary>
    public List<List<List<LngLat>>> Polygons { get; } = [];

    public IEnumerable<(Vec3 A, Vec3 B)> LineSegments() => Lines.SelectMany(Geo.Segments);

    public IEnumerable<(Vec3 A, Vec3 B)> PolygonEdges() => Polygons.SelectMany(p => p).SelectMany(Geo.Segments);

    /// <summary>Reads a GeoJSON geometry; null when the value isn't one or has invalid coordinates.</summary>
    public static Geometry? Parse(object? value)
    {
        var v = Dynamic.Normalize(value);
        if (v is string s) v = Dynamic.Parse(s);
        var g = new Geometry();
        return v is Dictionary<string, object?> bag && g.Add(bag) ? g : null;
    }

    private bool Add(Dictionary<string, object?> bag)
    {
        var type = bag.FirstOrDefault(kv => kv.Key.Equals("type", StringComparison.OrdinalIgnoreCase)).Value as string;
        var coords = bag.FirstOrDefault(kv => kv.Key.Equals("coordinates", StringComparison.OrdinalIgnoreCase)).Value;
        switch (type?.ToLowerInvariant())
        {
            case "point":
                return Point(coords) is { } p && Add(Points, p);
            case "multipoint":
                return All(coords, c => Point(c) is { } p && Add(Points, p));
            case "linestring":
                return Line(coords) is { } l && Add(Lines, l);
            case "multilinestring":
                return All(coords, c => Line(c) is { } l && Add(Lines, l));
            case "polygon":
                return Polygon(coords) is { } poly && Add(Polygons, poly);
            case "multipolygon":
                return All(coords, c => Polygon(c) is { } poly && Add(Polygons, poly));
            case "geometrycollection":
            {
                var members = bag.FirstOrDefault(kv => kv.Key.Equals("geometries", StringComparison.OrdinalIgnoreCase)).Value;
                return All(members, m => m is Dictionary<string, object?> child && Add(child));
            }
            default:
                return false;
        }
    }

    private static bool Add<T>(List<T> list, T item)
    {
        list.Add(item);
        return true;
    }

    private static bool All(object? array, Func<object?, bool> add) =>
        array is List<object?> items && items.All(add);

    private static LngLat? Point(object? c) =>
        c is List<object?> { Count: >= 2 } xy && KqlConvert.ToDouble(xy[0]) is { } lng && KqlConvert.ToDouble(xy[1]) is { } lat
            && new LngLat(lng, lat) is { IsValid: true } p ? p : null;

    private static List<LngLat>? Line(object? c)
    {
        if (c is not List<object?> { Count: >= 2 } items) return null;
        var points = items.Select(Point).ToList();
        return points.All(p => p.HasValue) ? points.Select(p => p!.Value).ToList() : null;
    }

    private static List<List<LngLat>>? Polygon(object? c)
    {
        if (c is not List<object?> { Count: >= 1 } rings) return null;
        var result = new List<List<LngLat>>();
        foreach (var r in rings)
        {
            if (Line(r) is not { Count: >= 4 } ring || ring[0] != ring[^1]) return null;
            result.Add(ring);
        }
        return result;
    }
}

/// <summary>Spherical geometry helpers. Distances are in metres on a sphere with the Earth's mean radius.</summary>
internal static class Geo
{
    public const double EarthRadius = 6371008.8;

    public static IEnumerable<(Vec3 A, Vec3 B)> Segments(List<LngLat> line)
    {
        for (var i = 0; i + 1 < line.Count; i++) yield return (line[i].ToVec(), line[i + 1].ToVec());
    }

    public static double Distance(LngLat a, LngLat b) => a.ToVec().Angle(b.ToVec()) * EarthRadius;

    /// <summary>Initial bearing from a to b, clockwise from north, in radians [0, 2π).</summary>
    public static double Azimuth(LngLat a, LngLat b)
    {
        var φ1 = a.Lat * Math.PI / 180;
        var φ2 = b.Lat * Math.PI / 180;
        var Δλ = (b.Lng - a.Lng) * Math.PI / 180;
        var θ = Math.Atan2(Math.Sin(Δλ) * Math.Cos(φ2), Math.Cos(φ1) * Math.Sin(φ2) - Math.Sin(φ1) * Math.Cos(φ2) * Math.Cos(Δλ));
        return (θ + 2 * Math.PI) % (2 * Math.PI);
    }

    /// <summary>Spherical linear interpolation between unit vectors.</summary>
    public static Vec3 Interpolate(Vec3 a, Vec3 b, double t)
    {
        var ω = a.Angle(b);
        if (ω < 1e-15) return a;
        var s = Math.Sin(ω);
        return (a * (Math.Sin((1 - t) * ω) / s) + b * (Math.Sin(t * ω) / s)).Normalize();
    }

    /// <summary>The angle from p to the nearest point of the minor arc a-b, and that point.</summary>
    public static double ToSegment(Vec3 p, Vec3 a, Vec3 b, out Vec3 closest)
    {
        var n = a.Cross(b);
        if (n.Norm > 1e-15)
        {
            n = n.Normalize();
            var projected = (p - n * p.Dot(n)).Normalize();
            if (projected.Norm > 0 && OnArc(projected, a, b, n))
            {
                closest = projected;
                return p.Angle(projected);
            }
        }
        var da = p.Angle(a);
        var db = p.Angle(b);
        closest = da <= db ? a : b;
        return Math.Min(da, db);
    }

    /// <summary>Whether x (on the great circle with normal n) lies within the minor arc a-b.</summary>
    private static bool OnArc(Vec3 x, Vec3 a, Vec3 b, Vec3 n) =>
        a.Cross(x).Dot(n) >= -1e-12 && x.Cross(b).Dot(n) >= -1e-12;

    /// <summary>Whether the minor arcs a-b and c-d share a point.</summary>
    public static bool SegmentsIntersect(Vec3 a, Vec3 b, Vec3 c, Vec3 d)
    {
        var n1 = a.Cross(b);
        var n2 = c.Cross(d);
        var x = n1.Cross(n2);
        if (x.Norm < 1e-12)
        {
            // same great circle (or degenerate): overlapping if an endpoint of one lies on the other
            const double eps = 1e-9;
            return ToSegment(a, c, d, out _) < eps || ToSegment(b, c, d, out _) < eps
                || ToSegment(c, a, b, out _) < eps || ToSegment(d, a, b, out _) < eps;
        }
        n1 = n1.Normalize();
        n2 = n2.Normalize();
        x = x.Normalize();
        return (OnArc(x, a, b, n1) && OnArc(x, c, d, n2)) || (OnArc(x * -1, a, b, n1) && OnArc(x * -1, c, d, n2));
    }

    /// <summary>The intersection points of two minor arcs (none when they don't cross or are collinear).</summary>
    public static Vec3? Crossing(Vec3 a, Vec3 b, Vec3 c, Vec3 d)
    {
        var n1 = a.Cross(b);
        var n2 = c.Cross(d);
        var x = n1.Cross(n2);
        if (x.Norm < 1e-12) return null;
        n1 = n1.Normalize();
        n2 = n2.Normalize();
        x = x.Normalize();
        if (OnArc(x, a, b, n1) && OnArc(x, c, d, n2)) return x;
        if (OnArc(x * -1, a, b, n1) && OnArc(x * -1, c, d, n2)) return x * -1;
        return null;
    }

    /// <summary>
    /// The winding angle of the ring around p (≈ ±2π inside, ≈ 0 outside). A ring encloses the smaller of the two
    /// regions it splits the sphere into, whichever way it is ordered.
    /// </summary>
    public static bool InRing(Vec3 p, List<LngLat> ring)
    {
        double sum = 0;
        for (var i = 0; i + 1 < ring.Count; i++)
        {
            var a = ring[i].ToVec();
            var b = ring[i + 1].ToVec();
            if (ToSegment(p, a, b, out _) < 1e-12) return true; // on the boundary
            sum += Math.Atan2(p.Dot(a.Cross(b)), a.Dot(b) - a.Dot(p) * b.Dot(p));
        }
        return Math.Abs(sum) > Math.PI;
    }

    public static bool InPolygon(Vec3 p, List<List<LngLat>> polygon) =>
        InRing(p, polygon[0]) && !polygon.Skip(1).Any(hole => InRing(p, hole));

    public static bool Contains(Geometry g, Vec3 p) => g.Polygons.Any(poly => InPolygon(p, poly));

    /// <summary>Area of the region a ring encloses (the smaller side), in steradians.</summary>
    public static double RingArea(List<LngLat> ring)
    {
        double sum = 0;
        for (var i = 0; i + 1 < ring.Count; i++)
        {
            var λ1 = ring[i].Lng * Math.PI / 180;
            var λ2 = ring[i + 1].Lng * Math.PI / 180;
            var Δ = λ2 - λ1;
            if (Δ > Math.PI) Δ -= 2 * Math.PI;
            if (Δ < -Math.PI) Δ += 2 * Math.PI;
            sum += Δ * (2 + Math.Sin(ring[i].Lat * Math.PI / 180) + Math.Sin(ring[i + 1].Lat * Math.PI / 180));
        }
        var area = Math.Abs(sum) / 2;
        return Math.Min(area, 4 * Math.PI - area);
    }

    /// <summary>
    /// ∫ (unit position) dA over the region a ring encloses: ½ Σ angle(a, b) · unit(a × b), oriented to point into the
    /// ring's (smaller) region. Its direction is the ring's centroid; its length grows with the area.
    /// </summary>
    public static Vec3 RingMoment(List<LngLat> ring)
    {
        var m = new Vec3(0, 0, 0);
        var mean = new Vec3(0, 0, 0);
        for (var i = 0; i + 1 < ring.Count; i++)
        {
            var a = ring[i].ToVec();
            var b = ring[i + 1].ToVec();
            mean += a;
            var n = a.Cross(b);
            if (n.Norm > 0) m += n.Normalize() * (a.Angle(b) / 2);
        }
        return m.Dot(mean) < 0 ? m * -1 : m;
    }

    public static double MinDistance(Geometry g, Vec3 p, out Vec3 closest)
    {
        var best = double.MaxValue;
        closest = p;
        foreach (var q in g.Points)
        {
            var v = q.ToVec();
            if (p.Angle(v) < best) (best, closest) = (p.Angle(v), v);
        }
        foreach (var (a, b) in g.LineSegments().Concat(g.PolygonEdges()))
        {
            var d = ToSegment(p, a, b, out var c);
            if (d < best) (best, closest) = (d, c);
        }
        return best;
    }

    /// <summary>All segments of a geometry's lines and polygon rings.</summary>
    public static IEnumerable<(Vec3 A, Vec3 B)> AllSegments(Geometry g) => g.LineSegments().Concat(g.PolygonEdges());

    public static IEnumerable<Vec3> AllVertices(Geometry g) =>
        g.Points.Concat(g.Lines.SelectMany(l => l)).Concat(g.Polygons.SelectMany(p => p).SelectMany(r => r)).Select(p => p.ToVec());

    /// <summary>Whether two geometries share any point.</summary>
    public static bool Intersects(Geometry x, Geometry y)
    {
        var sx = AllSegments(x).ToList();
        var sy = AllSegments(y).ToList();
        if (sx.Any(a => sy.Any(b => SegmentsIntersect(a.A, a.B, b.A, b.B)))) return true;
        if (AllVertices(x).Any(v => Contains(y, v) || OnAny(v, sy) || y.Points.Any(p => p.ToVec().Angle(v) < 1e-12))) return true;
        return AllVertices(y).Any(v => Contains(x, v) || OnAny(v, sx));
    }

    private static bool OnAny(Vec3 v, List<(Vec3 A, Vec3 B)> segments) =>
        segments.Any(s => ToSegment(v, s.A, s.B, out _) < 1e-12);

    /// <summary>The shortest distance (radians) between two geometries; 0 when they intersect.</summary>
    public static double Distance(Geometry x, Geometry y)
    {
        if (Intersects(x, y)) return 0;
        var dx = AllVertices(x).Select(v => MinDistance(y, v, out _));
        var dy = AllVertices(y).Select(v => MinDistance(x, v, out _));
        return dx.Concat(dy).DefaultIfEmpty(double.MaxValue).Min();
    }

    // ------------------------------------------------------------------ GeoJSON output

    private static List<object?> Coord(LngLat p) => [p.Lng, p.Lat];

    public static Dictionary<string, object?> Point(LngLat p) => new() { ["type"] = "Point", ["coordinates"] = Coord(p) };

    public static Dictionary<string, object?> LineString(IEnumerable<LngLat> points) =>
        new() { ["type"] = "LineString", ["coordinates"] = points.Select(p => (object?)Coord(p)).ToList() };

    public static List<object?> Rings(IEnumerable<IEnumerable<LngLat>> rings) =>
        rings.Select(r => (object?)r.Select(p => (object?)Coord(p)).ToList()).ToList();

    public static Dictionary<string, object?> Polygon(IEnumerable<IEnumerable<LngLat>> rings) =>
        new() { ["type"] = "Polygon", ["coordinates"] = Rings(rings) };

    public static Dictionary<string, object?> MultiPolygon(IEnumerable<IEnumerable<IEnumerable<LngLat>>> polygons) =>
        new() { ["type"] = "MultiPolygon", ["coordinates"] = polygons.Select(p => (object?)Rings(p)).ToList() };

    public static Dictionary<string, object?> MultiLineString(IEnumerable<IEnumerable<LngLat>> lines) =>
        new() { ["type"] = "MultiLineString", ["coordinates"] = Rings(lines) };

    // ------------------------------------------------------------------ WKT

    /// <summary>Converts Well-Known Text to a GeoJSON dynamic value (null if invalid).</summary>
    public static object? FromWkt(string? wkt)
    {
        if (string.IsNullOrWhiteSpace(wkt)) return null;
        try
        {
            var reader = new WktReader(wkt);
            var result = reader.Geometry();
            reader.End();
            return result;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private sealed class WktReader(string text)
    {
        private int _pos;

        public void End()
        {
            Skip();
            if (_pos != text.Length) throw new FormatException();
        }

        private void Skip()
        {
            while (_pos < text.Length && char.IsWhiteSpace(text[_pos])) _pos++;
        }

        private bool Peek(char c)
        {
            Skip();
            return _pos < text.Length && text[_pos] == c;
        }

        private void Expect(char c)
        {
            if (!Peek(c)) throw new FormatException();
            _pos++;
        }

        private string Word()
        {
            Skip();
            var start = _pos;
            while (_pos < text.Length && char.IsLetter(text[_pos])) _pos++;
            if (start == _pos) throw new FormatException();
            return text[start.._pos].ToUpperInvariant();
        }

        private double Number()
        {
            Skip();
            var start = _pos;
            while (_pos < text.Length && (char.IsDigit(text[_pos]) || text[_pos] is '-' or '+' or '.' or 'e' or 'E')) _pos++;
            return double.TryParse(text[start.._pos], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : throw new FormatException();
        }

        private List<object?> Position()
        {
            var lng = Number();
            var lat = Number();
            while (!Peek(',') && !Peek(')')) Number(); // ignore Z / M
            if (!new LngLat(lng, lat).IsValid) throw new FormatException();
            return [lng, lat];
        }

        private List<object?> List(Func<object?> item)
        {
            Expect('(');
            var items = new List<object?> { item() };
            while (Peek(','))
            {
                _pos++;
                items.Add(item());
            }
            Expect(')');
            return items;
        }

        private List<object?> Positions() => List(Position);

        private List<object?> PointsList() => List(() =>
        {
            if (!Peek('(')) return Position();
            Expect('(');
            var p = Position();
            Expect(')');
            return p;
        });

        public Dictionary<string, object?> Geometry()
        {
            var kind = Word();
            Skip();
            if (_pos + 5 <= text.Length && text.Substring(_pos, 5).Equals("EMPTY", StringComparison.OrdinalIgnoreCase))
                throw new FormatException();
            if (kind is "Z" or "M" or "ZM") kind = Word();
            return kind switch
            {
                "POINT" => Build("Point", List(Position)[0]),
                "LINESTRING" => Build("LineString", Positions()),
                "POLYGON" => Build("Polygon", List(Positions)),
                "MULTIPOINT" => Build("MultiPoint", PointsList()),
                "MULTILINESTRING" => Build("MultiLineString", List(Positions)),
                "MULTIPOLYGON" => Build("MultiPolygon", List(() => List(Positions))),
                "GEOMETRYCOLLECTION" => new Dictionary<string, object?> { ["type"] = "GeometryCollection", ["geometries"] = List(Geometry) },
                _ => throw new FormatException()
            };
        }

        private static Dictionary<string, object?> Build(string type, object? coordinates) =>
            new() { ["type"] = type, ["coordinates"] = coordinates };
    }
}

/// <summary>Geohash encoding (base 32, interleaved longitude/latitude bits).</summary>
internal static class Geohash
{
    private const string Alphabet = "0123456789bcdefghjkmnpqrstuvwxyz";

    public static string Encode(double lng, double lat, int precision)
    {
        double lngLo = -180, lngHi = 180, latLo = -90, latHi = 90;
        var sb = new StringBuilder(precision);
        var even = true;
        int bit = 0, ch = 0;
        while (sb.Length < precision)
        {
            if (even)
            {
                var mid = (lngLo + lngHi) / 2;
                if (lng >= mid) { ch = (ch << 1) | 1; lngLo = mid; }
                else { ch <<= 1; lngHi = mid; }
            }
            else
            {
                var mid = (latLo + latHi) / 2;
                if (lat >= mid) { ch = (ch << 1) | 1; latLo = mid; }
                else { ch <<= 1; latHi = mid; }
            }
            even = !even;
            if (++bit == 5)
            {
                sb.Append(Alphabet[ch]);
                bit = 0;
                ch = 0;
            }
        }
        return sb.ToString();
    }

    /// <summary>The cell's bounds (west, south, east, north), or null for an invalid geohash.</summary>
    public static (double W, double S, double E, double N)? Decode(string? hash)
    {
        if (string.IsNullOrEmpty(hash) || hash.Length > 18) return null;
        double lngLo = -180, lngHi = 180, latLo = -90, latHi = 90;
        var even = true;
        foreach (var c in hash.ToLowerInvariant())
        {
            var idx = Alphabet.IndexOf(c);
            if (idx < 0) return null;
            for (var b = 4; b >= 0; b--)
            {
                var on = ((idx >> b) & 1) == 1;
                if (even)
                {
                    var mid = (lngLo + lngHi) / 2;
                    if (on) lngLo = mid; else lngHi = mid;
                }
                else
                {
                    var mid = (latLo + latHi) / 2;
                    if (on) latLo = mid; else latHi = mid;
                }
                even = !even;
            }
        }
        return (lngLo, latLo, lngHi, latHi);
    }
}

/// <summary>S2 cells: the quadratic cube projection with Hilbert-curve cell ids, as in the S2 geometry library.</summary>
internal static class S2
{
    public const int MaxLevel = 30;
    private const int MaxSize = 1 << MaxLevel;
    private const int LookupBits = 4;
    private const int SwapMask = 1;
    private const int InvertMask = 2;
    private static readonly int[] LookupPos = new int[1 << (2 * LookupBits + 2)];
    private static readonly int[] LookupIJ = new int[1 << (2 * LookupBits + 2)];
    private static readonly int[][] PosToIJ = [[0, 1, 3, 2], [0, 2, 3, 1], [3, 2, 0, 1], [3, 1, 0, 2]];
    private static readonly int[] PosToOrientation = [SwapMask, 0, 0, InvertMask | SwapMask];

    static S2()
    {
        InitLookup(0, 0, 0, 0, 0, 0);
        InitLookup(0, 0, 0, SwapMask, 0, SwapMask);
        InitLookup(0, 0, 0, InvertMask, 0, InvertMask);
        InitLookup(0, 0, 0, SwapMask | InvertMask, 0, SwapMask | InvertMask);
    }

    private static void InitLookup(int level, int i, int j, int origOrientation, int pos, int orientation)
    {
        if (level == LookupBits)
        {
            var ij = (i << LookupBits) + j;
            LookupPos[(ij << 2) + origOrientation] = (pos << 2) + orientation;
            LookupIJ[(pos << 2) + origOrientation] = (ij << 2) + orientation;
            return;
        }
        level++;
        i <<= 1;
        j <<= 1;
        pos <<= 2;
        var r = PosToIJ[orientation];
        for (var k = 0; k < 4; k++)
            InitLookup(level, i + (r[k] >> 1), j + (r[k] & 1), origOrientation, pos + k, orientation ^ PosToOrientation[k]);
    }

    // ---------------------------------------------------------------- projections

    private static (int Face, double U, double V) XyzToFaceUV(Vec3 p)
    {
        var ax = Math.Abs(p.X);
        var ay = Math.Abs(p.Y);
        var az = Math.Abs(p.Z);
        var face = ax >= ay && ax >= az ? 0 : ay >= az ? 1 : 2;
        var component = face switch { 0 => p.X, 1 => p.Y, _ => p.Z };
        if (component < 0) face += 3;
        return face switch
        {
            0 => (0, p.Y / p.X, p.Z / p.X),
            1 => (1, -p.X / p.Y, p.Z / p.Y),
            2 => (2, -p.X / p.Z, -p.Y / p.Z),
            3 => (3, p.Z / p.X, p.Y / p.X),
            4 => (4, p.Z / p.Y, -p.X / p.Y),
            _ => (5, -p.Y / p.Z, -p.X / p.Z)
        };
    }

    private static Vec3 FaceUVToXyz(int face, double u, double v) => face switch
    {
        0 => new Vec3(1, u, v),
        1 => new Vec3(-u, 1, v),
        2 => new Vec3(-u, -v, 1),
        3 => new Vec3(-1, -v, -u),
        4 => new Vec3(v, -1, -u),
        _ => new Vec3(v, u, -1)
    };

    private static double UVToST(double u) => u >= 0 ? 0.5 * Math.Sqrt(1 + 3 * u) : 1 - 0.5 * Math.Sqrt(1 - 3 * u);

    private static double STToUV(double s) => s >= 0.5 ? (4 * s * s - 1) / 3 : (1 - 4 * (1 - s) * (1 - s)) / 3;

    private static int STToIJ(double s) => (int)Math.Clamp(Math.Floor(MaxSize * s), 0, MaxSize - 1);

    // ---------------------------------------------------------------- cell ids

    private static ulong FromFaceIJ(int face, int i, int j)
    {
        var n = (ulong)face << 60;
        var bits = face & SwapMask;
        const int mask = (1 << LookupBits) - 1;
        for (var k = 7; k >= 0; k--)
        {
            bits += ((i >> (k * LookupBits)) & mask) << (LookupBits + 2);
            bits += ((j >> (k * LookupBits)) & mask) << 2;
            bits = LookupPos[bits];
            n |= (ulong)(bits >> 2) << (k * 2 * LookupBits);
            bits &= SwapMask | InvertMask;
        }
        return n * 2 + 1;
    }

    private static (int Face, int I, int J) ToFaceIJ(ulong id)
    {
        var face = (int)(id >> 61);
        var orientation = face & SwapMask;
        int i = 0, j = 0;
        var nbits = MaxLevel - 7 * LookupBits;
        for (var k = 7; k >= 0; k--)
        {
            orientation += (int)((id >> (k * 2 * LookupBits + 1)) & (ulong)((1 << (2 * nbits)) - 1)) << 2;
            orientation = LookupIJ[orientation];
            i += (orientation >> (LookupBits + 2)) << (k * LookupBits);
            j += ((orientation >> 2) & ((1 << LookupBits) - 1)) << (k * LookupBits);
            orientation &= SwapMask | InvertMask;
            nbits = LookupBits;
        }
        return (face, i, j);
    }

    private static ulong Lsb(int level) => 1UL << (2 * (MaxLevel - level));

    public static int Level(ulong id) => MaxLevel - System.Numerics.BitOperations.TrailingZeroCount(id) / 2;

    public static ulong Parent(ulong id, int level)
    {
        var lsb = Lsb(level);
        return (id & (~lsb + 1)) | lsb;
    }

    public static IEnumerable<ulong> Children(ulong id)
    {
        var lsb = id & (~id + 1);
        var child = lsb >> 2;
        var start = id - lsb + child;
        for (var k = 0UL; k < 4; k++) yield return start + k * 2 * child;
    }

    public static IEnumerable<ulong> FaceCells() => Enumerable.Range(0, 6).Select(f => ((ulong)f << 61) | (1UL << 60));

    public static ulong FromPoint(LngLat p, int level)
    {
        var (face, u, v) = XyzToFaceUV(p.ToVec());
        return Parent(FromFaceIJ(face, STToIJ(UVToST(u)), STToIJ(UVToST(v))), level);
    }

    public static bool IsValid(ulong id) => id >> 61 < 6 && (id & (~id + 1) & 0x1555555555555555UL) != 0;

    public static string ToToken(ulong id) => id == 0 ? "X" : id.ToString("x16").TrimEnd('0');

    public static ulong? FromToken(string? token)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 16) return null;
        return ulong.TryParse(token.PadRight(16, '0'), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var id) && IsValid(id) ? id : null;
    }

    private static (int Face, int I0, int J0, int Size) Bounds(ulong id)
    {
        var (face, i, j) = ToFaceIJ(id);
        var size = 1 << (MaxLevel - Level(id));
        return (face, i & -size, j & -size, size);
    }

    private static LngLat FaceIJToPoint(int face, double i, double j) =>
        LngLat.FromVec(FaceUVToXyz(face, STToUV(i / MaxSize), STToUV(j / MaxSize)));

    public static LngLat Center(ulong id)
    {
        var (face, i0, j0, size) = Bounds(id);
        return FaceIJToPoint(face, i0 + size / 2.0, j0 + size / 2.0);
    }

    /// <summary>The cell's four corners, counterclockwise, closed (first repeated last).</summary>
    public static List<LngLat> Vertices(ulong id)
    {
        var (face, i0, j0, size) = Bounds(id);
        var corners = new[] { (i0, j0), (i0 + size, j0), (i0 + size, j0 + size), (i0, j0 + size) }
            .Select(c => FaceIJToPoint(face, c.Item1, c.Item2)).ToList();
        corners.Add(corners[0]);
        return corners;
    }

    /// <summary>The cells around this one at the same level (edge and corner neighbours, 7 at a cube corner).</summary>
    public static List<ulong> Neighbors(ulong id)
    {
        var level = Level(id);
        var (face, i0, j0, size) = Bounds(id);
        var result = new List<ulong>();
        for (var di = -1; di <= 1; di++)
            for (var dj = -1; dj <= 1; dj++)
            {
                if (di == 0 && dj == 0) continue;
                var i = di < 0 ? i0 - 1 : di > 0 ? i0 + size : i0;
                var j = dj < 0 ? j0 - 1 : dj > 0 ? j0 + size : j0;
                var n = Parent(FromFaceIJWrap(face, i, j), level);
                if (n != id && !result.Contains(n)) result.Add(n);
            }
        return result;
    }

    /// <summary>The leaf cell at (i, j), which may lie just beyond the face (it wraps onto the adjacent face).</summary>
    private static ulong FromFaceIJWrap(int face, int i, int j)
    {
        if (i is >= 0 and < MaxSize && j is >= 0 and < MaxSize) return FromFaceIJ(face, i, j);
        i = Math.Clamp(i, -1, MaxSize);
        j = Math.Clamp(j, -1, MaxSize);
        const double scale = 1.0 / MaxSize;
        var limit = Math.BitIncrement(1.0);
        var u = Math.Clamp(scale * (2.0 * i + 1 - MaxSize), -limit, limit);
        var v = Math.Clamp(scale * (2.0 * j + 1 - MaxSize), -limit, limit);
        var (f, nu, nv) = XyzToFaceUV(FaceUVToXyz(face, u, v));
        return FromFaceIJ(f, STToIJ(0.5 * (nu + 1)), STToIJ(0.5 * (nv + 1)));
    }
}
