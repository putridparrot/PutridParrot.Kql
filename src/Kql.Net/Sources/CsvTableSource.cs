using System.Text;
using Kql.Net.Runtime;

namespace Kql.Net;

/// <summary>Options for reading CSV data.</summary>
public sealed record CsvOptions
{
    /// <summary>Field delimiter (default <c>,</c>).</summary>
    public char Delimiter { get; init; } = ',';

    /// <summary>Whether the first record holds column names (default true). Without a header, columns are named Column1, Column2…</summary>
    public bool HasHeader { get; init; } = true;

    /// <summary>Infer column types from the data (default true); otherwise every column is a string.</summary>
    public bool InferTypes { get; init; } = true;

    /// <summary>How many records to sample for type inference (default 1000).</summary>
    public int InferenceSampleSize { get; init; } = 1000;

    /// <summary>Explicit column types by name, overriding inference (use <c>typeof(object)</c> for a JSON <c>dynamic</c> column).</summary>
    public IReadOnlyDictionary<string, Type>? ColumnTypes { get; init; }

    /// <summary>Encoding for files (default UTF-8).</summary>
    public Encoding Encoding { get; init; } = Encoding.UTF8;
}

/// <summary>
/// CSV support (RFC 4180: quoted fields, doubled quotes, delimiters and newlines inside quotes).
/// </summary>
public static class CsvTableSource
{
    /// <summary>
    /// Creates a source over a CSV file. The schema is inferred when the source is created; the file is read again
    /// on every query, so changes to its rows are picked up.
    /// </summary>
    public static RowSetTableSource FromFile(string path, CsvOptions? options = null)
    {
        options ??= new CsvOptions();
        return Create(() => new StreamReader(path, options.Encoding), options);
    }

    /// <summary>Creates a source over CSV text held in memory.</summary>
    public static RowSetTableSource FromText(string csv, CsvOptions? options = null) =>
        Create(() => new StringReader(csv), options ?? new CsvOptions());

    private static RowSetTableSource Create(Func<TextReader> open, CsvOptions options)
    {
        // read the header and a sample to build the schema
        string[] names;
        var sample = new List<string?[]>();
        using (var reader = open())
        {
            using var records = Records(reader, options.Delimiter).GetEnumerator();
            if (options.HasHeader)
            {
                names = records.MoveNext() ? records.Current : [];
            }
            else
            {
                names = [];
            }
            while (sample.Count < options.InferenceSampleSize && records.MoveNext()) sample.Add(records.Current);
        }
        var width = Math.Max(names.Length, sample.Count == 0 ? 0 : sample.Max(r => r.Length));
        names = Enumerable.Range(0, width).Select(i => i < names.Length && !string.IsNullOrWhiteSpace(names[i]) ? names[i].Trim() : $"Column{i + 1}").ToArray();
        names = Unique(names);

        var types = names.Select((name, i) =>
            options.ColumnTypes?.TryGetValue(name, out var t) == true ? t
            : options.InferTypes ? TextTypeInference.Infer(sample.Select(r => i < r.Length ? r[i] : null))
            : typeof(string)).ToArray();

        var columns = names.Select((n, i) => (n, types[i])).ToArray();
        return new RowSetTableSource(columns, () => Rows(open, options, types));
    }

    private static IEnumerable<object?[]> Rows(Func<TextReader> open, CsvOptions options, Type[] types)
    {
        using var reader = open();
        var first = true;
        var skipHeader = options.HasHeader;
        foreach (var record in Records(reader, options.Delimiter))
        {
            if (first && skipHeader)
            {
                first = false;
                continue;
            }
            first = false;
            var values = new object?[types.Length];
            for (var i = 0; i < types.Length && i < record.Length; i++)
                values[i] = types[i] == typeof(object) ? Dynamic.Parse(record[i]) : TextTypeInference.Parse(record[i], types[i]);
            yield return values;
        }
    }

    /// <summary>Splits CSV text into records of fields.</summary>
    public static IEnumerable<string[]> Records(TextReader reader, char delimiter = ',')
    {
        var fields = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var any = false;
        int c;
        while ((c = reader.Read()) != -1)
        {
            var ch = (char)c;
            any = true;
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (reader.Peek() == '"')
                    {
                        field.Append('"');
                        reader.Read();
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    field.Append(ch);
                }
            }
            else if (ch == '"' && field.Length == 0)
            {
                inQuotes = true;
            }
            else if (ch == delimiter)
            {
                fields.Add(field.ToString());
                field.Clear();
            }
            else if (ch is '\r' or '\n')
            {
                if (ch == '\r' && reader.Peek() == '\n') reader.Read();
                fields.Add(field.ToString());
                field.Clear();
                if (!(fields.Count == 1 && fields[0].Length == 0)) yield return fields.ToArray(); // skip blank lines
                fields.Clear();
                any = false;
            }
            else
            {
                field.Append(ch);
            }
        }
        if (any)
        {
            fields.Add(field.ToString());
            if (!(fields.Count == 1 && fields[0].Length == 0)) yield return fields.ToArray();
        }
    }

    private static string[] Unique(string[] names)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        return names.Select(n =>
        {
            var candidate = n;
            for (var i = 2; !seen.Add(candidate); i++) candidate = n + i;
            return candidate;
        }).ToArray();
    }
}
