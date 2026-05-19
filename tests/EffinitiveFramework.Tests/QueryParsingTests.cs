using EffinitiveFramework.Core.Http;
using Xunit;

namespace EffinitiveFramework.Tests;

public class QueryParsingTests
{
    [Fact]
    public void Query_NoQueryString_ReturnsEmptyCollection()
    {
        var request = new HttpRequest { Path = "/baseline11" };

        Assert.Empty(request.Query);
        Assert.Equal(0, request.Query.GetInt("a"));
    }

    [Fact]
    public void Query_ParsesTypedValuesFromPath()
    {
        var request = new HttpRequest { Path = "/baseline11?a=13&b=42&min=10.5" };

        Assert.Equal(13, request.Query.GetInt("a"));
        Assert.Equal(42, request.Query.GetInt("b"));
        Assert.Equal(10.5, request.Query.GetDouble("min"));
    }

    [Fact]
    public void Query_DecodesEscapedValues_AndIsCaseInsensitive()
    {
        var request = new HttpRequest { Path = "/search?TERM=hello%20world" };

        Assert.Equal("hello world", request.Query.Get("term"));
    }

    [Fact]
    public void Query_IsCachedUntilPathChanges()
    {
        var request = new HttpRequest { Path = "/baseline11?a=1" };

        var first = request.Query;
        request.Path = "/baseline11?a=2";
        var second = request.Query;

        Assert.NotSame(first, second);
        Assert.Equal(2, second.GetInt("a"));
    }

    [Fact]
    public void Query_ResetClearsCachedValues()
    {
        var request = new HttpRequest { Path = "/baseline11?a=7" };

        _ = request.Query;
        request.Reset();

        Assert.Empty(request.Query);
    }
}