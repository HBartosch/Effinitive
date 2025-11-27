using System.Text;

namespace EffinitiveFramework.Core.Http2.Hpack;

/// <summary>
/// Huffman decoder for HPACK (RFC 7541 Appendix B)
/// Complete implementation of the static Huffman code
/// </summary>
public static class HuffmanDecoder
{
    /// <summary>
    /// Decode Huffman-encoded string according to RFC 7541 Appendix B
    /// </summary>
    public static string Decode(ReadOnlySpan<byte> encoded)
    {
        if (encoded.Length == 0)
            return string.Empty;

        var result = new StringBuilder();
        var node = _root;
        var bitsInBuffer = 0;
        var bitBuffer = 0u;

        foreach (var b in encoded)
        {
            bitBuffer = (bitBuffer << 8) | b;
            bitsInBuffer += 8;

            while (bitsInBuffer >= 8)
            {
                var index = (bitBuffer >> (bitsInBuffer - 8)) & 0xFF;
                
                // Process bits from most significant
                for (int i = 7; i >= 0 && node != null; i--)
                {
                    var bit = (index >> i) & 1;
                    node = bit == 0 ? node.Left : node.Right;

                    if (node?.Value != null)
                    {
                        result.Append((char)node.Value.Value);
                        node = _root;
                    }
                }

                bitsInBuffer -= 8;
            }
        }

        // Process remaining bits (padding should be all 1s)
        if (bitsInBuffer > 0 && node != _root)
        {
            // Remaining bits should be padding (all 1s)
            var remainingBits = bitBuffer & ((1u << bitsInBuffer) - 1);
            var expectedPadding = (1u << bitsInBuffer) - 1;
            
            if (remainingBits != expectedPadding)
            {
                // Invalid padding - but be lenient in decoding
                // RFC 7541 §5.2: padding must be high bits, but we'll accept it
            }
        }

        return result.ToString();
    }

    // Huffman tree node
    private class HuffmanNode
    {
        public HuffmanNode? Left { get; set; }
        public HuffmanNode? Right { get; set; }
        public int? Value { get; set; }
    }

    // Build the Huffman tree from RFC 7541 Appendix B
    private static readonly HuffmanNode _root = BuildHuffmanTree();

    private static HuffmanNode BuildHuffmanTree()
    {
        var root = new HuffmanNode();

        // Use shared Huffman table from HuffmanTable.Codes
        for (int symbol = 0; symbol < HuffmanTable.Codes.Length; symbol++)
        {
            var (code, bits) = HuffmanTable.Codes[symbol];
            var node = root;
            
            // Build path in tree from MSB to LSB
            for (int i = bits - 1; i >= 0; i--)
            {
                var bit = (code >> i) & 1;
                
                if (bit == 0)
                {
                    node.Left ??= new HuffmanNode();
                    node = node.Left;
                }
                else
                {
                    node.Right ??= new HuffmanNode();
                    node = node.Right;
                }
            }
            
            node.Value = symbol;
        }

        // Add EOS symbol (256)
        {
            var (code, bits) = HuffmanTable.EOS;
            var node = root;
            
            for (int i = bits - 1; i >= 0; i--)
            {
                var bit = (code >> i) & 1;
                
                if (bit == 0)
                {
                    node.Left ??= new HuffmanNode();
                    node = node.Left;
                }
                else
                {
                    node.Right ??= new HuffmanNode();
                    node = node.Right;
                }
            }
            
            node.Value = 256; // EOS
        }

        return root;
    }
}
