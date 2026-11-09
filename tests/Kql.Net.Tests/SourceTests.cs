using System.Data;
using System.Dynamic;

namespace Kql.Net.Tests;

public class SourceTests
{
    [Fact]
    public void Array_OfObjects()
    {
        var kql = new KqlContext().AddTable("Customers", TestData.Customers().ToArray());
        Assert.Equal(2L, kql.Execute("Customers | where Tier == 1 | count").Scalar());
    }

    [Fact]
    public void Array_OfScalars_HasValueColumn()
    {
        var kql = new KqlContext().AddTable("Numbers", new[] { 5, 3, 8, 1 });
        var r = kql.Execute("Numbers | where value > 2 | sort by value asc");
        Assert.Equal("value", r.Columns.Single().Name);
        Assert.Equal([3, 5, 8], r.Column("value"));
    }

    [Fact]
    public void List_OfStrings()
    {
        var kql = new KqlContext().AddTable("Words", new List<string> { "apple", "banana", "cherry" });
        Assert.Equal(["banana"], kql.Execute("Words | where value has 'banana'").Column("value"));
    }

    [Fact]
    public void Dictionary_HasKeyAndValueColumns()
    {
        var stock = new Dictionary<string, int> { ["apples"] = 10, ["pears"] = 0, ["plums"] = 7 };
        var kql = new KqlContext().AddTable("Stock", stock);
        var r = kql.Execute("Stock | where Value > 0 | sort by Key asc");
        Assert.Equal(["Key", "Value"], r.Columns.Select(c => c.Name));
        Assert.Equal(["apples", "plums"], r.Column("Key"));
    }

    [Fact]
    public void Dictionary_WithObjectValues_IsDynamic()
    {
        var people = new Dictionary<int, Customer> { [1] = new("ann", "UK", 2), [2] = new("ben", "IE", 1) };
        var kql = new KqlContext().AddTable("People", people);
        var r = kql.Execute("People | where toint(Value.Tier) == 1 | project Key, Name = tostring(Value.Name)");
        Assert.Equal(2, r[0, "Key"]);
        Assert.Equal("ben", r[0, "Name"]);
    }

    [Fact]
    public void ExpandoObjects_InferSchema()
    {
        dynamic a = new ExpandoObject();
        a.Name = "x";
        a.Score = 10;
        dynamic b = new ExpandoObject();
        b.Name = "y";
        b.Score = 2.5;
        b.Extra = true;
        var kql = new KqlContext().AddTable("Bag", new List<ExpandoObject> { a, b });
        var r = kql.Execute("Bag | getschema");
        Assert.Equal(["Name", "Score", "Extra"], r.Column("ColumnName"));
        Assert.Equal(["string", "real", "bool"], r.Column("ColumnType"));
        Assert.Equal(["y"], kql.Execute("Bag | where Score < 5").Column("Name"));
    }

    [Fact]
    public void Dictionaries_AsRows()
    {
        var rows = new List<Dictionary<string, object?>>
        {
            new() { ["host"] = "a", ["cpu"] = 50L },
            new() { ["host"] = "b", ["cpu"] = 95L },
        };
        var kql = new KqlContext().AddTable("Metrics", rows);
        Assert.Equal(["b"], kql.Execute("Metrics | where cpu > 90").Column("host"));
    }

    [Fact]
    public void DataTable_Source()
    {
        var dt = new DataTable();
        dt.Columns.Add("Product Name", typeof(string));
        dt.Columns.Add("Price", typeof(decimal));
        dt.Rows.Add("Widget", 9.99m);
        dt.Rows.Add("Gadget", 24.50m);
        dt.Rows.Add("Gizmo", DBNull.Value);
        var kql = new KqlContext().AddTable("Products", dt);
        var r = kql.Execute("Products | where isnotnull(Price) | summarize total = sum(Price)");
        Assert.Equal(34.49m, r.Scalar());
        Assert.Equal(["Widget"], kql.Execute("Products | where ['Product Name'] startswith 'W'").Column("Product Name"));
    }

    [Fact]
    public void Json_Source()
    {
        var json = """[{"id": 1, "tags": ["a", "b"], "meta": {"ok": true}}, {"id": 2, "tags": [], "meta": {"ok": false}}]""";
        var kql = new KqlContext().AddJsonTable("Docs", json);
        var r = kql.Execute("Docs | where tobool(meta.ok) | project id, n = array_length(tags)");
        Assert.Equal(1L, r[0, "id"]);
        Assert.Equal(2L, r[0, "n"]);
    }

    [Fact]
    public void Queryable_Source()
    {
        var kql = new KqlContext().AddTable("Orders", TestData.Orders().AsQueryable());
        Assert.Equal(6L, kql.Execute("Orders | count").Scalar());
    }

    [Fact]
    public void Records_AreMappedBack()
    {
        var kql = TestData.Context();
        var rows = kql.Execute<CustomerSummary>("Customers | summarize Count = count() by Tier | sort by Tier asc");
        Assert.Equal([new CustomerSummary(1, 2), new CustomerSummary(2, 1), new CustomerSummary(3, 1)], rows);
    }

    [Fact]
    public void Execute_MapsToClasses()
    {
        var kql = TestData.Context();
        var orders = kql.Execute<Order>("Orders | where Region == 'EU'");
        Assert.Equal(2, orders.Count);
        Assert.Equal(OrderStatus.Shipped, orders[0].Status);
        Assert.Equal(120.5, orders[0].Amount);
    }

    [Fact]
    public void Execute_ScalarType()
    {
        var kql = TestData.Context();
        Assert.Equal([1L, 2L, 3L], kql.Execute<long>("Customers | distinct Tier | sort by Tier asc"));
    }

    [Fact]
    public void Results_ConvertToDataTableAndJson()
    {
        var kql = TestData.Context();
        var r = kql.Execute("Customers | where Tier == 3");
        var dt = r.ToDataTable();
        Assert.Equal("carol", dt.Rows[0]["Name"]);
        Assert.Equal("""[{"Name":"carol","Country":"Japan","Tier":3}]""", r.ToJson());
    }

    public sealed record CustomerSummary(int Tier, long Count);
}
