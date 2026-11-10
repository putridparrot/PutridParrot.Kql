using System.Runtime.CompilerServices;

namespace PutridParrot.Kql.Tests;

public class CsvTests
{
    private const string Csv = """
        id,name,score,active,joined,notes
        1,alice,12.5,true,2024-01-05,"likes ""quotes"", commas"
        2,bob,7,false,2024-02-10,
        3,"carol
        smith",,true,2024-03-15,multi-line name

        4,dave,30,TRUE,2024-04-20,plain
        """;

    [Fact]
    public void InfersTypesAndHandlesQuoting()
    {
        var kql = new KqlContext().AddTable("People", CsvTableSource.FromText(Csv));
        var schema = kql.Execute("People | getschema");
        Assert.Equal(["id", "name", "score", "active", "joined", "notes"], schema.Column("ColumnName"));
        Assert.Equal(["long", "string", "real", "bool", "datetime", "string"], schema.Column("ColumnType"));

        var r = kql.Execute("People | sort by id asc");
        Assert.Equal(4, r.Count);
        Assert.Equal("likes \"quotes\", commas", r[0, "notes"]);
        Assert.Equal("carol\nsmith", ((string)r[2, "name"]!).Replace("\r", ""));
        Assert.Null(r[2, "score"]);
        Assert.Equal(new DateTime(2024, 2, 10), r[1, "joined"]);
    }

    [Fact]
    public void QueriesWork()
    {
        var kql = new KqlContext().AddTable("People", CsvTableSource.FromText(Csv));
        Assert.Equal(49.5, kql.Execute("People | summarize sum(score)").Scalar());
        Assert.Equal(["alice", "dave"], kql.Execute("People | where active and isnotnull(score) and score > 10 | project name").Column("name"));
    }

    [Fact]
    public void File_IsReReadOnEachQuery_AndSupportsOptions()
    {
        var path = Path.Combine(Path.GetTempPath(), $"kqlnet-{Guid.NewGuid():N}.csv");
        try
        {
            File.WriteAllText(path, "a;b\n1;x\n2;y\n");
            var kql = new KqlContext().AddCsvTable("T", path, new CsvOptions { Delimiter = ';' });
            Assert.Equal(2L, kql.Execute("T | count").Scalar());

            File.AppendAllText(path, "3;z\n");
            Assert.Equal(3L, kql.Execute("T | count").Scalar());
            Assert.Equal("long", kql.Execute("T | getschema").Rows[0][3]);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void NoHeader_AndExplicitTypes()
    {
        var kql = new KqlContext().AddTable("T", CsvTableSource.FromText("10,{\"k\":1}\n20,{\"k\":2}",
            new CsvOptions { HasHeader = false, ColumnTypes = new Dictionary<string, Type> { ["Column2"] = typeof(object) } }));
        Assert.Equal([2L], kql.Execute("T | where Column1 > 15 | project k = tolong(Column2.k)").Column("k"));
    }
}

public class AsyncTests
{
    private static async IAsyncEnumerable<Order> OrdersAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        foreach (var o in TestData.Orders())
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            yield return o;
        }
    }

    [Fact]
    public async Task AsyncEnumerableSource_AndExecuteAsync()
    {
        var kql = new KqlContext().AddTable("Orders", OrdersAsync());
        var r = await kql.ExecuteAsync("Orders | where Region == 'US' | summarize n = count()");
        Assert.Equal(3L, r.Scalar());
        // re-enumerable: works for a second query
        Assert.Equal(6L, (await kql.ExecuteAsync("Orders | count")).Scalar());
    }

    [Fact]
    public async Task StreamAsync_YieldsRowsLazily()
    {
        var produced = 0;
        IEnumerable<int> Numbers()
        {
            for (var i = 0; i < 1_000_000; i++)
            {
                produced++;
                yield return i;
            }
        }

        var kql = new KqlContext().AddTable("N", Numbers());
        var taken = new List<object?>();
        await foreach (var row in kql.StreamAsync("N | where value % 2 == 0"))
        {
            taken.Add(row[0]);
            if (taken.Count == 3) break;
        }
        Assert.Equal([0, 2, 4], taken);
        Assert.True(produced < 10, $"source was enumerated {produced} times");
    }

    [Fact]
    public void GetResultColumns_WithoutRunning()
    {
        var kql = TestData.Context();
        var cols = kql.GetResultColumns("Orders | summarize total = sum(Amount) by Region");
        Assert.Equal(["Region", "total"], cols.Select(c => c.Name));
        Assert.Equal(typeof(double), cols[1].ClrType);
    }
}
