namespace PutridParrot.Kql;

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

/// <summary>Thrown for valid KQL that PutridParrot.Kql does not (yet) execute.</summary>
public sealed class KqlNotSupportedException : KqlException
{
    public KqlNotSupportedException(string feature, int position)
        : this(feature, position, null)
    {
    }

    /// <param name="hint">How to get the feature, e.g. which package adds it (appended to the message).</param>
    public KqlNotSupportedException(string feature, int position, string? hint)
        : base($"'{feature}' is not supported by PutridParrot.Kql (at position {position}).{(hint is null ? "" : " " + hint)}")
    {
        Feature = feature;
        Position = position;
    }

    public string Feature { get; }
    public int Position { get; }
}
