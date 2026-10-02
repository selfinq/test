// System 12 GPU support (new code, written against RecompOne commit 8d0091c; MIT-compatible).
//
// A headless, CPU-only IGpuBackend for RecompOne's Gpu front end.  Purpose:
//   * RecompOne's Gpu class never rasterises into its own `Vram` shadow; every primitive is handed to an
//     IGpuBackend (the shipped ones need an OpenGL context).  A software backend is therefore required to
//     test the GP0 path headless and to give the arcade a VRAM that is 1024 x 1024 (2 MB) regardless of the
//     VramShadow constants in RecompOne.
//   * It models the PSX-class GPU as MAME (psx.cpp, "type 2") does for the CXD8654Q: bgr555 + bit 15 mask,
//     VRAM rows addressed with & 1023, CLUT Y = (clut >> 6) & 0x3ff, tpage Y base from bit 4 (+256) and bit 11 (+512).
//
// Accuracy: NOT bit-exact.  No dithering, affine (not perspective) texturing, simplified edge rules; semi-transparent
// modes follow the usual PSX formulas.  Good enough for tests/diagnostics, not a replacement for a reviewed renderer.

using RecompOne.Runtime.Hle;

namespace System12.Runtime.Gpu;

public sealed class SoftwareGpuBackend : IGpuBackend
{
    public const int Width = 1024;
    public const int Height = 1024;          // CXD8654Q board: 2 MB SGRAM = 1024 x 1024 x 16 bit
    public const int Mask = 1023;

    /// <summary>Raw VRAM, index = y * 1024 + x, bgr555 in bits 0..14, mask bit in bit 15.</summary>
    public readonly ushort[] Vram = new ushort[Width * Height];

    private HleDrawEnv _env = new() { ClipX0 = 0, ClipY0 = 0, ClipX1 = Width - 1, ClipY1 = Height - 1 };

    public bool Ready => true;
    public HleDispEnv LastDisplay { get; private set; }
    public int PresentCount { get; private set; }
    public long PixelsWritten { get; private set; }

    public ushort this[int x, int y]
    {
        get => Vram[(y & Mask) * Width + (x & Mask)];
        set => Vram[(y & Mask) * Width + (x & Mask)] = value;
    }

    public void SetDrawEnv(in HleDrawEnv env)
    {
        _env = env;
    }

    // ------------------------------------------------------------------ transfers

    public void FillRect(int x, int y, int w, int h, ushort color15)
    {
        // GP0(02): not affected by mask bits / draw area; MAME-style coordinates wrap at 1024.
        for (var dy = 0; dy < h; dy++)
        for (var dx = 0; dx < w; dx++)
            this[x + dx, y + dy] = color15;
    }

    public void CopyVram(int sx, int sy, int dx, int dy, int w, int h)
    {
        var tmp = new ushort[Math.Max(0, w * h)];
        for (var r = 0; r < h; r++)
        for (var c = 0; c < w; c++)
            tmp[r * w + c] = this[sx + c, sy + r];
        for (var r = 0; r < h; r++)
        for (var c = 0; c < w; c++)
            PutMasked(dx + c, dy + r, tmp[r * w + c]);
    }

    public void WriteVram(int x, int y, int w, int h, ReadOnlySpan<ushort> px)
    {
        for (var r = 0; r < h; r++)
        for (var c = 0; c < w; c++)
        {
            var i = r * w + c;
            if (i >= px.Length) return;
            PutMasked(x + c, y + r, px[i]);
        }
    }

    public void ReadVram(int x, int y, int w, int h, Span<ushort> px)
    {
        for (var r = 0; r < h; r++)
        for (var c = 0; c < w; c++)
        {
            var i = r * w + c;
            if (i >= px.Length) return;
            px[i] = this[x + c, y + r];
        }
    }

    private void PutMasked(int x, int y, ushort v)
    {
        ref var d = ref Vram[(y & Mask) * Width + (x & Mask)];
        if (_env.CheckMask && (d & 0x8000) != 0) return;
        d = _env.SetMask ? (ushort)(v | 0x8000) : v;
    }

    public int RegisterImage(ReadOnlySpan<byte> rgba, int width, int height) => 0;
    public void Flush() { }

    public void Present(in HleDispEnv disp)
    {
        LastDisplay = disp;
        PresentCount++;
    }

    // ------------------------------------------------------------------ pixel pipeline

    private static ushort Pack(int r, int g, int b)
    {
        r = Math.Clamp(r, 0, 31);
        g = Math.Clamp(g, 0, 31);
        b = Math.Clamp(b, 0, 31);
        return (ushort)(r | (g << 5) | (b << 10));
    }

    private void Plot(int x, int y, int r8, int g8, int b8, bool semi, int blend, ushort texel, bool textured, bool raw)
    {
        if (x < _env.ClipX0 || x > _env.ClipX1 || y < _env.ClipY0 || y > _env.ClipY1) return;
        if (x < 0 || x >= Width || y < 0 || y >= Height) return;

        int r, g, b;
        var stp = false;
        if (textured)
        {
            var tr = texel & 31;
            var tg = (texel >> 5) & 31;
            var tb = (texel >> 10) & 31;
            stp = (texel & 0x8000) != 0;
            if (raw)
            {
                r = tr; g = tg; b = tb;
            }
            else
            {
                // modulate: texel * colour / 128, 5-bit result
                r = Math.Min(31, (tr * r8) >> 7);
                g = Math.Min(31, (tg * g8) >> 7);
                b = Math.Min(31, (tb * b8) >> 7);
            }
        }
        else
        {
            r = r8 >> 3; g = g8 >> 3; b = b8 >> 3;
        }

        ref var dst = ref Vram[y * Width + x];
        if (_env.CheckMask && (dst & 0x8000) != 0) return;

        var doBlend = semi && (!textured || stp);
        if (doBlend)
        {
            int br = dst & 31, bg = (dst >> 5) & 31, bb = (dst >> 10) & 31;
            switch (blend & 3)
            {
                case 0: r = (br + r) >> 1; g = (bg + g) >> 1; b = (bb + b) >> 1; break;
                case 1: r += br; g += bg; b += bb; break;
                case 2: r = br - r; g = bg - g; b = bb - b; break;
                default: r = br + (r >> 2); g = bg + (g >> 2); b = bb + (b >> 2); break;
            }
        }

        var v = Pack(r, g, b);
        if (_env.SetMask || (textured && stp)) v |= 0x8000;
        dst = v;
        PixelsWritten++;
    }

    // ------------------------------------------------------------------ texture fetch

    private ushort FetchTexel(in PrimFlags f, int u, int v)
    {
        // texture window (units of 8 px)
        if (_env.TwMaskX != 0 || _env.TwMaskY != 0)
        {
            u = (u & ~(_env.TwMaskX * 8)) | ((_env.TwOffX & _env.TwMaskX) * 8);
            v = (v & ~(_env.TwMaskY * 8)) | ((_env.TwOffY & _env.TwMaskY) * 8);
        }

        u &= 255; v &= 255;
        var baseX = (f.TPage & 0xF) * 64;
        // Y base: bit 4 -> +256, bit 11 -> +512 (2 MB GPUs; see docs/system12/GPU.md and patches/0004)
        var baseY = ((f.TPage >> 4) & 1) * 256 + ((f.TPage >> 11) & 1) * 512;
        var depth = (f.TPage >> 7) & 3;
        var y = (baseY + v) & Mask;
        switch (depth)
        {
            case 0:
            {
                var w = Vram[y * Width + ((baseX + (u >> 2)) & Mask)];
                return Clut(f, (w >> ((u & 3) * 4)) & 0xF);
            }
            case 1:
            {
                var w = Vram[y * Width + ((baseX + (u >> 1)) & Mask)];
                return Clut(f, (w >> ((u & 1) * 8)) & 0xFF);
            }
            default:
                return Vram[y * Width + ((baseX + u) & Mask)];
        }
    }

    private ushort Clut(in PrimFlags f, int index)
    {
        var cx = (f.Clut & 0x3F) * 16;
        var cy = (f.Clut >> 6) & 0x3FF; // MAME: (clut >> 6) & 0x3ff
        return Vram[cy * Width + ((cx + index) & Mask)];
    }

    // ------------------------------------------------------------------ primitives

    public void DrawRect(in HleRect r, in PrimFlags f)
    {
        var x0 = (int)MathF.Round(r.X);
        var y0 = (int)MathF.Round(r.Y);
        for (var dy = 0; dy < r.H; dy++)
        for (var dx = 0; dx < r.W; dx++)
        {
            ushort texel = 0;
            if (f.Textured)
            {
                texel = FetchTexel(f, r.U + dx, r.V + dy);
                if (texel == 0) continue;
            }
            Plot(x0 + dx, y0 + dy, r.R, r.G, r.B, f.SemiTrans, f.BlendMode, texel, f.Textured, f.RawTexture);
        }
    }

    public void DrawLine(in HleVertex a, in HleVertex b, in PrimFlags f)
    {
        int x0 = (int)MathF.Round(a.X), y0 = (int)MathF.Round(a.Y);
        int x1 = (int)MathF.Round(b.X), y1 = (int)MathF.Round(b.Y);
        var n = Math.Max(Math.Abs(x1 - x0), Math.Abs(y1 - y0));
        if (n == 0)
        {
            Plot(x0, y0, a.R, a.G, a.B, f.SemiTrans, f.BlendMode, 0, false, false);
            return;
        }
        for (var i = 0; i <= n; i++)
        {
            var t = (float)i / n;
            var x = (int)MathF.Round(x0 + (x1 - x0) * t);
            var y = (int)MathF.Round(y0 + (y1 - y0) * t);
            int r = f.Gouraud ? (int)(a.R + (b.R - a.R) * t) : a.R;
            int g = f.Gouraud ? (int)(a.G + (b.G - a.G) * t) : a.G;
            int bl = f.Gouraud ? (int)(a.B + (b.B - a.B) * t) : a.B;
            Plot(x, y, r, g, bl, f.SemiTrans, f.BlendMode, 0, false, false);
        }
    }

    public void DrawTri(in HleVertex a, in HleVertex b, in HleVertex c, in PrimFlags f)
    {
        float ax = MathF.Round(a.X), ay = MathF.Round(a.Y);
        float bx = MathF.Round(b.X), by = MathF.Round(b.Y);
        float cx = MathF.Round(c.X), cy = MathF.Round(c.Y);

        var area = (bx - ax) * (cy - ay) - (by - ay) * (cx - ax);
        if (area == 0f) return;

        var minX = (int)Math.Max(Math.Min(ax, Math.Min(bx, cx)), Math.Max(_env.ClipX0, 0));
        var maxX = (int)Math.Min(Math.Max(ax, Math.Max(bx, cx)), Math.Min(_env.ClipX1, Width - 1));
        var minY = (int)Math.Max(Math.Min(ay, Math.Min(by, cy)), Math.Max(_env.ClipY0, 0));
        var maxY = (int)Math.Min(Math.Max(ay, Math.Max(by, cy)), Math.Min(_env.ClipY1, Height - 1));

        // top-left fill rule via bias on edge functions (area sign normalised)
        var sign = area > 0 ? 1f : -1f;
        for (var y = minY; y <= maxY; y++)
        for (var x = minX; x <= maxX; x++)
        {
            var w0 = sign * Edge(bx, by, cx, cy, x, y);
            var w1 = sign * Edge(cx, cy, ax, ay, x, y);
            var w2 = sign * Edge(ax, ay, bx, by, x, y);
            if (!Inside(w0, bx, by, cx, cy, sign)) continue;
            if (!Inside(w1, cx, cy, ax, ay, sign)) continue;
            if (!Inside(w2, ax, ay, bx, by, sign)) continue;

            var inv = 1f / (MathF.Abs(area));
            var l0 = w0 * inv; var l1 = w1 * inv; var l2 = w2 * inv;

            int r, g, bl;
            if (f.Gouraud)
            {
                r = (int)(a.R * l0 + b.R * l1 + c.R * l2);
                g = (int)(a.G * l0 + b.G * l1 + c.G * l2);
                bl = (int)(a.B * l0 + b.B * l1 + c.B * l2);
            }
            else
            {
                r = a.R; g = a.G; bl = a.B;
            }

            ushort texel = 0;
            if (f.Textured)
            {
                var u = (int)MathF.Floor(a.U * l0 + b.U * l1 + c.U * l2 + 0.0001f);
                var v = (int)MathF.Floor(a.V * l0 + b.V * l1 + c.V * l2 + 0.0001f);
                texel = FetchTexel(f, u, v);
                if (texel == 0) continue;
            }

            Plot(x, y, r, g, bl, f.SemiTrans, f.BlendMode, texel, f.Textured, f.RawTexture);
        }
    }

    private static float Edge(float x0, float y0, float x1, float y1, float px, float py)
        => (x1 - x0) * (py - y0) - (y1 - y0) * (px - x0);

    // pixel is inside if edge value > 0, or == 0 on a top/left edge (orientation normalised by `sign`).
    private static bool Inside(float w, float x0, float y0, float x1, float y1, float sign)
    {
        if (w > 0f) return true;
        if (w < 0f) return false;
        var dx = (x1 - x0) * sign;
        var dy = (y1 - y0) * sign;
        // interior has w > 0.  With y pointing down: a top edge runs left->right (dy == 0, dx > 0) with the triangle
        // below it, a left edge runs bottom->top (dy < 0).  Pixels exactly on those edges belong to the triangle.
        return dy < 0f || (dy == 0f && dx > 0f);
    }

    // ------------------------------------------------------------------ display read-out

    /// <summary>
    /// Convert the display area to RGBA8888 the way MAME's update_screen does (15 bit direct, or 24 bit packed in
    /// 16-bit words).  <paramref name="widthPixels"/> is in output pixels; for 24 bit each pixel uses 1.5 VRAM words.
    /// </summary>
    public byte[] ReadDisplayRgba(int startX, int startY, int widthPixels, int heightLines, bool rgb24)
    {
        var o = new byte[widthPixels * heightLines * 4];
        for (var y = 0; y < heightLines; y++)
        {
            var row = (startY + y) & Mask;
            for (var x = 0; x < widthPixels; x++)
            {
                byte r, g, b;
                if (!rgb24)
                {
                    var p = Vram[row * Width + ((startX + x) & Mask)];
                    r = P5(p & 31); g = P5((p >> 5) & 31); b = P5((p >> 10) & 31);
                }
                else
                {
                    var byteX = startX * 2 + x * 3; // byte offset inside the row (little endian halfwords)
                    r = Byte(row, byteX); g = Byte(row, byteX + 1); b = Byte(row, byteX + 2);
                }
                var i = (y * widthPixels + x) * 4;
                o[i] = r; o[i + 1] = g; o[i + 2] = b; o[i + 3] = 255;
            }
        }
        return o;
    }

    // MAME pal5bit(): (v << 3) | (v >> 2)
    private static byte P5(int v) => (byte)((v << 3) | (v >> 2));

    private byte Byte(int row, int byteX)
    {
        var w = Vram[row * Width + ((byteX >> 1) & Mask)];
        return (byte)((byteX & 1) == 0 ? w & 0xFF : w >> 8);
    }
}
