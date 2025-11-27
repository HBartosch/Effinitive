using System.Text.Json.Serialization;

namespace EffinitiveFramework.EFCore.Sample.Models;

public class Order
{
    public int Id { get; set; }
    public string CustomerName { get; set; } = string.Empty;
    public string CustomerEmail { get; set; } = string.Empty;
    public decimal TotalAmount { get; set; }
    public string Status { get; set; } = "Pending"; // Pending, Processing, Shipped, Delivered
    public DateTime OrderDate { get; set; }
    public DateTime? ShippedDate { get; set; }
    
    public List<OrderItem> Items { get; set; } = new();
}

public class OrderItem
{
    public int Id { get; set; }
    public int OrderId { get; set; }
    public int ProductId { get; set; }
    public string ProductName { get; set; } = string.Empty;
    public decimal UnitPrice { get; set; }
    public int Quantity { get; set; }
    public decimal Subtotal { get; set; }
    
    // Prevent circular reference when serializing to JSON
    [JsonIgnore]
    public Order Order { get; set; } = null!;
}
