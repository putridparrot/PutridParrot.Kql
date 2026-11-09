namespace Kql.Net.Tests;

public enum OrderStatus { Pending, Shipped, Cancelled }

public sealed class Order
{
    public int Id { get; set; }
    public string Customer { get; set; } = "";
    public string Region { get; set; } = "";
    public double Amount { get; set; }
    public int Quantity { get; set; }
    public DateTime Timestamp { get; set; }
    public OrderStatus Status { get; set; }
    public string? Notes { get; set; }
}

public sealed record Customer(string Name, string Country, int Tier);

public static class TestData
{
    public static readonly DateTime Day0 = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public static List<Order> Orders() =>
    [
        new() { Id = 1, Customer = "alice", Region = "EU", Amount = 120.5, Quantity = 2, Timestamp = Day0.AddHours(1), Status = OrderStatus.Shipped, Notes = "Express delivery requested" },
        new() { Id = 2, Customer = "bob", Region = "US", Amount = 75, Quantity = 1, Timestamp = Day0.AddHours(5), Status = OrderStatus.Pending },
        new() { Id = 3, Customer = "alice", Region = "EU", Amount = 300, Quantity = 5, Timestamp = Day0.AddDays(1), Status = OrderStatus.Shipped, Notes = "gift-wrap" },
        new() { Id = 4, Customer = "carol", Region = "APAC", Amount = 42.25, Quantity = 3, Timestamp = Day0.AddDays(1).AddHours(3), Status = OrderStatus.Cancelled },
        new() { Id = 5, Customer = "dave", Region = "US", Amount = 999.99, Quantity = 10, Timestamp = Day0.AddDays(2), Status = OrderStatus.Shipped, Notes = "EXPRESS" },
        new() { Id = 6, Customer = "bob", Region = "US", Amount = 15, Quantity = 1, Timestamp = Day0.AddDays(3), Status = OrderStatus.Shipped },
    ];

    public static List<Customer> Customers() =>
    [
        new("alice", "France", 1),
        new("bob", "USA", 2),
        new("carol", "Japan", 3),
        new("erin", "Canada", 1),
    ];

    public static KqlContext Context() => new KqlContext()
        .AddTable("Orders", Orders())
        .AddTable("Customers", Customers());
}
