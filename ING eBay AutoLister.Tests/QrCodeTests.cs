using ING_eBay_AutoLister.Services;

namespace ING_eBay_AutoLister.Tests;

/// <summary>
/// The QR encoder behind the Photo Box phone-capture link.
/// </summary>
/// <remarks>
/// <para>
/// A wrong QR code is the worst kind of wrong: it still renders as a convincing square of dots, and
/// the only symptom is a phone that quietly refuses to scan it. So these tests are not written from
/// the same understanding that produced the encoder. The two golden matrices below were generated
/// once and then <b>decoded back</b> by two independent decoders (zbar and OpenCV), alongside a
/// 200-payload fuzz run of random strings from 1 to 180 characters that round-tripped through zbar
/// without a single failure. Pinning the matrices here is what keeps that verification true.
/// </para>
/// <para>
/// The bug this suite exists to prevent already happened: the format information — the 15 bits that
/// tell a reader which mask and error level the rest of the symbol used — was written transposed.
/// Every finder, every timing pattern and every data module was correct, the symbol looked perfect,
/// and nothing on earth could read it.
/// </para>
/// </remarks>
public class QrCodeTests
{
    private static string[] Render(string text) =>
        [.. QrCode.Encode(text).Modules.Select(row => string.Concat(row.Select(b => b ? '1' : '0')))];

    [Fact]
    public void A_known_symbol_is_produced_exactly_as_it_was_verified()
    {
        string[] expected =
        [
            "111111100101101111111", "100000101011001000001", "101110101101001011101",
            "101110101011001011101", "101110100100101011101", "100000100011001000001",
            "111111101010101111111", "000000001100000000000", "100000101011011001110",
            "100110000001110111001", "001011100110101100000", "010101011001111101010",
            "110100111101111111111", "000000001100100000101", "111111100111010011110",
            "100000100010001000111", "101110100111010011100", "101110100101111101000",
            "101110100101110111011", "100000100011111101000", "111111101010100100110",
        ];
        Assert.Equal(expected, Render("a"));
    }

    [Fact]
    public void The_capture_link_shape_is_produced_exactly_as_it_was_verified()
    {
        string[] expected =
        [
            "11111110001100011001001111111", "10000010010010000110101000001",
            "10111010111010101001001011101", "10111010101110000110101011101",
            "10111010101111110001001011101", "10000010111101110110101000001",
            "11111110101010101010101111111", "00000000111011011000000000000",
            "10111110000100111111101111100", "10010100010110000111001010001",
            "11000010100100011100000110000", "11000001001010100001011010010",
            "11101011100110011101110101100", "11010000101111100001001010101",
            "01011111001011110010011000100", "11111001100101000010010100010",
            "10111111111110111110110000100", "10000000101100000111011011101",
            "10001010000000011100101001100", "10010000101010110000111010010",
            "10011111101100011000111110111", "00000000110101100011100011111",
            "11111110010011110011101011100", "10000010111101011000100010001",
            "10111010111010110100111111110", "10111010111001001010110100011",
            "10111010111110111111101011010", "10000010000011000010110110010",
            "11111110100000110000000001100",
        ];
        Assert.Equal(expected, Render("http://192.168.1.50:9333/p/AbCd1234"));
    }

    // The capacities are the codeword counts at error level M less the mode and
    // character-count header — 14 bytes in a version-1 symbol, not the 16 codewords
    // it holds, and 213 in a version-10 one rather than 216.
    [Theory]
    [InlineData(1, 21)]     // the smallest symbol
    [InlineData(14, 21)]    // the last payload that still fits version 1
    [InlineData(15, 25)]    // one byte more, so one version up
    [InlineData(213, 57)]   // the largest payload this encoder accepts
    public void The_symbol_grows_only_when_the_payload_makes_it(int payloadLength, int expectedSize)
    {
        var sym = QrCode.Encode(new string('K', payloadLength));
        Assert.Equal(expectedSize, sym.Size);
    }

    [Fact]
    public void Too_much_data_is_refused_rather_than_silently_truncated()
    {
        // A QR that quietly drops the end of a URL is a link that goes somewhere else.
        Assert.Throws<ArgumentException>(() => QrCode.Encode(new string('K', 214)));
    }

    [Fact]
    public void Every_symbol_carries_the_three_finder_patterns_a_scanner_looks_for()
    {
        foreach (var text in new[] { "a", "http://10.0.0.7:9333/p/Zz9", new string('K', 200) })
        {
            var m = QrCode.Encode(text).Modules;
            var n = m.Length;
            foreach (var (oy, ox) in new[] { (0, 0), (0, n - 7), (n - 7, 0) })
            {
                Assert.True(m[oy + 0][ox + 0], $"{text}: finder at {oy},{ox} is missing its corner");
                Assert.True(m[oy + 3][ox + 3], $"{text}: finder at {oy},{ox} is missing its centre");
                Assert.False(m[oy + 1][ox + 1], $"{text}: finder at {oy},{ox} has no inner ring");
            }
            // The timing patterns alternate, and every reader uses them to find the grid.
            for (var i = 8; i < n - 8; i++)
            {
                Assert.Equal(i % 2 == 0, m[6][i]);
                Assert.Equal(i % 2 == 0, m[i][6]);
            }
        }
    }

    [Fact]
    public void The_svg_is_self_contained_and_carries_the_quiet_zone()
    {
        var svg = QrCode.ToSvg("http://192.168.1.50:9333/p/AbCd1234");
        Assert.StartsWith("<svg xmlns=\"http://www.w3.org/2000/svg\"", svg);
        Assert.EndsWith("</svg>", svg);
        Assert.DoesNotContain("http://", svg[40..]);   // the payload is drawn, never written in text
        // 29 modules plus four on each side: a quiet zone is part of the specification, and a QR
        // rendered flush to the edge of a dark panel is one a phone will not see.
        Assert.Contains("viewBox=\"0 0 37 37\"", svg);
    }
}
