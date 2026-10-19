using EffinitiveFramework.Core.Http;
using EffinitiveFramework.Core.Middleware;
using Xunit;

namespace EffinitiveFramework.Tests;

/// <summary>
/// The pipeline keeps the chain it builds, and what it keeps still behaves like a chain built
/// fresh each time.
/// </summary>
/// <remarks>
/// Building walks every registered factory and each one yields a delegate, so building per
/// request allocates in proportion to how much middleware is registered. Keeping the chain is
/// only safe while three things hold: the order middleware runs in does not change, registering
/// more middleware is noticed, and a different terminal is not answered with the previous
/// chain.
/// </remarks>
public class MiddlewarePipelineReuseTests
{
    private sealed class Recording(string name, List<string> log) : IMiddleware
    {
        public async ValueTask<HttpResponse> InvokeAsync(
            HttpRequest request, RequestDelegate next, CancellationToken cancellationToken)
        {
            log.Add($"enter:{name}");
            var response = await next(request, cancellationToken);
            log.Add($"exit:{name}");
            return response;
        }
    }

    private static RequestDelegate Terminal(List<string> log, string tag = "terminal")
        => (_, _) =>
        {
            log.Add(tag);
            return ValueTask.FromResult(new HttpResponse { StatusCode = 200 });
        };

    [Fact]
    public async Task OrderIsTheSameOnEveryExecution()
    {
        var log = new List<string>();
        var pipeline = new MiddlewarePipeline();
        pipeline.Use(new Recording("a", log));
        pipeline.Use(new Recording("b", log));

        var terminal = Terminal(log);
        var request = new HttpRequest { Path = "/x" };

        await pipeline.ExecuteAsync(request, terminal, CancellationToken.None);
        var first = log.ToArray();

        log.Clear();
        await pipeline.ExecuteAsync(request, terminal, CancellationToken.None);

        Assert.Equal(["enter:a", "enter:b", "terminal", "exit:b", "exit:a"], first);
        Assert.Equal(first, log);
    }

    [Fact]
    public async Task MiddlewareRegisteredAfterAnExecutionStillRuns()
    {
        var log = new List<string>();
        var pipeline = new MiddlewarePipeline();
        pipeline.Use(new Recording("a", log));

        var terminal = Terminal(log);
        var request = new HttpRequest { Path = "/x" };

        await pipeline.ExecuteAsync(request, terminal, CancellationToken.None);

        // A chain kept from before this registration would never reach "b".
        pipeline.Use(new Recording("b", log));
        log.Clear();

        await pipeline.ExecuteAsync(request, terminal, CancellationToken.None);

        Assert.Equal(["enter:a", "enter:b", "terminal", "exit:b", "exit:a"], log);
    }

    [Fact]
    public async Task ADifferentTerminalIsNotAnsweredWithThePreviousChain()
    {
        var log = new List<string>();
        var pipeline = new MiddlewarePipeline();
        pipeline.Use(new Recording("a", log));

        var request = new HttpRequest { Path = "/x" };

        await pipeline.ExecuteAsync(request, Terminal(log, "first"), CancellationToken.None);
        log.Clear();

        await pipeline.ExecuteAsync(request, Terminal(log, "second"), CancellationToken.None);

        Assert.Equal(["enter:a", "second", "exit:a"], log);
    }

    [Fact]
    public async Task AMiddlewareThatShortCircuitsNeverReachesTheTerminal()
    {
        var log = new List<string>();
        var pipeline = new MiddlewarePipeline();
        pipeline.Use((request, next, ct) =>
        {
            log.Add("short");
            return ValueTask.FromResult(new HttpResponse { StatusCode = 403 });
        });

        var response = await pipeline.ExecuteAsync(
            new HttpRequest { Path = "/x" }, Terminal(log), CancellationToken.None);

        Assert.Equal(403, response.StatusCode);
        Assert.Equal(["short"], log);
    }

    [Fact]
    public async Task ConcurrentExecutionsEachSeeTheWholeChain()
    {
        // The chain is shared across threads once kept, so it has to be re-entrant.
        var pipeline = new MiddlewarePipeline();
        var depth = 0;
        pipeline.Use(async (request, next, ct) =>
        {
            Interlocked.Increment(ref depth);
            var response = await next(request, ct);
            return response;
        });

        var terminal = new RequestDelegate((_, _) =>
            ValueTask.FromResult(new HttpResponse { StatusCode = 200 }));

        var work = Enumerable.Range(0, 64).Select(_ => Task.Run(async () =>
        {
            var response = await pipeline.ExecuteAsync(
                new HttpRequest { Path = "/x" }, terminal, CancellationToken.None);
            Assert.Equal(200, response.StatusCode);
        }));

        await Task.WhenAll(work);

        Assert.Equal(64, depth);
    }
}
