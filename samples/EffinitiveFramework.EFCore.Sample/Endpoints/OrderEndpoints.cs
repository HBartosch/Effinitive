using EffinitiveFramework.Core;
using EffinitiveFramework.EFCore.Sample.Models;
using EffinitiveFramework.EFCore.Sample.Services;

namespace EffinitiveFramework.EFCore.Sample.Endpoints;

// GET /api/orders
public class GetAllOrdersEndpoint : AsyncEndpointBase<EmptyRequest, List<Order>>
{
    private readonly IOrderService _orderService;

    public GetAllOrdersEndpoint(IOrderService orderService)
    {
        _orderService = orderService;
    }

    protected override string Route => "/api/orders";
    protected override string Method => "GET";

    public override async Task<List<Order>> HandleAsync(EmptyRequest request, CancellationToken cancellationToken)
    {
        return await _orderService.GetAllOrdersAsync(cancellationToken);
    }
}

// GET /api/orders/{id}
public class GetOrderByIdEndpoint : AsyncEndpointBase<GetOrderRequest, Order?>
{
    private readonly IOrderService _orderService;

    public GetOrderByIdEndpoint(IOrderService orderService)
    {
        _orderService = orderService;
    }

    protected override string Route => "/api/orders/{id}";
    protected override string Method => "GET";

    public override async Task<Order?> HandleAsync(GetOrderRequest request, CancellationToken cancellationToken)
    {
        return await _orderService.GetOrderByIdAsync(request.Id, cancellationToken);
    }
}

// GET /api/orders/customer/{email}
public class GetOrdersByCustomerEndpoint : AsyncEndpointBase<GetOrdersByCustomerRequest, List<Order>>
{
    private readonly IOrderService _orderService;

    public GetOrdersByCustomerEndpoint(IOrderService orderService)
    {
        _orderService = orderService;
    }

    protected override string Route => "/api/orders/customer/{email}";
    protected override string Method => "GET";

    public override async Task<List<Order>> HandleAsync(GetOrdersByCustomerRequest request, CancellationToken cancellationToken)
    {
        return await _orderService.GetOrdersByCustomerEmailAsync(request.Email, cancellationToken);
    }
}

// POST /api/orders
public class CreateOrderEndpoint : AsyncEndpointBase<CreateOrderRequest, Order>
{
    private readonly IOrderService _orderService;

    public CreateOrderEndpoint(IOrderService orderService)
    {
        _orderService = orderService;
    }

    protected override string Route => "/api/orders";
    protected override string Method => "POST";

    public override async Task<Order> HandleAsync(CreateOrderRequest request, CancellationToken cancellationToken)
    {
        return await _orderService.CreateOrderAsync(request, cancellationToken);
    }
}

// PATCH /api/orders/{id}/status
public class UpdateOrderStatusEndpoint : AsyncEndpointBase<UpdateOrderStatusRequest, Order?>
{
    private readonly IOrderService _orderService;

    public UpdateOrderStatusEndpoint(IOrderService orderService)
    {
        _orderService = orderService;
    }

    protected override string Route => "/api/orders/{id}/status";
    protected override string Method => "PATCH";

    public override async Task<Order?> HandleAsync(UpdateOrderStatusRequest request, CancellationToken cancellationToken)
    {
        return await _orderService.UpdateOrderStatusAsync(request.Id, request.Status, cancellationToken);
    }
}

// Request models
public class GetOrderRequest
{
    public int Id { get; set; }
}

public class GetOrdersByCustomerRequest
{
    public string Email { get; set; } = string.Empty;
}

public class UpdateOrderStatusRequest
{
    public int Id { get; set; }
    public string Status { get; set; } = string.Empty;
}
