using System.ComponentModel.DataAnnotations;
using Routya.ResultKit.Attributes;

namespace EffinitiveFramework.Validation.Sample.Requests;

/// <summary>
/// Request model with comprehensive validation examples using:
/// - System.ComponentModel.DataAnnotations (built-in)
/// - Routya.ResultKit custom attributes
/// </summary>
public class CreateUserRequest
{
    [Required(ErrorMessage = "Name is required")]
    [StringLength(100, MinimumLength = 2, ErrorMessage = "Name must be between 2 and 100 characters")]
    public string Name { get; set; } = string.Empty;

    [Required(ErrorMessage = "Email is required")]
    [EmailAddress(ErrorMessage = "Invalid email format")]
    public string Email { get; set; } = string.Empty;

    [Range(18, 120, ErrorMessage = "Age must be between 18 and 120")]
    public int Age { get; set; }

    [Required(ErrorMessage = "Role is required")]
    [StringEnum(typeof(UserRole), ErrorMessage = "Role must be one of: Admin, User, Guest")]
    public string Role { get; set; } = string.Empty;

    [Required(ErrorMessage = "Password is required")]
    [StringLength(100, MinimumLength = 8, ErrorMessage = "Password must be at least 8 characters")]
    public string Password { get; set; } = string.Empty;

    [Compare(nameof(Password), ErrorMessage = "Passwords do not match")]
    public string ConfirmPassword { get; set; } = string.Empty;
}

public enum UserRole
{
    Admin,
    User,
    Guest
}
