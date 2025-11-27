using System.Buffers;

namespace EffinitiveFramework.Core.Http2.Hpack;

/// <summary>
/// Huffman encoder for HPACK (RFC 7541 Appendix B)
/// Implements static Huffman encoding for header compression
/// </summary>
public static class HuffmanEncoder
{
    /// <summary>
    /// Encode string using RFC 7541 Appendix B Huffman table
    /// </summary>
    public static byte[] Encode(string input)
    {
        if (string.IsNullOrEmpty(input))
            return Array.Empty<byte>();

        // Estimate output size (Huffman typically reduces size by ~30%)
        var estimatedSize = (int)(input.Length * 0.7) + 1;
        var buffer = new byte[estimatedSize * 2]; // Double for safety
        
        var bitBuffer = 0u;
        var bitsInBuffer = 0;
        var outputIndex = 0;

        foreach (var ch in input)
        {
            // Only encode valid ASCII (0-255)
            if (ch > 255)
            {
                throw new ArgumentException($"Character '{ch}' (U+{((int)ch):X4}) is not valid ASCII (0-255)", nameof(input));
            }
            
            var symbol = (byte)ch;
            var (code, bits) = HuffmanTable.Codes[symbol];

            // Add code to bit buffer
            bitBuffer = (bitBuffer << bits) | code;
            bitsInBuffer += bits;

            // Write complete bytes
            while (bitsInBuffer >= 8)
            {
                bitsInBuffer -= 8;
                buffer[outputIndex++] = (byte)(bitBuffer >> bitsInBuffer);
                bitBuffer &= (1u << bitsInBuffer) - 1;
            }
        }

        // Add EOS padding (all 1s)
        if (bitsInBuffer > 0)
        {
            var padding = 8 - bitsInBuffer;
            bitBuffer = (bitBuffer << padding) | ((1u << padding) - 1);
            buffer[outputIndex++] = (byte)bitBuffer;
        }

        // Return exact-sized array
        var result = new byte[outputIndex];
        Array.Copy(buffer, result, outputIndex);
        return result;
    }
}
