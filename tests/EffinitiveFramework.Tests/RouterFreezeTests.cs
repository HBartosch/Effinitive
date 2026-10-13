using EffinitiveFramework.Core;
using Xunit;

namespace EffinitiveFramework.Tests;

/// <summary>
/// Freeze() builds a span-keyed view of each frozen table once, rather than rebuilding it per
/// lookup. Building one asserts that the table's comparer can hash and compare the alternate key,
/// so a table that ends up with a different comparer than the one it was asked for would fail
/// there instead of at the first request. An application with no parameterised routes and no
/// WebSocket routes freezes two empty tables, which is the ordinary case and the one at risk.
/// </summary>
public class RouterFreezeTests
{
    private static Router Frozen(params (string Method, string Pattern)[] routes)
    {
        var router = new Router();
        foreach (var (method, pattern) in routes)
            router.AddRoute(method, pattern, (EmptyRequest _, CancellationToken _) => Task.FromResult("x"));
        router.Freeze();
        return router;
    }

    [Fact]
    public void RouterWithNoRoutesAtAll_Freezes()
    {
        var router = Frozen();

        Assert.Null(router.FindRoute("GET", "/anything"));
        Assert.Null(router.FindWebSocketRoute("/anything"));
    }

    [Fact]
    public void RouterWithNoParametricOrWebSocketRoutes_Freezes()
    {
        var router = Frozen(("GET", "/plain"), ("POST", "/plain"));

        Assert.NotNull(router.FindRoute("GET", "/plain"));
        Assert.Null(router.FindRoute("GET", "/missing"));
        Assert.Null(router.FindWebSocketRoute("/ws"));
    }

    [Fact]
    public void ParametricRouteStillMatchesAndYieldsItsValues()
    {
        var router = Frozen(("GET", "/plain"), ("GET", "/users/{id}"));

        var match = router.FindRoute("GET", "/users/42");

        Assert.NotNull(match);
        Assert.Equal("42", match!.Value.Parameters!["id"]);
    }

    [Fact]
    public void MethodIsMatchedWithoutRegardToCaseOnTheParametricPath()
    {
        var router = Frozen(("GET", "/users/{id}"));

        Assert.NotNull(router.FindRoute("GET", "/users/7"));
        Assert.NotNull(router.FindRoute("get", "/users/7"));
    }

    [Fact]
    public void FreezeIsIdempotent()
    {
        var router = Frozen(("GET", "/users/{id}"));
        router.Freeze();

        Assert.NotNull(router.FindRoute("GET", "/users/7"));
    }

    [Fact]
    public void WebSocketRouteIsFoundAfterFreeze()
    {
        var router = new Router();
        router.AddWebSocketRoute("/ws/echo", (_, _) => Task.CompletedTask);
        router.Freeze();

        Assert.NotNull(router.FindWebSocketRoute("/ws/echo"));
        Assert.Null(router.FindWebSocketRoute("/ws/other"));
    }
}
