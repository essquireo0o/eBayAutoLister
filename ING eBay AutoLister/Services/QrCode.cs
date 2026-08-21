using System.Text;

namespace ING_eBay_AutoLister.Services;

/// <summary>
/// A QR code, small enough to hold a LAN URL, rendered as SVG.
/// </summary>
/// <remarks>
/// <para>
/// Written here rather than taken from a package because the one thing this app needs a QR code for
/// — pointing a phone at the Photo Box capture page — must work on a machine with no internet, and
/// because a barcode is a specification, not an opinion: byte mode, error-correction level M,
/// versions 1 to 10. That is 213 bytes at the top end, and the URLs are around forty.
/// </para>
/// <para>
/// The implementation follows ISO/IEC 18004. It is verified against an independent encoder (segno)
/// module-for-module in <c>QrCodeTests</c>, which is the only honest way to know a barcode is right:
/// a QR that is subtly wrong still renders as a convincing square of dots.
/// </para>
/// </remarks>
public static class QrCode
{
    // Per version (1-10) at EC level M: total codewords, EC codewords per block,
    // group-1 block count, group-1 data codewords, group-2 block count, group-2 data codewords.
    private static readonly int[][] SpecM =
    [
        //         total  ecPerBlk  g1  g1dc  g2  g2dc
        [  0,  0, 0,  0, 0,  0],   // index 0 unused
        [ 26, 10, 1, 16, 0,  0],   // v1
        [ 44, 16, 1, 28, 0,  0],   // v2
        [ 70, 26, 1, 44, 0,  0],   // v3
        [100, 18, 2, 32, 0,  0],   // v4
        [134, 24, 2, 43, 0,  0],   // v5
        [172, 16, 4, 27, 0,  0],   // v6
        [196, 18, 4, 31, 0,  0],   // v7
        [242, 22, 2, 38, 2, 39],   // v8
        [292, 22, 3, 36, 2, 37],   // v9
        [346, 26, 4, 43, 1, 44],   // v10
    ];

    private static readonly int[][] AlignCenters =
    [
        [], [], [6, 18], [6, 22], [6, 26], [6, 30], [6, 34],
        [6, 22, 38], [6, 24, 42], [6, 26, 46], [6, 28, 50],
    ];

    // 15-bit format strings for EC level M, masks 0-7 (ISO/IEC 18004 Table C.1).
    private static readonly int[] FormatM =
    [
        0b101010000010010, 0b101000100100101, 0b101111001111100, 0b101101101001011,
        0b100010111111001, 0b100000011001110, 0b100111110010111, 0b100101010100000,
    ];

    // 18-bit version strings, versions 7-10 (versions below 7 carry none).
    private static readonly Dictionary<int, int> VersionBits = new()
    {
        [7]  = 0b000111110010010100,
        [8]  = 0b001000010110111100,
        [9]  = 0b001001101010011001,
        [10] = 0b001010010011010011,
    };

    /// <summary>The finished symbol: <c>Modules[y][x]</c> is true where a module is dark.</summary>
    public sealed record Symbol(bool[][] Modules, int Version)
    {
        public int Size => Modules.Length;
    }

    /// <summary>
    /// Encodes <paramref name="text"/>, or throws when it does not fit version 10.
    /// <paramref name="forceMask"/> exists so the tests can compare this encoder against an
    /// independent one mask-for-mask; production always lets the penalty rules choose.
    /// </summary>
    public static Symbol Encode(string text, int? forceMask = null)
    {
        var data = Encoding.UTF8.GetBytes(text);

        var version = 0;
        for (var v = 1; v <= 10; v++)
        {
            var spec = SpecM[v];
            var capacity = spec[2] * spec[3] + spec[4] * spec[5];
            var headerBits = 4 + (v <= 9 ? 8 : 16);
            if (data.Length + (headerBits + 7) / 8 <= capacity) { version = v; break; }
        }
        if (version == 0)
            throw new ArgumentException($"{data.Length} bytes is more than a version-10 QR code holds.", nameof(text));

        var codewords = BuildCodewords(data, version);
        var size = 17 + version * 4;

        // Two planes: the modules themselves, and which positions are function patterns —
        // masking must skip those, and the data walk must not overwrite them.
        var modules = NewGrid(size);
        var reserved = NewGrid(size);
        DrawFunctionPatterns(modules, reserved, version);
        DrawData(modules, reserved, codewords, size);

        var mask = forceMask ?? BestMask(modules, reserved, size, version);
        ApplyMask(modules, reserved, size, mask);
        DrawFormat(modules, size, mask);
        if (version >= 7) DrawVersion(modules, size, version);

        return new Symbol(modules, version);
    }

    /// <summary>The symbol as a standalone SVG, sized in CSS pixels, with a 4-module quiet zone.</summary>
    public static string ToSvg(string text, int pixels = 220, string dark = "#0e3d42", string light = "#ffffff")
    {
        var sym = Encode(text);
        var n = sym.Size + 8;   // the quiet zone is part of the specification, not decoration
        var sb = new StringBuilder();
        sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{pixels}\" height=\"{pixels}\" viewBox=\"0 0 {n} {n}\" shape-rendering=\"crispEdges\">");
        sb.Append($"<rect width=\"{n}\" height=\"{n}\" fill=\"{light}\"/>");
        sb.Append($"<path fill=\"{dark}\" d=\"");
        for (var y = 0; y < sym.Size; y++)
            for (var x = 0; x < sym.Size; x++)
                if (sym.Modules[y][x])
                    sb.Append($"M{x + 4} {y + 4}h1v1h-1z");
        sb.Append("\"/></svg>");
        return sb.ToString();
    }

    private static bool[][] NewGrid(int size)
    {
        var g = new bool[size][];
        for (var i = 0; i < size; i++) g[i] = new bool[size];
        return g;
    }

    // ── Data → codewords ─────────────────────────────────────────────────────────
    private static byte[] BuildCodewords(byte[] data, int version)
    {
        var spec = SpecM[version];
        int ecPerBlock = spec[1], g1 = spec[2], g1dc = spec[3], g2 = spec[4], g2dc = spec[5];
        var dataCapacity = g1 * g1dc + g2 * g2dc;

        var bits = new BitSink();
        bits.Push(0b0100, 4);                              // byte mode
        bits.Push(data.Length, version <= 9 ? 8 : 16);     // character count
        foreach (var b in data) bits.Push(b, 8);

        // Terminator, then pad to a byte boundary, then the specified alternating pad bytes.
        var remaining = dataCapacity * 8 - bits.Length;
        bits.Push(0, Math.Min(4, remaining));
        if (bits.Length % 8 != 0) bits.Push(0, 8 - bits.Length % 8);
        var pad = new[] { 0xEC, 0x11 };
        for (var i = 0; bits.Length < dataCapacity * 8; i++) bits.Push(pad[i % 2], 8);

        var all = bits.ToBytes();

        // Split into blocks, compute EC for each, then interleave data and EC as the spec requires.
        var blocks = new List<byte[]>();
        var ecBlocks = new List<byte[]>();
        var offset = 0;
        for (var i = 0; i < g1 + g2; i++)
        {
            var len = i < g1 ? g1dc : g2dc;
            var block = all[offset..(offset + len)];
            offset += len;
            blocks.Add(block);
            ecBlocks.Add(ReedSolomon(block, ecPerBlock));
        }

        var outCw = new List<byte>();
        var maxData = Math.Max(g1dc, g2 == 0 ? 0 : g2dc);
        for (var i = 0; i < maxData; i++)
            foreach (var b in blocks)
                if (i < b.Length) outCw.Add(b[i]);
        for (var i = 0; i < ecPerBlock; i++)
            foreach (var e in ecBlocks)
                outCw.Add(e[i]);

        return [.. outCw];
    }

    private sealed class BitSink
    {
        private readonly List<bool> _bits = [];
        public int Length => _bits.Count;
        public void Push(int value, int count)
        {
            for (var i = count - 1; i >= 0; i--) _bits.Add(((value >> i) & 1) == 1);
        }
        public byte[] ToBytes()
        {
            var bytes = new byte[(_bits.Count + 7) / 8];
            for (var i = 0; i < _bits.Count; i++)
                if (_bits[i]) bytes[i / 8] |= (byte)(1 << (7 - i % 8));
            return bytes;
        }
    }

    // ── Reed-Solomon over GF(256), the QR generator polynomial ───────────────────
    private static readonly byte[] Exp = new byte[512];
    private static readonly byte[] Log = new byte[256];

    static QrCode()
    {
        var x = 1;
        for (var i = 0; i < 255; i++)
        {
            Exp[i] = (byte)x;
            Log[x] = (byte)i;
            x <<= 1;
            if (x >= 256) x ^= 0x11D;   // the QR field's primitive polynomial
        }
        for (var i = 255; i < 512; i++) Exp[i] = Exp[i - 255];
    }

    private static byte Mul(byte a, byte b) => a == 0 || b == 0 ? (byte)0 : Exp[Log[a] + Log[b]];

    private static byte[] ReedSolomon(byte[] data, int ecLength)
    {
        // Generator polynomial for ecLength check symbols.
        var gen = new byte[] { 1 };
        for (var i = 0; i < ecLength; i++)
        {
            var next = new byte[gen.Length + 1];
            for (var j = 0; j < gen.Length; j++)
            {
                next[j] ^= gen[j];
                next[j + 1] ^= Mul(gen[j], Exp[i]);
            }
            gen = next;
        }

        var remainder = new byte[ecLength];
        foreach (var d in data)
        {
            var factor = (byte)(d ^ remainder[0]);
            Array.Copy(remainder, 1, remainder, 0, ecLength - 1);
            remainder[ecLength - 1] = 0;
            for (var i = 0; i < ecLength; i++) remainder[i] ^= Mul(gen[i + 1], factor);
        }
        return remainder;
    }

    // ── The matrix ───────────────────────────────────────────────────────────────
    private static void DrawFunctionPatterns(bool[][] m, bool[][] res, int version)
    {
        var size = m.Length;

        void Finder(int ox, int oy)
        {
            for (var y = -1; y <= 7; y++)
                for (var x = -1; x <= 7; x++)
                {
                    int px = ox + x, py = oy + y;
                    if (px < 0 || py < 0 || px >= size || py >= size) continue;
                    var dark = x >= 0 && x <= 6 && (y == 0 || y == 6)
                            || y >= 0 && y <= 6 && (x == 0 || x == 6)
                            || x >= 2 && x <= 4 && y >= 2 && y <= 4;
                    m[py][px] = dark;
                    res[py][px] = true;
                }
        }
        Finder(0, 0); Finder(size - 7, 0); Finder(0, size - 7);

        for (var i = 8; i < size - 8; i++)
        {
            var dark = i % 2 == 0;
            m[6][i] = dark; res[6][i] = true;
            m[i][6] = dark; res[i][6] = true;
        }

        var centers = AlignCenters[version];
        foreach (var cy in centers)
            foreach (var cx in centers)
            {
                // Alignment patterns never overlap a finder.
                if (cx <= 8 && cy <= 8) continue;
                if (cx >= size - 9 && cy <= 8) continue;
                if (cx <= 8 && cy >= size - 9) continue;
                for (var dy = -2; dy <= 2; dy++)
                    for (var dx = -2; dx <= 2; dx++)
                    {
                        m[cy + dy][cx + dx] = Math.Abs(dx) == 2 || Math.Abs(dy) == 2 || (dx == 0 && dy == 0);
                        res[cy + dy][cx + dx] = true;
                    }
            }

        // Format-information area is reserved before the data walk; the bits land later.
        for (var i = 0; i < 9; i++)
        {
            if (i != 6) { res[8][i] = true; res[i][8] = true; }
        }
        for (var i = 0; i < 8; i++)
        {
            res[8][size - 1 - i] = true;
            res[size - 1 - i][8] = true;
        }
        m[size - 8][8] = true;          // the always-dark module
        res[size - 8][8] = true;

        if (version >= 7)
            for (var i = 0; i < 6; i++)
                for (var j = 0; j < 3; j++)
                {
                    res[size - 11 + j][i] = true;
                    res[i][size - 11 + j] = true;
                }
    }

    private static void DrawData(bool[][] m, bool[][] res, byte[] codewords, int size)
    {
        var bit = 0;
        var total = codewords.Length * 8;
        var upward = true;
        for (var right = size - 1; right >= 1; right -= 2)
        {
            if (right == 6) right = 5;   // the vertical timing column is skipped entirely
            for (var i = 0; i < size; i++)
            {
                var y = upward ? size - 1 - i : i;
                for (var c = 0; c < 2; c++)
                {
                    var x = right - c;
                    if (res[y][x]) continue;
                    if (bit < total)
                        m[y][x] = ((codewords[bit / 8] >> (7 - bit % 8)) & 1) == 1;
                    bit++;
                }
            }
            upward = !upward;
        }
    }

    private static bool MaskAt(int mask, int x, int y) => mask switch
    {
        0 => (y + x) % 2 == 0,
        1 => y % 2 == 0,
        2 => x % 3 == 0,
        3 => (y + x) % 3 == 0,
        4 => (y / 2 + x / 3) % 2 == 0,
        5 => y * x % 2 + y * x % 3 == 0,
        6 => (y * x % 2 + y * x % 3) % 2 == 0,
        _ => ((y + x) % 2 + y * x % 3) % 2 == 0,
    };

    private static void ApplyMask(bool[][] m, bool[][] res, int size, int mask)
    {
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
                if (!res[y][x] && MaskAt(mask, x, y))
                    m[y][x] = !m[y][x];
    }

    private static int BestMask(bool[][] m, bool[][] res, int size, int version)
    {
        var best = 0;
        var bestScore = int.MaxValue;
        for (var mask = 0; mask < 8; mask++)
        {
            var trial = NewGrid(size);
            for (var y = 0; y < size; y++) Array.Copy(m[y], trial[y], size);
            ApplyMask(trial, res, size, mask);
            DrawFormat(trial, size, mask);
            if (version >= 7) DrawVersion(trial, size, version);
            var score = Penalty(trial, size);
            if (score < bestScore) { bestScore = score; best = mask; }
        }
        return best;
    }

    private static int Penalty(bool[][] m, int size)
    {
        var score = 0;

        // Rule 1 — runs of five or more of the same colour, in both directions.
        for (var i = 0; i < size; i++)
            for (var dir = 0; dir < 2; dir++)
            {
                var run = 1;
                for (var j = 1; j < size; j++)
                {
                    var a = dir == 0 ? m[i][j - 1] : m[j - 1][i];
                    var b = dir == 0 ? m[i][j] : m[j][i];
                    if (a == b) { run++; if (run == 5) score += 3; else if (run > 5) score++; }
                    else run = 1;
                }
            }

        // Rule 2 — every 2x2 block of one colour.
        for (var y = 0; y < size - 1; y++)
            for (var x = 0; x < size - 1; x++)
                if (m[y][x] == m[y][x + 1] && m[y][x] == m[y + 1][x] && m[y][x] == m[y + 1][x + 1])
                    score += 3;

        // Rule 3 — the finder-like 1:1:3:1:1 sequence with four light modules beside it.
        bool[] p1 = [true, false, true, true, true, false, true, false, false, false, false];
        bool[] p2 = [false, false, false, false, true, false, true, true, true, false, true];
        for (var y = 0; y < size; y++)
            for (var x = 0; x <= size - 11; x++)
            {
                var h1 = true; var h2 = true; var v1 = true; var v2 = true;
                for (var k = 0; k < 11; k++)
                {
                    if (m[y][x + k] != p1[k]) h1 = false;
                    if (m[y][x + k] != p2[k]) h2 = false;
                    if (m[x + k][y] != p1[k]) v1 = false;
                    if (m[x + k][y] != p2[k]) v2 = false;
                }
                if (h1) score += 40;
                if (h2) score += 40;
                if (v1) score += 40;
                if (v2) score += 40;
            }

        // Rule 4 — deviation from an even balance of dark and light.
        var dark = 0;
        foreach (var row in m) foreach (var v in row) if (v) dark++;
        var percent = dark * 100 / (size * size);
        score += Math.Abs(percent - 50) / 5 * 10;

        return score;
    }

    private static void DrawFormat(bool[][] m, int size, int mask)
    {
        var bits = FormatM[mask];

        // Position p runs 0-14 and carries the format string most-significant bit first.
        // Copy one starts at (row 8, col 0), runs right along the bottom of the top-left
        // finder, turns at the corner and climbs column 8. Copy two starts at the bottom
        // of column 8 and climbs, then continues along row 8 from the right-hand finder.
        // Both were transposed here once, which is invisible — the symbol still looks like
        // a QR code, and no scanner can read it, because the format tells the reader which
        // mask and error level everything else used.
        for (var p = 0; p < 15; p++)
        {
            var on = ((bits >> (14 - p)) & 1) == 1;

            if (p < 6) m[8][p] = on;
            else if (p == 6) m[8][7] = on;
            else if (p == 7) m[8][8] = on;
            else if (p == 8) m[7][8] = on;
            else m[14 - p][8] = on;

            if (p < 7) m[size - 1 - p][8] = on;
            else m[8][size - 15 + p] = on;
        }
    }

    private static void DrawVersion(bool[][] m, int size, int version)
    {
        var bits = VersionBits[version];
        for (var i = 0; i < 18; i++)
        {
            var on = ((bits >> i) & 1) == 1;
            int a = i / 3, b = size - 11 + i % 3;
            m[b][a] = on;
            m[a][b] = on;
        }
    }
}
