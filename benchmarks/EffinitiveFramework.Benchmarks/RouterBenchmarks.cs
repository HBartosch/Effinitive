using BenchmarkDotNet.Attributes;
using EffinitiveFramework.Core;

namespace EffinitiveFramework.Benchmarks;

/// <summary>
/// The three outcomes a route lookup can have, measured separately.
/// </summary>
/// <remarks>
/// An exact match returns from the frozen dictionary without consulting the parametric
/// table. Everything else falls through to it: a route with a parameter in its pattern,
/// and equally a request for a path that is not registered at all, which is the shape a
/// 404 takes. Both therefore pay whatever that fallthrough costs.
///
/// Run: dotnet run -c Release -f net10.0 -- --filter *RouterBenchmarks*
/// </remarks>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 10)]
public class RouterBenchmarks
{
    private Router _router = null!;

    [GlobalSetup]
    public void Setup()
    {
        _router = new Router();
        _router.AddRoute("GET", "/plain", (EmptyRequest _, CancellationToken _) => Task.FromResult("x"));
        _router.AddRoute("GET", "/users/{id}", (EmptyRequest _, CancellationToken _) => Task.FromResult("x"));
        _router.AddRoute("POST", "/orders/{id}/items", (EmptyRequest _, CancellationToken _) => Task.FromResult("x"));
        _router.Freeze();
    }

    [Benchmark(Baseline = true)]
    public RouteMatch? ExactMatch() => _router.FindRoute("GET", "/plain");

    [Benchmark]
    public RouteMatch? ParametricMatch() => _router.FindRoute("GET", "/users/42");

    [Benchmark]
    public RouteMatch? NoMatch() => _router.FindRoute("GET", "/not-registered");
}
