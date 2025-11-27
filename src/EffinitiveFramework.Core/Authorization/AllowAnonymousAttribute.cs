namespace EffinitiveFramework.Core.Authorization;

/// <summary>
/// Indicates that an endpoint allows anonymous access without authentication
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class AllowAnonymousAttribute : Attribute
{
}
