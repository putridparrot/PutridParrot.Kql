using System.Net;

namespace PutridParrot.Kql.Geo;

/// <summary>Where an IP address is, as returned by your lookup for <c>geo_info_from_ip_address</c>.</summary>
public sealed record GeoIpInfo(string? Country, string? State, string? City, double? Latitude, double? Longitude);

/// <summary>Adds the geospatial functions from this package to a <see cref="KqlContext"/>.</summary>
public static class KqlContextGeoExtensions
{
    /// <summary>
    /// Enables H3 cells (<c>geo_point_to_h3cell</c>, <c>geo_h3cell_*</c>, <c>geo_polygon_to_h3cells</c>), buffers,
    /// unions and geometry intersections. Pass <paramref name="ipLookup"/> (for example backed by a MaxMind GeoIP2
    /// database) to enable <c>geo_info_from_ip_address</c>.
    /// </summary>
    public static KqlContext UseGeo(this KqlContext context, Func<IPAddress, GeoIpInfo?>? ipLookup = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.AddFunctions(typeof(GeoFunctions));
        if (ipLookup is not null)
            context.AddFunction("geo_info_from_ip_address", (string? ip) => InfoFromIp(ip, ipLookup));
        return context;
    }

    private static object? InfoFromIp(string? ip, Func<IPAddress, GeoIpInfo?> lookup)
    {
        if (!IPAddress.TryParse(ip?.Trim(), out var address) || lookup(address) is not { } info) return null;
        return new Dictionary<string, object?>
        {
            ["country"] = info.Country,
            ["state"] = info.State,
            ["city"] = info.City,
            ["latitude"] = info.Latitude,
            ["longitude"] = info.Longitude
        };
    }
}
