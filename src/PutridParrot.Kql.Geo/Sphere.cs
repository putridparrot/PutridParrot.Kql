using NetTopologySuite.Algorithm;
using NetTopologySuite.Geometries;

namespace PutridParrot.Kql.Geo;

/// <summary>Spherical helpers. Distances are in metres on a sphere with the Earth's mean radius.</summary>
internal static class Sphere
{
    public const double EarthRadius = 6371008.8;

    public readonly record struct Vec(double X, double Y, double Z)
    {
        public static Vec operator +(Vec a, Vec b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static Vec operator *(Vec a, double k) => new(a.X * k, a.Y * k, a.Z * k);
        public double Dot(Vec b) => X * b.X + Y * b.Y + Z * b.Z;
        public Vec Cross(Vec b) => new(Y * b.Z - Z * b.Y, Z * b.X - X * b.Z, X * b.Y - Y * b.X);
        public double Norm => Math.Sqrt(Dot(this));
        public Vec Unit => this * (1 / Norm);
        public double Angle(Vec b) => Math.Atan2(Cross(b).Norm, Dot(b));
    }

    private const double Rad = Math.PI / 180;

    public static Vec ToVec(Coordinate c) =>
        new(Math.Cos(c.Y * Rad) * Math.Cos(c.X * Rad), Math.Cos(c.Y * Rad) * Math.Sin(c.X * Rad), Math.Sin(c.Y * Rad));

    public static Coordinate ToCoordinate(Vec v)
    {
        var u = v.Unit;
        return new Coordinate(Math.Atan2(u.Y, u.X) / Rad, Math.Atan2(u.Z, Math.Sqrt(u.X * u.X + u.Y * u.Y)) / Rad);
    }

    /// <summary>Initial bearing from a to b, clockwise from north, in radians.</summary>
    public static double Bearing(Coordinate a, Coordinate b)
    {
        var φ1 = a.Y * Rad;
        var φ2 = b.Y * Rad;
        var Δλ = (b.X - a.X) * Rad;
        return Math.Atan2(Math.Sin(Δλ) * Math.Cos(φ2), Math.Cos(φ1) * Math.Sin(φ2) - Math.Sin(φ1) * Math.Cos(φ2) * Math.Cos(Δλ));
    }

    /// <summary>The point reached by travelling <paramref name="distance"/> metres from p on the given bearing.</summary>
    public static Coordinate Destination(Coordinate p, double bearing, double distance)
    {
        var δ = distance / EarthRadius;
        var φ1 = p.Y * Rad;
        var λ1 = p.X * Rad;
        var φ2 = Math.Asin(Math.Sin(φ1) * Math.Cos(δ) + Math.Cos(φ1) * Math.Sin(δ) * Math.Cos(bearing));
        var λ2 = λ1 + Math.Atan2(Math.Sin(bearing) * Math.Sin(δ) * Math.Cos(φ1), Math.Cos(δ) - Math.Sin(φ1) * Math.Sin(φ2));
        var lng = (λ2 / Rad + 540) % 360 - 180;
        return new Coordinate(lng, φ2 / Rad);
    }

    public static double Distance(Coordinate a, Coordinate b) => ToVec(a).Angle(ToVec(b)) * EarthRadius;

    /// <summary>The point a fraction t of the way along the geodesic a-b.</summary>
    public static Coordinate Interpolate(Coordinate a, Coordinate b, double t)
    {
        var (va, vb) = (ToVec(a), ToVec(b));
        var ω = va.Angle(vb);
        if (ω < 1e-15) return a.Copy();
        var s = Math.Sin(ω);
        return ToCoordinate(va * (Math.Sin((1 - t) * ω) / s) + vb * (Math.Sin(t * ω) / s));
    }

    /// <summary>Number of polygon vertices so a circle of the given radius deviates by at most the tolerance (metres).</summary>
    public static int CircleVertices(double radius, double tolerance, int max)
    {
        var ratio = Math.Clamp(1 - tolerance / radius, -1, 1);
        var n = (int)Math.Ceiling(Math.PI / Math.Max(Math.Acos(ratio), 1e-9));
        return Math.Clamp(n, 16, max);
    }

    /// <summary>A geodesic circle as a closed counterclockwise ring of lng/lat coordinates.</summary>
    public static Coordinate[] Circle(Coordinate center, double radius, int vertices)
    {
        var ring = new Coordinate[vertices + 1];
        for (var k = 0; k < vertices; k++) ring[k] = Destination(center, -2 * Math.PI * k / vertices, radius); // counterclockwise
        ring[vertices] = ring[0].Copy();
        return ring;
    }
}

/// <summary>
/// The gnomonic projection about a centre point. It maps great circles to straight lines, so planar operations on
/// projected geometries (union, intersection, difference) are exact for geodesic edges. Only points within
/// <see cref="MaxAngle"/> of the centre are projected.
/// </summary>
internal sealed class Gnomonic
{
    public const double MaxAngle = 80 * Math.PI / 180;

    private readonly Sphere.Vec _c, _e, _n;

    private Gnomonic(Sphere.Vec center)
    {
        _c = center.Unit;
        var east = new Sphere.Vec(0, 0, 1).Cross(_c);
        _e = east.Norm < 1e-12 ? new Sphere.Vec(0, 1, 0) : east.Unit; // at a pole any east works
        _n = _c.Cross(_e);
    }

    /// <summary>
    /// A projection centred on the given coordinates, or null when they don't fit (some point lies further than
    /// <see cref="MaxAngle"/> less <paramref name="margin"/> metres from the centre).
    /// </summary>
    public static Gnomonic? For(IEnumerable<Coordinate> coordinates, double margin = 0)
    {
        var vs = coordinates.Select(Sphere.ToVec).ToList();
        if (vs.Count == 0) return null;
        var sum = vs.Aggregate(new Sphere.Vec(0, 0, 0), (a, b) => a + b);
        if (sum.Norm < 1e-9) return null;
        var center = sum.Unit;
        var limit = MaxAngle - margin / Sphere.EarthRadius;
        return vs.All(v => center.Angle(v) <= limit) ? new Gnomonic(center) : null;
    }

    public Coordinate Forward(Coordinate c)
    {
        var p = Sphere.ToVec(c);
        var d = p.Dot(_c);
        return new Coordinate(p.Dot(_e) / d, p.Dot(_n) / d);
    }

    public Coordinate Inverse(Coordinate xy) => Sphere.ToCoordinate(_c + _e * xy.X + _n * xy.Y);

    public Geometry Forward(Geometry g) => Map(g, Forward);

    /// <summary>Back to lng/lat, with polygon shells counterclockwise and holes clockwise.</summary>
    public Geometry Inverse(Geometry g) => Map(Orient(g), Inverse);

    private static Geometry Map(Geometry g, Func<Coordinate, Coordinate> f)
    {
        var copy = g.Copy();
        copy.Apply(new MapFilter(f));
        copy.GeometryChanged();
        return copy;
    }

    private static Geometry Orient(Geometry g) => g switch
    {
        Polygon p => OrientPolygon(p),
        MultiPolygon mp => GeoJson.Factory.CreateMultiPolygon(mp.Geometries.Cast<Polygon>().Select(OrientPolygon).ToArray()),
        GeometryCollection gc when gc.GeometryType == "GeometryCollection" =>
            GeoJson.Factory.CreateGeometryCollection(gc.Geometries.Select(Orient).ToArray()),
        _ => g
    };

    private static Polygon OrientPolygon(Polygon p)
    {
        LinearRing OrientRing(LinearRing r, bool ccw) =>
            Orientation.IsCCW(r.CoordinateSequence) == ccw ? r : (LinearRing)r.Reverse();
        return GeoJson.Factory.CreatePolygon(OrientRing(p.Shell, true), p.Holes.Select(h => OrientRing(h, false)).ToArray());
    }

    private sealed class MapFilter(Func<Coordinate, Coordinate> f) : ICoordinateSequenceFilter
    {
        public void Filter(CoordinateSequence seq, int i)
        {
            var r = f(new Coordinate(seq.GetX(i), seq.GetY(i)));
            seq.SetX(i, r.X);
            seq.SetY(i, r.Y);
        }

        public bool Done => false;
        public bool GeometryChanged => true;
    }
}
