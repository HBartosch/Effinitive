using System.Buffers;
using System.Runtime.CompilerServices;

namespace EffinitiveFramework.Core;

/// <summary>
/// High-performance router using zero-allocation techniques
/// </summary>
public sealed class Router
{
    private readonly Dictionary<string, RouteNode> _routes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ArrayPool<string> _segmentPool = ArrayPool<string>.Shared;

    /// <summary>
    /// Register a route pattern with its handler
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AddRoute(string method, string pattern, Delegate handler)
    {
        var key = $"{method}:{pattern}";
        
        if (!_routes.TryGetValue(key, out var node))
        {
            node = new RouteNode(pattern, handler, null);
            _routes[key] = node;
        }
    }

    /// <summary>
    /// Register a route pattern with endpoint type (for DI resolution)
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AddEndpointType(string method, string pattern, Type endpointType)
    {
        var key = $"{method}:{pattern}";
        
        if (!_routes.TryGetValue(key, out var node))
        {
            node = new RouteNode(pattern, null, endpointType);
            _routes[key] = node;
        }
    }

    /// <summary>
    /// Find matching route for given method and path
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public RouteMatch? FindRoute(ReadOnlySpan<char> method, ReadOnlySpan<char> path)
    {
        // Fast path: exact match lookup
        Span<char> keyBuffer = stackalloc char[method.Length + path.Length + 1];
        method.CopyTo(keyBuffer);
        keyBuffer[method.Length] = ':';
        path.CopyTo(keyBuffer[(method.Length + 1)..]);
        
        var key = new string(keyBuffer);
        
        if (_routes.TryGetValue(key, out var node))
        {
            return new RouteMatch(node.Handler, null, node.EndpointType);
        }

        // Slow path: check routes with parameters
        var methodStr = method.ToString();
        foreach (var kvp in _routes)
        {
            if (!kvp.Key.StartsWith(methodStr + ":", StringComparison.OrdinalIgnoreCase))
                continue;

            var routePattern = kvp.Key.Substring(methodStr.Length + 1);
            var match = MatchRouteWithParameters(path, routePattern);
            if (match != null)
            {
                return new RouteMatch(kvp.Value.Handler, match, kvp.Value.EndpointType);
            }
        }

        return null;
    }

    private Dictionary<string, string>? MatchRouteWithParameters(ReadOnlySpan<char> path, string pattern)
    {
        var pathSegments = path.ToString().Split('/', StringSplitOptions.RemoveEmptyEntries);
        var patternSegments = pattern.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (pathSegments.Length != patternSegments.Length)
            return null;

        Dictionary<string, string>? parameters = null;

        for (int i = 0; i < pathSegments.Length; i++)
        {
            var patternSegment = patternSegments[i];
            var pathSegment = pathSegments[i];

            if (patternSegment.StartsWith('{') && patternSegment.EndsWith('}'))
            {
                // This is a parameter
                var paramName = patternSegment.Trim('{', '}');
                parameters ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                parameters[paramName] = pathSegment;
            }
            else if (!patternSegment.Equals(pathSegment, StringComparison.OrdinalIgnoreCase))
            {
                // Segment doesn't match
                return null;
            }
        }

        return parameters;
    }

    private sealed class RouteNode
    {
        public string Pattern { get; }
        public Delegate? Handler { get; }
        public Type? EndpointType { get; }

        public RouteNode(string pattern, Delegate? handler, Type? endpointType = null)
        {
            Pattern = pattern;
            Handler = handler;
            EndpointType = endpointType;
        }
    }
}

/// <summary>
/// Represents a matched route
/// </summary>
public readonly struct RouteMatch
{
    public Delegate? Handler { get; }
    public Dictionary<string, string>? Parameters { get; }
    public Type? EndpointType { get; }

    public RouteMatch(Delegate? handler, Dictionary<string, string>? parameters, Type? endpointType = null)
    {
        Handler = handler;
        Parameters = parameters;
        EndpointType = endpointType;
    }
}
