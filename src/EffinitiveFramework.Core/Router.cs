using System.Collections.Frozen;
using System.Runtime.CompilerServices;
using EffinitiveFramework.Core.WebSocket;

namespace EffinitiveFramework.Core;

/// <summary>
/// High-performance router using zero-allocation techniques
/// </summary>
public sealed class Router
{
    // Registration-phase store — mutable, only written before Freeze()
    private readonly Dictionary<string, RouteNode> _mutableRoutes =
        new(StringComparer.OrdinalIgnoreCase);

    // WebSocket routes — keyed by path
    private readonly Dictionary<string, Func<WebSocketConnection, CancellationToken, Task>> _wsRoutes =
        new(StringComparer.OrdinalIgnoreCase);

    // Runtime stores — immutable, populated by Freeze()
    private FrozenDictionary<string, RouteNode>? _frozenRoutes;
    private FrozenDictionary<string, ParametricRoute[]>? _paramRoutes;
    private FrozenDictionary<string, Func<WebSocketConnection, CancellationToken, Task>>? _frozenWsRoutes;
    private bool _frozen;

    /// <summary>
    /// Register a route pattern with its handler
    /// </summary>
    public void AddRoute(string method, string pattern, Delegate handler)
    {
        if (_frozen)
            throw new InvalidOperationException("Cannot add routes after Router.Freeze() has been called.");

        var key = $"{method}:{pattern}";
        if (!_mutableRoutes.TryGetValue(key, out _))
            _mutableRoutes[key] = new RouteNode(pattern, handler, null);
    }

    /// <summary>
    /// Register a route pattern with endpoint type (for DI resolution)
    /// </summary>
    public void AddEndpointType(string method, string pattern, Type endpointType, EndpointInvoker? invoker = null)
    {
        if (_frozen)
            throw new InvalidOperationException("Cannot add routes after Router.Freeze() has been called.");

        var key = $"{method}:{pattern}";
        if (!_mutableRoutes.TryGetValue(key, out _))
            _mutableRoutes[key] = new RouteNode(pattern, null, endpointType, invoker);
    }

    /// <summary>
    /// Register a WebSocket route with a handler delegate.
    /// </summary>
    public void AddWebSocketRoute(string path, Func<WebSocketConnection, CancellationToken, Task> handler)
    {
        if (_frozen)
            throw new InvalidOperationException("Cannot add routes after Router.Freeze() has been called.");

        _wsRoutes[path] = handler;
    }

    /// <summary>
    /// Find WebSocket handler for a given path.
    /// </summary>
    public Func<WebSocketConnection, CancellationToken, Task>? FindWebSocketRoute(ReadOnlySpan<char> path)
    {
        if (_frozenWsRoutes == null) return null;
#if NET9_0_OR_GREATER
        var lookup = _frozenWsRoutes.GetAlternateLookup<ReadOnlySpan<char>>();
        return lookup.TryGetValue(path, out var handler) ? handler : null;
#else
        return _frozenWsRoutes.TryGetValue(path.ToString(), out var handler) ? handler : null;
#endif
    }

    /// <summary>
    /// Freeze the router after all routes are registered. Must be called once before FindRoute().
    /// Materialises FrozenDictionary for zero-allocation exact-match lookups and pre-splits
    /// parameterised route patterns to eliminate per-request Split('/') allocations.
    /// </summary>
    public void Freeze()
    {
        if (_frozen) return;
        _frozen = true;

        // Build immutable exact-match dictionary
        _frozenRoutes = _mutableRoutes.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

        // Build per-method parameterised route lists with pre-split segments
        var byMethod = new Dictionary<string, List<ParametricRoute>>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in _mutableRoutes)
        {
            var colonIdx = kvp.Key.IndexOf(':');
            if (colonIdx < 0) continue;
            var method  = kvp.Key[..colonIdx];
            var pattern = kvp.Key[(colonIdx + 1)..];
            if (!pattern.Contains('{')) continue;

            // Pre-split once at startup — never again at request time
            var segs = pattern.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (!byMethod.TryGetValue(method, out var list))
            {
                list = [];
                byMethod[method] = list;
            }
            list.Add(new ParametricRoute(segs, kvp.Value));
        }

        var final = new Dictionary<string, ParametricRoute[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in byMethod)
            final[kv.Key] = [.. kv.Value];
        _paramRoutes = final.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

        // Freeze WebSocket routes
        _frozenWsRoutes = _wsRoutes.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Find matching route for given method and path
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public RouteMatch? FindRoute(ReadOnlySpan<char> method, ReadOnlySpan<char> path)
    {
        if (_frozenRoutes == null)
            ThrowNotFrozen();

        // Strip query string before lookup
        var qsIndex = path.IndexOf('?');
        var pathOnly = qsIndex >= 0 ? path[..qsIndex] : path;

        // Fast path: exact match — build composite key on the stack, zero heap allocation
        Span<char> keyBuffer = stackalloc char[method.Length + pathOnly.Length + 1];
        method.CopyTo(keyBuffer);
        keyBuffer[method.Length] = ':';
        pathOnly.CopyTo(keyBuffer[(method.Length + 1)..]);

        // FrozenDictionary uses a computed perfect hash — faster lookup than Dictionary.
        // On .NET 9+, GetAlternateLookup avoids the string allocation entirely by using a span-based lookup.
        // Minimum .NET version requirement: NET9_0_OR_GREATER for span-based lookup; NET8 falls back to string key.
#if NET9_0_OR_GREATER
        var lookup = _frozenRoutes!.GetAlternateLookup<ReadOnlySpan<char>>();
        if (lookup.TryGetValue(keyBuffer, out var node))
            return new RouteMatch(node.Handler, null, node.EndpointType, node.Invoker);
#else
        // Fallback for .NET 8: allocate a string key for lookup.
        var key = new string(keyBuffer);
        if (_frozenRoutes!.TryGetValue(key, out var node))
            return new RouteMatch(node.Handler, null, node.EndpointType, node.Invoker);
#endif

        // Slow path: parameterised routes
        return FindParametricRoute(method, pathOnly);
    }

    /// <summary>
    /// Check if a path has routes registered for any HTTP method.
    /// Returns the list of methods if found, null otherwise.
    /// Used for 405 Method Not Allowed with Allow header.
    /// </summary>
    public List<string>? GetAllowedMethods(ReadOnlySpan<char> path)
    {
        if (_frozenRoutes == null) return null;

        var qsIndex = path.IndexOf('?');
        var pathOnly = qsIndex >= 0 ? path[..qsIndex] : path;

        List<string>? methods = null;
        string[] methodList = ["GET", "POST", "PUT", "DELETE", "PATCH"];

        // Max method length (DELETE=6) + 1 (colon) + path
        Span<char> keyBuf = stackalloc char[7 + pathOnly.Length];

        for (int i = 0; i < methodList.Length; i++)
        {
            var m = methodList[i].AsSpan();
            var keySlice = keyBuf[..(m.Length + pathOnly.Length + 1)];
            m.CopyTo(keySlice);
            keySlice[m.Length] = ':';
            pathOnly.CopyTo(keySlice[(m.Length + 1)..]);

#if NET9_0_OR_GREATER
            var lookup = _frozenRoutes.GetAlternateLookup<ReadOnlySpan<char>>();
            if (lookup.TryGetValue(keySlice, out _))
            {
                methods ??= new List<string>(4);
                methods.Add(methodList[i]);
            }
#else
            if (_frozenRoutes.TryGetValue(new string(keySlice), out _))
            {
                methods ??= new List<string>(4);
                methods.Add(methodList[i]);
            }
#endif
        }

        // Also check parametric routes
        if (_paramRoutes != null)
        {
            foreach (var m in methodList)
            {
                if (methods != null && methods.Contains(m)) continue;
                if (!_paramRoutes.TryGetValue(m, out var candidates)) continue;
                foreach (var route in candidates)
                {
                    if (MatchSegments(pathOnly, route.Segments) != null)
                    {
                        methods ??= new List<string>(4);
                        methods.Add(m);
                        break;
                    }
                }
            }
        }

        return methods;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowNotFrozen() =>
        throw new InvalidOperationException(
            "Router.Freeze() must be called before FindRoute(). Call it from EffinitiveAppBuilder.Build().");

    private RouteMatch? FindParametricRoute(ReadOnlySpan<char> method, ReadOnlySpan<char> path)
    {
        if (_paramRoutes == null) return null;

        // One short string alloc for the method key — "GET", "POST", etc. (unavoidable)
        if (!_paramRoutes.TryGetValue(method.ToString(), out var candidates))
            return null;

        foreach (var route in candidates)
        {
            var match = MatchSegments(path, route.Segments);
            if (match != null)
                return new RouteMatch(route.Node.Handler, match, route.Node.EndpointType, route.Node.Invoker);
        }

        return null;
    }

    /// <summary>
    /// Match path segments against pre-split pattern segments using spans — no Split('/') allocation.
    /// </summary>
    private static Dictionary<string, string>? MatchSegments(ReadOnlySpan<char> path, string[] patternSegs)
    {
        // Count path segments without allocating
        int segCount = 0;
        var tmp = path.TrimStart('/');
        while (!tmp.IsEmpty)
        {
            segCount++;
            int s = tmp.IndexOf('/');
            if (s < 0) break;
            tmp = tmp[(s + 1)..].TrimStart('/');
        }

        if (segCount != patternSegs.Length) return null;

        Dictionary<string, string>? parameters = null;
        var remaining = path.TrimStart('/');

        for (int i = 0; i < patternSegs.Length; i++)
        {
            int slash = remaining.IndexOf('/');
            var seg = slash < 0 ? remaining : remaining[..slash];
            var pat = patternSegs[i];

            if (pat.Length > 2 && pat[0] == '{' && pat[^1] == '}')
            {
                // Parameter capture — one string alloc per captured value (unavoidable)
                parameters ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                parameters[pat[1..^1]] = seg.ToString();
            }
            else if (!seg.Equals(pat.AsSpan(), StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            remaining = slash < 0 ? default : remaining[(slash + 1)..].TrimStart('/');
        }

        // Return an empty dictionary (not null) when the route matches but has no parameters,
        // to distinguish "matched with no params" from "no match" (null), which is returned above.
        return parameters ?? new Dictionary<string, string>();
    }

    private sealed class RouteNode
    {
        public string Pattern { get; }
        public Delegate? Handler { get; }
        public Type? EndpointType { get; }
        public EndpointInvoker? Invoker { get; }

        public RouteNode(string pattern, Delegate? handler, Type? endpointType = null, EndpointInvoker? invoker = null)
        {
            Pattern = pattern;
            Handler = handler;
            EndpointType = endpointType;
            Invoker = invoker;
        }
    }

    private sealed class ParametricRoute
    {
        public string[] Segments { get; }
        public RouteNode Node { get; }

        public ParametricRoute(string[] segments, RouteNode node)
        {
            Segments = segments;
            Node = node;
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
    /// <summary>Pre-compiled invoker for this endpoint type. Null for legacy handler-based routes.</summary>
    public EndpointInvoker? Invoker { get; }

    public RouteMatch(Delegate? handler, Dictionary<string, string>? parameters, Type? endpointType = null, EndpointInvoker? invoker = null)
    {
        Handler = handler;
        Parameters = parameters;
        EndpointType = endpointType;
        Invoker = invoker;
    }
}
