namespace EffinitiveFramework.Core.Validation;

/// <summary>
/// Marker attribute to enable automatic validation for an endpoint.
/// When applied to an endpoint class, the request will be validated using Routya.ResultKit
/// before the HandleAsync method is called.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class ValidateRequestAttribute : Attribute
{
}
