namespace EffinitiveFramework.Core.Http;

/// <summary>
/// Lazily-created query parameter collection with typed accessors.
/// </summary>
/// <remarks>
/// Pairs are held as offsets into the path string the request already owns rather than as
/// separate strings, so reading a value as a number reads it straight out of that text. A name
/// or value carrying percent or plus encoding cannot be read in place, and is decoded once
/// during parsing and kept alongside its offsets.
/// </remarks>
public sealed class QueryCollection : IReadOnlyDictionary<string, string>
{
    private readonly struct Pair(
        int nameStart, int nameLength, int valueStart, int valueLength,
        string? decodedName, string? decodedValue)
    {
        public readonly int NameStart = nameStart;
        public readonly int NameLength = nameLength;
        public readonly int ValueStart = valueStart;
        public readonly int ValueLength = valueLength;
        public readonly string? DecodedName = decodedName;
        public readonly string? DecodedValue = decodedValue;
    }

    private readonly string _source;
    private readonly Pair[] _pairs;
    private readonly int _count;

    public static QueryCollection Empty { get; } = new(string.Empty, [], 0);

    private QueryCollection(string source, Pair[] pairs, int count)
    {
        _source = source;
        _pairs = pairs;
        _count = count;
    }

    private ReadOnlySpan<char> NameOf(in Pair pair)
        => pair.DecodedName ?? _source.AsSpan(pair.NameStart, pair.NameLength);

    private ReadOnlySpan<char> ValueOf(in Pair pair)
        => pair.DecodedValue ?? _source.AsSpan(pair.ValueStart, pair.ValueLength);

    /// <summary>
    /// Whether the text means something other than itself. A plus is a literal in a query
    /// (RFC 3986 §3.4); reading it as a space is a form-encoding convention that the URI
    /// grammar does not impose, so only a percent-escape forces a decode.
    /// </summary>
    private static bool NeedsDecoding(ReadOnlySpan<char> text)
        => text.IndexOf('%') >= 0;

    private static string Decode(ReadOnlySpan<char> text)
        => Uri.UnescapeDataString(text.ToString());

    private bool TryFind(string key, out Pair found)
    {
        for (int i = 0; i < _count; i++)
        {
            if (NameOf(_pairs[i]).Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                found = _pairs[i];
                return true;
            }
        }

        found = default;
        return false;
    }

    internal static QueryCollection Parse(string path)
    {
        int queryIndex = path.IndexOf('?');
        if (queryIndex < 0 || queryIndex == path.Length - 1)
            return Empty;

        int cursor = queryIndex + 1;
        int limit = path.Length;

        int fragment = path.AsSpan(cursor).IndexOf('#');
        if (fragment >= 0)
            limit = cursor + fragment;

        if (cursor >= limit)
            return Empty;

        Pair[]? pairs = null;
        int count = 0;

        while (cursor < limit)
        {
            int separator = path.AsSpan(cursor, limit - cursor).IndexOf('&');
            int pairEnd = separator >= 0 ? cursor + separator : limit;

            if (pairEnd > cursor)
            {
                int equals = path.AsSpan(cursor, pairEnd - cursor).IndexOf('=');
                int nameStart = cursor;
                int nameLength = equals >= 0 ? equals : pairEnd - cursor;
                int valueStart = equals >= 0 ? cursor + equals + 1 : pairEnd;
                int valueLength = pairEnd - valueStart;

                if (nameLength > 0)
                {
                    var name = path.AsSpan(nameStart, nameLength);
                    var value = path.AsSpan(valueStart, valueLength);

                    pairs ??= new Pair[4];
                    if (count == pairs.Length)
                        Array.Resize(ref pairs, pairs.Length * 2);

                    pairs[count++] = new Pair(
                        nameStart, nameLength, valueStart, valueLength,
                        NeedsDecoding(name) ? Decode(name) : null,
                        NeedsDecoding(value) ? Decode(value) : null);
                }
            }

            cursor = pairEnd + 1;
        }

        return count == 0 ? Empty : new QueryCollection(path, pairs!, count);
    }

    public string? Get(string key)
        => TryFind(key, out var pair) ? ValueOf(pair).ToString() : null;

    public int GetInt(string key, int defaultValue = 0)
        => TryFind(key, out var pair) && int.TryParse(ValueOf(pair), out var parsed) ? parsed : defaultValue;

    public double GetDouble(string key, double defaultValue = 0)
        => TryFind(key, out var pair) && double.TryParse(ValueOf(pair), out var parsed) ? parsed : defaultValue;

    public IEnumerable<string> Keys
    {
        get
        {
            for (int i = 0; i < _count; i++)
                yield return NameOf(_pairs[i]).ToString();
        }
    }

    public IEnumerable<string> Values
    {
        get
        {
            for (int i = 0; i < _count; i++)
                yield return ValueOf(_pairs[i]).ToString();
        }
    }

    public int Count => _count;

    public string this[string key] => TryFind(key, out var pair)
        ? ValueOf(pair).ToString()
        : throw new KeyNotFoundException(key);

    public bool ContainsKey(string key) => TryFind(key, out _);

    public bool TryGetValue(string key, out string value)
    {
        if (TryFind(key, out var pair))
        {
            value = ValueOf(pair).ToString();
            return true;
        }

        value = null!;
        return false;
    }

    public IEnumerator<KeyValuePair<string, string>> GetEnumerator()
    {
        for (int i = 0; i < _count; i++)
            yield return new KeyValuePair<string, string>(
                NameOf(_pairs[i]).ToString(), ValueOf(_pairs[i]).ToString());
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
}
