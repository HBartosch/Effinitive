using EffinitiveFramework.EFCore.Sample.Data;
using EffinitiveFramework.EFCore.Sample.Models;
using Microsoft.EntityFrameworkCore;

namespace EffinitiveFramework.EFCore.Sample.Services;

public interface IOrderService
{
    Task<List<Order>> GetAllOrdersAsync(CancellationToken cancellationToken = default);
    Task<Order?> GetOrderByIdAsync(int id, CancellationToken cancellationToken = default);
    Task<List<Order>> GetOrdersByCustomerEmailAsync(string email, CancellationToken cancellationToken = default);
    Task<Order> CreateOrderAsync(CreateOrderRequest request, CancellationToken cancellationToken = default);
    Task<Order?> UpdateOrderStatusAsync(int id, string status, CancellationToken cancellationToken = default);
}

public class OrderService : IOrderService
{
    private readonly AppDbContext _context;
    private readonly IProductService _productService;

    public OrderService(AppDbContext context, IProductService productService)
    {
        _context = context;
        _productService = productService;
    }

    public async Task<List<Order>> GetAllOrdersAsync(CancellationToken cancellationToken = default)
    {
        return await _context.Orders
            .AsNoTracking()
            .Include(o => o.Items)
            .OrderByDescending(o => o.OrderDate)
            .ToListAsync(cancellationToken);
    }

    public async Task<Order?> GetOrderByIdAsync(int id, CancellationToken cancellationToken = default)
    {
        return await _context.Orders
            .AsNoTracking()
            .Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.Id == id, cancellationToken);
    }

    public async Task<List<Order>> GetOrdersByCustomerEmailAsync(string email, CancellationToken cancellationToken = default)
    {
        return await _context.Orders
            .AsNoTracking()
            .Include(o => o.Items)
            .Where(o => o.CustomerEmail == email)
            .OrderByDescending(o => o.OrderDate)
            .ToListAsync(cancellationToken);
    }

    public async Task<Order> CreateOrderAsync(CreateOrderRequest request, CancellationToken cancellationToken = default)
    {
        var order = new Order
        {
            CustomerName = request.CustomerName,
            CustomerEmail = request.CustomerEmail,
            OrderDate = DateTime.UtcNow,
            Status = "Pending"
        };

        decimal totalAmount = 0;

        foreach (var item in request.Items)
        {
            var product = await _productService.GetProductByIdAsync(item.ProductId, cancellationToken);
            if (product == null)
                throw new InvalidOperationException($"Product {item.ProductId} not found");

            if (product.Stock < item.Quantity)
                throw new InvalidOperationException($"Insufficient stock for product {product.Name}");

            var orderItem = new OrderItem
            {
                ProductId = product.Id,
                ProductName = product.Name,
                UnitPrice = product.Price,
                Quantity = item.Quantity,
                Subtotal = product.Price * item.Quantity
            };

            order.Items.Add(orderItem);
            totalAmount += orderItem.Subtotal;

            // Update stock
            await _productService.UpdateStockAsync(product.Id, product.Stock - item.Quantity, cancellationToken);
        }

        order.TotalAmount = totalAmount;

        _context.Orders.Add(order);
        await _context.SaveChangesAsync(cancellationToken);

        return order;
    }

    public async Task<Order?> UpdateOrderStatusAsync(int id, string status, CancellationToken cancellationToken = default)
    {
        var order = await _context.Orders
            .Include(o => o.Items)
            .FirstOrDefaultAsync(o => o.Id == id, cancellationToken);

        if (order == null)
            return null;

        order.Status = status;
        if (status == "Shipped")
            order.ShippedDate = DateTime.UtcNow;

        await _context.SaveChangesAsync(cancellationToken);
        return order;
    }
}

public class CreateOrderRequest
{
    public string CustomerName { get; set; } = string.Empty;
    public string CustomerEmail { get; set; } = string.Empty;
    public List<OrderItemRequest> Items { get; set; } = new();
}

public class OrderItemRequest
{
    public int ProductId { get; set; }
    public int Quantity { get; set; }
}
