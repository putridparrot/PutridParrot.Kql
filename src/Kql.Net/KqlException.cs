namespace Kql.Net;

/// <summary>A diagnostic (error/warning) for a KQL query, with its position in the query text.</summary>
public sealed record KqlDiagnostic(string Code, string Severity, string Message, int Start, int Length)
{
    public override string ToString() => $"{Severity} {Code} at {Start}: {Message}";
}

/// <summary>Thrown when a query cannot be parsed, bound, compiled or executed.</summary>
public class KqlException : Exception
{
    public KqlException(string message) : base(message) => Diagnostics = [];

    public KqlException(string message, Exception inner) : base(message, inner) => Diagnostics = [];

    public KqlException(IReadOnlyList<KqlDiagnostic> diagnostics)
        : base("KQL query is invalid:" + Environment.NewLine + string.Join(Environment.NewLine, diagnostics.Select(d => "  " + d)))
        => Diagnostics = diagnostics;

    public IReadOnlyList<KqlDiagnostic> Diagnostics { get; }
}

/// <summary>Thrown for valid KQL that Kql.Net does not (yet) execute.</summary>
public sealed class KqlNotSupportedException : KqlException
{
    public KqlNotSupportedException(string feature, int position)
        : base($"'{feature}' is not supported by Kql.Net (at position {position}).")
    {
        Feature = feature;
        Position = position;
    }

    public string Feature { get; }
    public int Position { get; }
}
