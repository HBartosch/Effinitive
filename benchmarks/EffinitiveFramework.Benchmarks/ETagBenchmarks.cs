using System.Buffers.Binary;
using System.IO.Hashing;
using System.Security.Cryptography;
using BenchmarkDotNet.Attributes;

namespace EffinitiveFramework.Benchmarks;

/// <summary>
/// Cost of deriving the entity-tag that every 2xx GET response carries.
/// </summary>
/// <remarks>
/// An entity-tag is opaque to the client (RFC 9110 8.8.3): it is compared for
/// equality and never interpreted, so the only properties that matter are that
/// it is stable for a given representation and unlikely to collide across
/// different ones. Nothing about it is a security boundary, and the current
/// implementation already truncates its digest to eight bytes, so the strength
/// on offer is 64 bits whichever way it is produced.
///
/// Run: dotnet run -c Release -- --filter *ETag*
/// </remarks>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 10)]
public class ETagBenchmarks
{
    // Plaintext, a small JSON object, and a page-sized document.
    [Params(13, 27, 4096)]
    public int BodySize { get; set; }

    private byte[] _body = null!;

    [GlobalSetup]
    public void Setup()
    {
        _body = new byte[BodySize];
        Random.Shared.NextBytes(_body);
    }

    [Benchmark(Baseline = true)]
    public string Current()
    {
        var hash = SHA256.HashData(_body);
        return $"\"{Convert.ToHexString(hash, 0, 8).ToLowerInvariant()}\"";
    }

    [Benchmark]
    public string Sha256NoIntermediateStrings()
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(_body, hash);
        return Format(BinaryPrimitives.ReadUInt64BigEndian(hash));
    }

    [Benchmark]
    public string XxHash64NoIntermediateStrings()
        => Format(XxHash64.HashToUInt64(_body));

    private static string Format(ulong value) => string.Create(18, value, static (dst, v) =>
    {
        const string Hex = "0123456789abcdef";
        dst[0] = '"';
        for (int i = 0; i < 16; i++)
            dst[1 + i] = Hex[(int)((v >> (60 - (i * 4))) & 0xF)];
        dst[17] = '"';
    });
}
