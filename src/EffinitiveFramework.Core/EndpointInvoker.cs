using System.Collections.Frozen;
using System.Linq.Expressions;
using System.Reflection;
using EffinitiveFramework.Core.Http;

namespace EffinitiveFramework.Core;

/// <summary>
/// Compiled per-endpoint-type descriptor. Built once at startup; zero reflection on the hot request path.
/// </summary>
public sealed class EndpointInvoker
{
    /// <summary>The TRequest type extracted from IEndpoint&lt;TRequest,TResponse&gt;.</summary>
    public required Type RequestType { get; init; }

    /// <summary>
    /// Route-parameter setters keyed by parameter name (case-insensitive).
    /// Each entry holds the <see cref="PropertyInfo"/> (for type conversion) and a compiled
    /// setter that avoids <see cref="PropertyInfo.SetValue"/> boxing.
    /// </summary>
    public required FrozenDictionary<string, RouteParamSetter> RouteParamSetters { get; init; }

    /// <summary>Compiled delegate that sets HttpContext on an endpoint instance without reflection.</summary>
    public required Action<object, HttpRequest> SetHttpContext { get; init; }

    /// <summary>Compiled delegate that reads the ContentType property without reflection.</summary>
    public required Func<object, string> GetContentType { get; init; }

    /// <summary>True for NoRequest endpoint variants where HandleAsync takes only CancellationToken.</summary>
    public required bool IsNoRequest { get; init; }

    /// <summary>
    /// Compiled invocation delegate. Signature: (endpointInstance, requestObj, cancellationToken) => Task&lt;object?&gt;
    /// The Task result is the boxed TResponse. Built once via Expression.Lambda at startup.
    /// </summary>
    public required Func<object, object?, CancellationToken, Task<object?>> InvokeAsync { get; init; }

    // -----------------------------------------------------------------------
    // Factory
    // -----------------------------------------------------------------------

    /// <summary>Build an <see cref="EndpointInvoker"/> for the given endpoint type.</summary>
    public static EndpointInvoker Build(Type endpointType)
    {
        // Find the implemented interface: IEndpoint<TRequest,TResponse> or IAsyncEndpoint<TRequest,TResponse>
        var endpointInterface = endpointType.GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType &&
                (i.GetGenericTypeDefinition() == typeof(IEndpoint<,>) ||
                 i.GetGenericTypeDefinition() == typeof(IAsyncEndpoint<,>)));

        if (endpointInterface == null)
            throw new InvalidOperationException($"Type {endpointType.Name} does not implement IEndpoint<,> or IAsyncEndpoint<,>");

        var genericArgs = endpointInterface.GetGenericArguments();
        var requestType  = genericArgs[0];
        var responseType = genericArgs[1];
        var isAsync = endpointInterface.GetGenericTypeDefinition() == typeof(IAsyncEndpoint<,>);

        // Determine NoRequest variant (HandleAsync takes only CancellationToken)
        var handleMethod = endpointType.GetMethod("HandleAsync",
            BindingFlags.Public | BindingFlags.Instance);
        if (handleMethod == null)
            throw new InvalidOperationException($"Type {endpointType.Name} has no public HandleAsync method");

        var handleParams = handleMethod.GetParameters();
        var isNoRequest = handleParams.Length == 1 &&
                          handleParams[0].ParameterType == typeof(CancellationToken);

        // --- Build InvokeAsync compiled delegate ---
        var invokeAsync = BuildInvokeAsync(endpointType, handleMethod, requestType, responseType, isAsync, isNoRequest);

        // --- Build SetHttpContext compiled delegate ---
        var setHttpContext = BuildSetHttpContext(endpointType);

        // --- Build GetContentType compiled delegate ---
        var getContentType = BuildGetContentType(endpointType);

        // --- Build route-param setters for every public writable property on TRequest ---
        var routeParamSetters = BuildRouteParamSetters(requestType);

        return new EndpointInvoker
        {
            RequestType      = requestType,
            RouteParamSetters = routeParamSetters,
            SetHttpContext   = setHttpContext,
            GetContentType   = getContentType,
            IsNoRequest      = isNoRequest,
            InvokeAsync      = invokeAsync,
        };
    }

    // -----------------------------------------------------------------------
    // Private builders
    // -----------------------------------------------------------------------

    private static Func<object, object?, CancellationToken, Task<object?>> BuildInvokeAsync(
        Type endpointType,
        MethodInfo handleMethod,
        Type requestType,
        Type responseType,
        bool isAsync,
        bool isNoRequest)
    {
        // Parameters for the outer lambda
        var endpointParam = Expression.Parameter(typeof(object), "endpointObj");
        var requestParam  = Expression.Parameter(typeof(object), "requestObj");
        var ctParam       = Expression.Parameter(typeof(CancellationToken), "ct");

        // Cast endpoint to concrete type
        var typedEndpoint = Expression.Convert(endpointParam, endpointType);

        // Build the HandleAsync call
        Expression callExpr;
        if (isNoRequest)
        {
            callExpr = Expression.Call(typedEndpoint, handleMethod, ctParam);
        }
        else
        {
            var typedRequest = Expression.Convert(requestParam, requestType);
            callExpr = Expression.Call(typedEndpoint, handleMethod, typedRequest, ctParam);
        }

        // callExpr returns either ValueTask<TResponse> or Task<TResponse>.
        // We need to convert it to Task<object?>.

        // Use a generic helper method to do the conversion without further reflection at call time.
        // The helper is resolved here (once) and baked into the expression tree.
        MethodInfo wrapperMethod;
        if (isAsync)
        {
            // Task<TResponse> -> Task<object?>
            wrapperMethod = typeof(EndpointInvoker)
                .GetMethod(nameof(WrapTaskAsync), BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(responseType);
        }
        else
        {
            // ValueTask<TResponse> -> Task<object?>
            wrapperMethod = typeof(EndpointInvoker)
                .GetMethod(nameof(WrapValueTaskAsync), BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(responseType);
        }

        var wrappedCall = Expression.Call(wrapperMethod, callExpr);

        var lambda = Expression.Lambda<Func<object, object?, CancellationToken, Task<object?>>>(
            wrappedCall, endpointParam, requestParam, ctParam);

        return lambda.Compile();
    }

    private static Action<object, HttpRequest> BuildSetHttpContext(Type endpointType)
    {
        var httpContextProp = endpointType.GetProperty("HttpContext",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        if (httpContextProp == null || !httpContextProp.CanWrite)
        {
            // Property not present or not writable — return no-op
            return static (_, _) => { };
        }

        var endpointParam = Expression.Parameter(typeof(object), "endpoint");
        var requestParam  = Expression.Parameter(typeof(HttpRequest), "request");
        var typedEndpoint = Expression.Convert(endpointParam, endpointType);
        var setProp = Expression.Call(typedEndpoint, httpContextProp.GetSetMethod(nonPublic: true)!, requestParam);
        return Expression.Lambda<Action<object, HttpRequest>>(setProp, endpointParam, requestParam).Compile();
    }

    private static Func<object, string> BuildGetContentType(Type endpointType)
    {
        var prop = endpointType.GetProperty("ContentType",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        if (prop == null || !prop.CanRead)
        {
            return static _ => "application/json";
        }

        var endpointParam = Expression.Parameter(typeof(object), "endpoint");
        var typedEndpoint = Expression.Convert(endpointParam, endpointType);
        var getProp = Expression.Property(typedEndpoint, prop);
        // prop.PropertyType should be string; convert just in case
        Expression body = prop.PropertyType == typeof(string)
            ? getProp
            : Expression.Call(getProp, nameof(object.ToString), Type.EmptyTypes);
        return Expression.Lambda<Func<object, string>>(body, endpointParam).Compile();
    }

    private static FrozenDictionary<string, RouteParamSetter> BuildRouteParamSetters(Type requestType)
    {
        var dict = new Dictionary<string, RouteParamSetter>(StringComparer.OrdinalIgnoreCase);

        foreach (var prop in requestType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!prop.CanWrite) continue;

            var targetParam = Expression.Parameter(typeof(object), "target");
            var valueParam  = Expression.Parameter(typeof(object), "value");
            var typedTarget = Expression.Convert(targetParam, requestType);
            var typedValue  = Expression.Convert(valueParam, prop.PropertyType);
            var setProp     = Expression.Call(typedTarget, prop.GetSetMethod()!, typedValue);
            var setter      = Expression.Lambda<Action<object, object?>>(setProp, targetParam, valueParam).Compile();

            dict[prop.Name] = new RouteParamSetter(prop, setter);
        }

        return dict.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    // -----------------------------------------------------------------------
    // Private async wrappers (baked into compiled lambdas above)
    // -----------------------------------------------------------------------

    private static async Task<object?> WrapTaskAsync<TResponse>(Task<TResponse> task)
        => await task;

    private static async Task<object?> WrapValueTaskAsync<TResponse>(ValueTask<TResponse> vt)
        => await vt;
}

/// <summary>Pre-built setter for a single route parameter property.</summary>
public sealed class RouteParamSetter
{
    public PropertyInfo Property { get; }
    public Action<object, object?> Setter { get; }

    public RouteParamSetter(PropertyInfo property, Action<object, object?> setter)
    {
        Property = property;
        Setter   = setter;
    }
}
