using System.ComponentModel.DataAnnotations;
using Routya.ResultKit.Attributes;

namespace EffinitiveFramework.Validation.Sample.Requests;

/// <summary>
/// Request demonstrating Routya.ResultKit's advanced validation attributes
/// </summary>
public class CreateOrderRequest
{
    [Required]
    public string ProductName { get; set; } = string.Empty;

    [Range(1, 1000, ErrorMessage = "Quantity must be between 1 and 1000")]
    public int Quantity { get; set; }

    [Range(0.01, double.MaxValue, ErrorMessage = "Price must be greater than zero")]
    public decimal UnitPrice { get; set; }

    public decimal MinimumOrderValue { get; set; }

    [GreaterThan(nameof(MinimumOrderValue), ErrorMessage = "Total amount must be greater than minimum order value")]
    public decimal TotalAmount { get; set; }

    public DateTime? PromotionStartDate { get; set; }

    [RequiredIf(nameof(PromotionStartDate), null, ErrorMessage = "Promotion end date is required when promotion starts")]
    public DateTime? PromotionEndDate { get; set; }

    [MinItems(1, ErrorMessage = "At least one shipping address is required")]
    [MaxItems(5, ErrorMessage = "Maximum 5 shipping addresses allowed")]
    public List<string> ShippingAddresses { get; set; } = new();

    [MatchRegex(@"^[A-Z]{2}\d{4,10}$", ErrorMessage = "Invalid coupon code format. Must start with 2 uppercase letters followed by 4-10 digits")]
    public string? CouponCode { get; set; }
}
