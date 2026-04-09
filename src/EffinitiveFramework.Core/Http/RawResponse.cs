namespace EffinitiveFramework.Core.Http;

/// <summary>
/// Raw response type that bypasses JSON serialization.
/// Use this when you need to return pre-built bytes with custom headers.
/// </summary>
public sealed class RawResponse
{
    public byte[] Body { get; init; } = Array.Empty<byte>();
    public string ContentType { get; init; } = "application/octet-stream";
    public Dictionary<string, string>? Headers { get; init; }
    public int StatusCode { get; init; } = 200;
}
