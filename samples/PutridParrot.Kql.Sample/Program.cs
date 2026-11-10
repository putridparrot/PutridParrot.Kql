using PutridParrot.Kql;
using PutridParrot.Kql.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

// ---------------------------------------------------------------- in-memory objects
var start = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
var requests = Enumerable.Range(0, 200).Select(i => new Request(
    Id: i,
    Endpoint: new[] { "/api/orders", "/api/customers", "/health" }[i % 3],
    Status: i % 17 == 0 ? 500 : i % 11 == 0 ? 404 : 200,
    DurationMs: 20 + i * 37 % 400,
    Timestamp: start.AddMinutes(i * 7))).ToList();

var kql = new KqlContext()
    .AddTable("Requests", requests)
    .AddTable("Thresholds", new Dictionary<string, int> { ["/api/orders"] = 250, ["/api/customers"] = 300 });

Run("Slowest endpoints", """
    Requests
    | summarize Count = count(), P90 = percentile(DurationMs, 90), Errors = countif(Status >= 500) by Endpoint
    | sort by P90 desc
    """);

Run("Hourly error rate (first 5 hours)", """
    Requests
    | where Timestamp < datetime(2024-01-01 05:00)
    | summarize Total = count(), Failed = countif(Status != 200) by Hour = bin(Timestamp, 1h)
    | extend ErrorRate = round(100.0 * Failed / Total, 1)
    | sort by Hour asc
    """);

Run("Requests over their endpoint's threshold (join with a Dictionary)", """
    Requests
    | join kind=inner (Thresholds) on $left.Endpoint == $right.Key
    | where DurationMs > Value
    | summarize Over = count() by Endpoint
    """);

// ---------------------------------------------------------------- EF Core (SQLite)
using var connection = new SqliteConnection("DataSource=:memory:");
connection.Open();
var options = new DbContextOptionsBuilder<ShopDb>()
    .UseSqlite(connection)
    .LogTo(Console.WriteLine, [DbLoggerCategory.Database.Command.Name], Microsoft.Extensions.Logging.LogLevel.Information)
    .Options;
using (var seed = new ShopDb(new DbContextOptionsBuilder<ShopDb>().UseSqlite(connection).Options))
{
    seed.Database.EnsureCreated();
    seed.Products.AddRange(
        new Product { Name = "Widget", Category = "Tools", Price = 9.99, Stock = 120 },
        new Product { Name = "Gadget", Category = "Tools", Price = 24.50, Stock = 0 },
        new Product { Name = "Gizmo", Category = "Toys", Price = 14.00, Stock = 33 },
        new Product { Name = "Doohickey", Category = "Toys", Price = 4.25, Stock = 500 });
    seed.SaveChanges();
}

using var db = new ShopDb(options);
var dbKql = new KqlContext().UseEntityFrameworkCore().AddTable("Products", db.Products.AsNoTracking());
Run("EF Core: in-stock value by category (translated to SQL)", """
    Products
    | where Stock > 0 and Name contains "g"
    | summarize Items = count(), StockValue = sum(Price * Stock) by Category
    | sort by StockValue desc
    """, dbKql);

void Run(string title, string query, KqlContext? context = null)
{
    Console.WriteLine();
    Console.WriteLine($"== {title}");
    Console.WriteLine(query.Trim());
    Console.WriteLine();
    Console.WriteLine((context ?? kql).Execute(query));
}

record Request(int Id, string Endpoint, int Status, int DurationMs, DateTime Timestamp);

class Product
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public double Price { get; set; }
    public int Stock { get; set; }
}

class ShopDb(DbContextOptions<ShopDb> options) : DbContext(options)
{
    public DbSet<Product> Products => Set<Product>();
}
