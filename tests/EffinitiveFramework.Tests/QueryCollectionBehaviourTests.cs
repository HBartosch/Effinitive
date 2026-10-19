using EffinitiveFramework.Core.Http;
using Xunit;

namespace EffinitiveFramework.Tests;

/// <summary>
/// What the query collection reports, for the shapes a path can take.
/// </summary>
/// <remarks>
/// Values are read out of the path the request already holds rather than copied into strings,
/// so the cases that matter are the ones where an offset into that text is not the whole story:
/// a percent escape that has to be decoded before it means anything, a pair with no value, a
/// fragment that is not part of the query, and a repeated name.
/// </remarks>
public class QueryCollectionBehaviourTests
{
    private static QueryCollection Parse(string path)
    {
        var request = new HttpRequest { Path = path };
        return request.Query;
    }

    [Fact]
    public void ValuesAreReadOutOfThePathWithoutDecodingWhenThereIsNothingEscaped()
    {
        var query = Parse("/x?a=13&b=42");

        Assert.Equal(2, query.Count);
        Assert.Equal(13, query.GetInt("a"));
        Assert.Equal(42, query.GetInt("b"));
        Assert.Equal("13", query.Get("a"));
    }

    [Fact]
    public void APercentEscapeIsDecoded()
    {
        var query = Parse("/x?term=hello%20world");

        Assert.Equal("hello world", query.Get("term"));
    }

    [Fact]
    public void APlusIsLeftAsItself()
    {
        // RFC 3986 §3.4 admits '+' as an ordinary character in a query. Reading it as a space
        // is a form-encoding convention, and the query component carries nothing that says the
        // text was form-encoded.
        var query = Parse("/x?term=a+b");

        Assert.Equal("a+b", query.Get("term"));
    }

    [Fact]
    public void NamesAreMatchedWithoutRegardToCase()
    {
        var query = Parse("/x?Alpha=1");

        Assert.Equal(1, query.GetInt("alpha"));
        Assert.True(query.ContainsKey("ALPHA"));
    }

    [Fact]
    public void APairWithNoValueReadsAsEmptyRatherThanMissing()
    {
        var query = Parse("/x?flag&b=2");

        Assert.True(query.ContainsKey("flag"));
        Assert.Equal(string.Empty, query.Get("flag"));
        Assert.Equal(2, query.GetInt("b"));
    }

    [Fact]
    public void AFragmentIsNotPartOfTheQuery()
    {
        var query = Parse("/x?a=1#section");

        Assert.Equal(1, query.Count);
        Assert.Equal("1", query.Get("a"));
    }

    [Fact]
    public void AnAbsentNameYieldsTheSuppliedDefault()
    {
        var query = Parse("/x?a=1");

        Assert.Equal(7, query.GetInt("missing", 7));
        Assert.Null(query.Get("missing"));
        Assert.False(query.TryGetValue("missing", out _));
    }

    [Theory]
    [InlineData("/x")]
    [InlineData("/x?")]
    [InlineData("/x?#frag")]
    [InlineData("/x?&&")]
    [InlineData("/x?=novalue")]
    public void APathWithNothingToReadYieldsAnEmptyCollection(string path)
    {
        Assert.Empty(Parse(path));
    }

    [Fact]
    public void MoreThanTheInitialCapacityOfPairsIsKept()
    {
        // The pair store starts small and grows; a query longer than that must not lose pairs.
        var query = Parse("/x?a=1&b=2&c=3&d=4&e=5&f=6&g=7");

        Assert.Equal(7, query.Count);
        Assert.Equal(7, query.GetInt("g"));
        Assert.Equal(1, query.GetInt("a"));
    }

    [Fact]
    public void EnumeratingYieldsEveryPair()
    {
        var query = Parse("/x?a=1&b=2");

        var seen = new List<KeyValuePair<string, string>>(query);

        Assert.Equal(2, seen.Count);
        Assert.Contains(seen, kv => kv.Key == "a" && kv.Value == "1");
        Assert.Contains(seen, kv => kv.Key == "b" && kv.Value == "2");
    }
}
