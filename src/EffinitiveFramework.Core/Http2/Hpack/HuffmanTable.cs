namespace EffinitiveFramework.Core.Http2.Hpack;

/// <summary>
/// RFC 7541 Appendix B - Static Huffman Code Table
/// Shared by HuffmanEncoder and HuffmanDecoder
/// </summary>
public static class HuffmanTable
{
    /// <summary>
    /// Huffman codes for symbols 0-255
    /// Format: (code, bits) where code is the Huffman code and bits is the code length
    /// </summary>
    public static readonly (uint code, int bits)[] Codes = new (uint, int)[]
    {
        (0x1ff8, 13), (0x7fffd8, 23), (0xfffffe2, 28), (0xfffffe3, 28),     // 0-3
        (0xfffffe4, 28), (0xfffffe5, 28), (0xfffffe6, 28), (0xfffffe7, 28), // 4-7
        (0xfffffe8, 28), (0xffffea, 24), (0x3ffffffc, 30), (0xfffffe9, 28), // 8-11
        (0xfffffea, 28), (0x3ffffffd, 30), (0xfffffeb, 28), (0xfffffec, 28), // 12-15
        (0xfffffed, 28), (0xfffffee, 28), (0xfffffef, 28), (0xffffff0, 28), // 16-19
        (0xffffff1, 28), (0xffffff2, 28), (0x3ffffffe, 30), (0xffffff3, 28), // 20-23
        (0xffffff4, 28), (0xffffff5, 28), (0xffffff6, 28), (0xffffff7, 28), // 24-27
        (0xffffff8, 28), (0xffffff9, 28), (0xffffffa, 28), (0xffffffb, 28), // 28-31
        (0x14, 6), (0x3f8, 10), (0x3f9, 10), (0xffa, 12),                    // 32-35: space, !, ", #
        (0x1ff9, 13), (0x15, 6), (0xf8, 8), (0x7fa, 11),                     // 36-39: $, %, &, '
        (0x3fa, 10), (0x3fb, 10), (0xf9, 8), (0x7fb, 11),                    // 40-43: (, ), *, +
        (0xfa, 8), (0x16, 6), (0x17, 6), (0x18, 6),                          // 44-47: ,, -, ., /
        (0x0, 5), (0x1, 5), (0x2, 5), (0x19, 6),                             // 48-51: 0, 1, 2, 3
        (0x1a, 6), (0x1b, 6), (0x1c, 6), (0x1d, 6),                          // 52-55: 4, 5, 6, 7
        (0x1e, 6), (0x1f, 6), (0x5c, 7), (0xfb, 8),                          // 56-59: 8, 9, :, ;
        (0x7ffc, 15), (0x20, 6), (0xffb, 12), (0x3fc, 10),                   // 60-63: <, =, >, ?
        (0x1ffa, 13), (0x21, 6), (0x5d, 7), (0x5e, 7),                       // 64-67: @, A, B, C
        (0x5f, 7), (0x60, 7), (0x61, 7), (0x62, 7),                          // 68-71: D, E, F, G
        (0x63, 7), (0x64, 7), (0x65, 7), (0x66, 7),                          // 72-75: H, I, J, K
        (0x67, 7), (0x68, 7), (0x69, 7), (0x6a, 7),                          // 76-79: L, M, N, O
        (0x6b, 7), (0x6c, 7), (0x6d, 7), (0x6e, 7),                          // 80-83: P, Q, R, S
        (0x6f, 7), (0x70, 7), (0x71, 7), (0x72, 7),                          // 84-87: T, U, V, W
        (0xfc, 8), (0x73, 7), (0xfd, 8), (0x1ffb, 13),                       // 88-91: X, Y, Z, [
        (0x7fff0, 19), (0x1ffc, 13), (0x3ffc, 14), (0x22, 6),                // 92-95: \, ], ^, _
        (0x7ffd, 15), (0x3, 5), (0x23, 6), (0x4, 5),                         // 96-99: `, a, b, c
        (0x24, 6), (0x5, 5), (0x25, 6), (0x26, 6),                           // 100-103: d, e, f, g
        (0x27, 6), (0x6, 5), (0x74, 7), (0x75, 7),                           // 104-107: h, i, j, k
        (0x28, 6), (0x29, 6), (0x2a, 6), (0x7, 5),                           // 108-111: l, m, n, o
        (0x2b, 6), (0x76, 7), (0x2c, 6), (0x8, 5),                           // 112-115: p, q, r, s
        (0x9, 5), (0x2d, 6), (0x77, 7), (0x78, 7),                           // 116-119: t, u, v, w
        (0x79, 7), (0x7a, 7), (0x7b, 7), (0x7ffe, 15),                       // 120-123: x, y, z, {
        (0x7fc, 11), (0x3ffd, 14), (0x1ffd, 13), (0xffffffc, 28),            // 124-127: |, }, ~, DEL
        (0xfffe6, 20), (0x3fffd2, 22), (0xfffe7, 20), (0xfffe8, 20),         // 128-131
        (0x3fffd3, 22), (0x3fffd4, 22), (0x3fffd5, 22), (0x7fffd9, 23),      // 132-135
        (0x3fffd6, 22), (0x7fffda, 23), (0x7fffdb, 23), (0x7fffdc, 23),      // 136-139
        (0x7fffdd, 23), (0x7fffde, 23), (0xffffeb, 24), (0x7fffdf, 23),      // 140-143
        (0xffffec, 24), (0xffffed, 24), (0x3fffd7, 22), (0x7fffe0, 23),      // 144-147
        (0xffffee, 24), (0x7fffe1, 23), (0x7fffe2, 23), (0x7fffe3, 23),      // 148-151
        (0x7fffe4, 23), (0x1fffdc, 21), (0x3fffd8, 22), (0x7fffe5, 23),      // 152-155
        (0x3fffd9, 22), (0x7fffe6, 23), (0x7fffe7, 23), (0xffffef, 24),      // 156-159
        (0x3fffda, 22), (0x1fffdd, 21), (0xfffe9, 20), (0x3fffdb, 22),       // 160-163
        (0x3fffdc, 22), (0x7fffe8, 23), (0x7fffe9, 23), (0x1fffde, 21),      // 164-167
        (0x7fffea, 23), (0x3fffdd, 22), (0x3fffde, 22), (0xfffff0, 24),      // 168-171
        (0x1fffdf, 21), (0x3fffdf, 22), (0x7fffeb, 23), (0x7fffec, 23),      // 172-175
        (0x1fffe0, 21), (0x1fffe1, 21), (0x3fffe0, 22), (0x1fffe2, 21),      // 176-179
        (0x7fffed, 23), (0x3fffe1, 22), (0x7fffee, 23), (0x7fffef, 23),      // 180-183
        (0xfffea, 20), (0x3fffe2, 22), (0x3fffe3, 22), (0x3fffe4, 22),       // 184-187
        (0x7ffff0, 23), (0x3fffe5, 22), (0x3fffe6, 22), (0x7ffff1, 23),      // 188-191
        (0x3ffffe0, 26), (0x3ffffe1, 26), (0xfffeb, 20), (0x7fff1, 19),      // 192-195
        (0x3fffe7, 22), (0x7ffff2, 23), (0x3fffe8, 22), (0x1ffffec, 25),     // 196-199
        (0x3ffffe2, 26), (0x3ffffe3, 26), (0x3ffffe4, 26), (0x7ffffde, 27),  // 200-203
        (0x7ffffdf, 27), (0x3ffffe5, 26), (0xfffff1, 24), (0x1ffffed, 25),   // 204-207
        (0x7fff2, 19), (0x1fffe3, 21), (0x3ffffe6, 26), (0x7ffffe0, 27),     // 208-211
        (0x7ffffe1, 27), (0x3ffffe7, 26), (0x7ffffe2, 27), (0xfffff2, 24),   // 212-215
        (0x1fffe4, 21), (0x1fffe5, 21), (0x3ffffe8, 26), (0x3ffffe9, 26),    // 216-219
        (0xffffffd, 28), (0x7ffffe3, 27), (0x7ffffe4, 27), (0x7ffffe5, 27),  // 220-223
        (0xfffec, 20), (0xfffff3, 24), (0xfffed, 20), (0x1fffe6, 21),        // 224-227
        (0x3fffe9, 22), (0x1fffe7, 21), (0x1fffe8, 21), (0x7ffff3, 23),      // 228-231
        (0x3fffea, 22), (0x3fffeb, 22), (0x1ffffee, 25), (0x1ffffef, 25),    // 232-235
        (0xfffff4, 24), (0xfffff5, 24), (0x3ffffea, 26), (0x7ffff4, 23),     // 236-239
        (0x3ffffeb, 26), (0x7ffffe6, 27), (0x3ffffec, 26), (0x3ffffed, 26),  // 240-243
        (0x7ffffe7, 27), (0x7ffffe8, 27), (0x7ffffe9, 27), (0x7ffffea, 27),  // 244-247
        (0x7ffffeb, 27), (0xffffffe, 28), (0x7ffffec, 27), (0x7ffffed, 27),  // 248-251
        (0x7ffffee, 27), (0x7ffffef, 27), (0x7fffff0, 27), (0x3ffffee, 26)   // 252-255
    };

    /// <summary>
    /// EOS (End of String) symbol code (symbol 256)
    /// Used for padding validation
    /// </summary>
    public static readonly (uint code, int bits) EOS = (0x3fffffff, 30);
}
