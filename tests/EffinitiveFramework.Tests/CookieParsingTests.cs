using EffinitiveFramework.Core.Http;
using Xunit;

namespace EffinitiveFramework.Tests;

/// <summary>
/// Tests for HttpRequest.Cookies lazy-parsed property.
/// </summary>
public class CookieParsingTests
{
    [Fact]
    public void Cookies_NoCookieHeader_ReturnsEmpty()
    {
        var request = new HttpRequest();
        Assert.Empty(request.Cookies);
    }

    [Fact]
    public void Cookies_SingleCookie_ParsesCorrectly()
    {
        var request = new HttpRequest();
        request.Headers["Cookie"] = "foo=bar";
        Assert.Equal("bar", request.Cookies["foo"]);
    }

    [Fact]
    public void Cookies_MultipleCookies_SemicolonSeparated()
    {
        var request = new HttpRequest();
        request.Headers["Cookie"] = "a=1; b=2; c=3";
        Assert.Equal(3, request.Cookies.Count);
        Assert.Equal("1", request.Cookies["a"]);
        Assert.Equal("2", request.Cookies["b"]);
        Assert.Equal("3", request.Cookies["c"]);
    }

    [Fact]
    public void Cookies_EmptyValue_ParsesAsEmpty()
    {
        var request = new HttpRequest();
        request.Headers["Cookie"] = "empty=";
        Assert.Equal("", request.Cookies["empty"]);
    }

    [Fact]
    public void Cookies_NoEqualsSign_SkipsEntry()
    {
        var request = new HttpRequest();
        request.Headers["Cookie"] = "valid=yes; malformed; other=ok";
        Assert.Equal(2, request.Cookies.Count);
        Assert.Equal("yes", request.Cookies["valid"]);
        Assert.Equal("ok", request.Cookies["other"]);
    }

    [Fact]
    public void Cookies_WhitespaceHandling_TrimsPairs()
    {
        var request = new HttpRequest();
        request.Headers["Cookie"] = "  foo = bar ;  baz = qux  ";
        Assert.True(request.Cookies.ContainsKey("foo"));
        Assert.True(request.Cookies.ContainsKey("baz"));
    }

    [Fact]
    public void Cookies_LazyParsed_CachedAfterFirstAccess()
    {
        var request = new HttpRequest();
        request.Headers["Cookie"] = "x=1";
        var first = request.Cookies;
        var second = request.Cookies;
        Assert.Same(first, second);
    }

    [Fact]
    public void Cookies_Reset_ClearsCachedCookies()
    {
        var request = new HttpRequest();
        request.Headers["Cookie"] = "x=1";
        _ = request.Cookies; // trigger parse
        request.Reset();
        Assert.Empty(request.Cookies); // Cookie header cleared by Reset
    }

    [Fact]
    public void Cookies_CommaSeparated_AlsoParses()
    {
        var request = new HttpRequest();
        request.Headers["Cookie"] = "a=1, b=2";
        Assert.Equal(2, request.Cookies.Count);
    }

    [Fact]
    public void Cookies_DuplicateNames_LastWins()
    {
        var request = new HttpRequest();
        request.Headers["Cookie"] = "dup=first; dup=second";
        Assert.Equal("second", request.Cookies["dup"]);
    }
}
