using EffinitiveFramework.Core;
using EffinitiveFramework.Validation.Sample.Requests;

namespace EffinitiveFramework.Validation.Sample.Endpoints;

public class CreateOrderEndpoint : AsyncEndpointBase<CreateOrderRequest, object>
{
    protected override string Method => "POST";
    protected override string Route => "/orders";

    public override async Task<object> HandleAsync(CreateOrderRequest request, CancellationToken cancellationToken = default)
    {
        // Validation already passed via ValidationMiddleware
        await Task.Delay(10, cancellationToken);

        return new
        {
            success = true,
            message = "Order created successfully",
            order = new
            {
                productName = request.ProductName,
                quantity = request.Quantity,
                unitPrice = request.UnitPrice,
                totalAmount = request.TotalAmount,
                shippingAddresses = request.ShippingAddresses,
                couponCode = request.CouponCode
            }
        };
    }
}
