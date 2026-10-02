# Patch proposal 0004: 2 MB (1024 x 1024) VRAM for the System 12 GPU

Status: **proposal, tested headless only**. Not applied to any RecompOne checkout by this project (the checkout at
`/home/user/work/recompone` is untouched). Base: RecompOne `8d0091cf4877b27c2d526106a78c75f6b0ef97b2` (MIT).
Like patch 0001 this is for this project's private fork; it is not meant for upstream submission.

## Why a fork is unavoidable

RecompOne hard-codes a 1024 x 512 VRAM:

* `VramShadow.Height` is a compile-time `const int = 512` (`RecompOne.Runtime/Gpu/Hle/VramShadow.cs`). `Gpu.VramHeight` aliases it,
  and every GP0 transfer decoder masks Y with `0x1FF` (`GpuCommands.cs`: fill, VRAM copy, image load, image read; `Gpu.cs`: GP1(05) display start).
* GP0(E1) bit 11 is decoded as PS1 "texture disable" and the texture page Y base has a single bit (bit 4, +256).
  The tektagt board's GPU (CXD8654Q, 2 MB SGRAM) uses bit 11 as a second Y bit (+512), as MAME's `psx.cpp` models it.
* A `const` cannot be changed by a runtime adapter, so `System12GpuAdapter` can only *detect* the situation
  (`System12GpuAdapter.ArcadeVramSupported`) and report it.

Evidence that tektagt really uses the upper half of VRAM (MAME 0.264 probe `src/System12.Runtime/Gpu/mame/hw_usage.lua`, 7000 frames, sampled once per frame):

* display start Y >= 512 from frame 643 on (FMV: GP1(05) Y = 0x200 and 0x2F0, 24 bit, 256x240);
  draw areas at Y 0x2FA..0x3DD during those frames (`vram_dump.lua`, frames 1000-1500);
* GPUSTAT bit 15 (= GP0(E1) bit 11, texture page Y +512): seen together with bit 4 (Y base 768) first at frame 514 (472 of the 7000
  sampled frames) and with bit 4 clear (Y base 512) first at frame 4711 (972 sampled frames).

## Design

* Opt-in: `-p:S12Vram=1` defines `S12_2MB_VRAM`. Without it every changed line evaluates to the old constants
  (`VramHeight - 1 == 0x1FF`, `VramHeight == 0x200`), i.e. retail PS1 behaviour is unchanged. Verified: the patched tree built **without**
  the define passes exactly the same self-test results as the unpatched tree.
* With the define: VRAM is 1024 rows; GP0 Y fields use `VramHeight - 1` (0x3FF); tpage Y base = `bit4 * 256 + bit11 * 512`;
  bit 11 no longer sets `_texDisable`; GPUSTAT bit 15 mirrors bit 11 (MAME type-2 status layout);
  `PrimFlags.TPage` carries the extra Y bit in **bit 11** (bits 0-8 keep their meaning) so a backend can see it.
* Display start Y is 10 bits (MAME `n_displaystarty = (data >> 10) & 1023`).

## Test evidence (headless, `src/System12.Runtime/Gpu/tools/run_selftest.sh`)

| RecompOne tree | build flag | result |
|---|---|---|
| unpatched | none | 36 pass, 0 fail, 5 xfail (arcade rows unreachable), 2 xpass (aliasing coincidences, see GPU.md) |
| patched | none | identical to unpatched (retail behaviour preserved) |
| patched | `-p:S12Vram=1` | 43 pass, 0 fail, 0 xfail |

The 7 `ARCADE` checks are: fill at Y=600, image load/read at Y=700, texture upload at Y=512 not aliasing onto row 0,
textured rect with tpage Y base 512 and 768, GPUSTAT bits 4/15, GP1(05) display start Y 0x2F0.

## NOT covered / NOT tested (all of this is OpenGL- or asset-pipeline-side; no GL context exists in this environment)

These still assume 512 rows and must be revisited before RecompOne's own GL renderer is used for the arcade
(the alternative used so far is `SoftwareGpuBackend`, which is independent of them):

| file | lines (at 8d0091c) | assumption |
|---|---|---|
| `Gpu/Backends/Common/GlCore.cs` | 182 | `_kClipY1 = 511` |
| | 424 | CLUT Y `& 0x1FF` |
| | 535 | `f.TPage & 0x1FF` drops the new bit 11 |
| `Gpu/Backends/Common/GlShaders.cs` | 42, 103, 143, 360, 396 | `& 511`, `& 0x1ff`, `ivec2(1023, 511)`, `mod(..., 512.0)` |
| `Assets/Textures/TextureTile.cs` | 40, 67, 95, 120, 132, 210, 214, 251, 275 | `& 511`, `& 0x1FF` |
| `Assets/Textures/TextureResolver.cs` | 60, 215-220 | `tpage & 0x1FF`, `w/h & 0x1FF` |
| `Gpu/GpuHleForward.cs` | 55, 78 | primitive size limit `spanY > 511` (this is the GPU's own 1023x511 primitive limit; left as is) |

`Gl21/Gl33/Gl45` VRAM textures are created from `VramShadow.Width/Height` and follow the constant automatically.
The PS1 SDK HLE layer (`sdk/LibGpu.cs`) is irrelevant for the arcade (no PS1 BIOS/SDK).

## Diff (apply from the RecompOne repo root: `git apply 0004.diff`; `git apply --check` passed against `8d0091c` + the working tree of `/home/user/work/recompone`)

```diff
diff --git a/RecompOne.Runtime/Assets/Textures/VramTracker.cs b/RecompOne.Runtime/Assets/Textures/VramTracker.cs
--- a/RecompOne.Runtime/Assets/Textures/VramTracker.cs
+++ b/RecompOne.Runtime/Assets/Textures/VramTracker.cs
@@ -5,7 +5,7 @@
     public const int BlockW = 16;
     public const int BlockH = 16;
     public const int Cols = 1024 / BlockW;
-    public const int Rows = 512 / BlockH;
+    public const int Rows = Hle.VramShadow.Height / BlockH;
 
     private static readonly int[] _gen = new int[Cols * Rows];
     private static readonly bool[] _gpuDirty = new bool[Cols * Rows];
diff --git a/RecompOne.Runtime/Gpu/Gpu.cs b/RecompOne.Runtime/Gpu/Gpu.cs
--- a/RecompOne.Runtime/Gpu/Gpu.cs
+++ b/RecompOne.Runtime/Gpu/Gpu.cs
@@ -82,6 +82,9 @@
         uint s = 0;
         s |= (uint)((_texPageX / 64) & 0xF);
         s |= (uint)(((_texPageY / 256) & 1) << 4);
+#if S12_2MB_VRAM
+        if ((_texPageY & 512) != 0) s |= 1u << 15; // MAME type-2: tpage bit 11 (Y base +512) shows in status bit 15
+#endif
         s |= (uint)((_blendMode & 3) << 5);
         s |= (uint)((_texDepth & 3) << 7);
 
@@ -91,7 +94,9 @@
         if (_checkMask) s |= 1u << 12;
         s |= 1u << 13;
 
+#if !S12_2MB_VRAM
         if (_texDisable) s |= 1u << 15;
+#endif
         if (_hres368) s |= 1u << 16;
 
         s |= (uint)((_hres & 3) << 17);
@@ -236,7 +241,7 @@
         {
             case 0x05:
                 _dispVramX = (int)(p & 0x3FF);
-                _dispVramY = (int)((p >> 10) & 0x1FF);
+                _dispVramY = (int)((p >> 10) & (uint)(VramHeight - 1));
                 break;
             case 0x06:
                 _hRange1 = (int)(p & 0xFFF);
diff --git a/RecompOne.Runtime/Gpu/GpuCommands.cs b/RecompOne.Runtime/Gpu/GpuCommands.cs
--- a/RecompOne.Runtime/Gpu/GpuCommands.cs
+++ b/RecompOne.Runtime/Gpu/GpuCommands.cs
@@ -89,10 +89,15 @@
     {
         _texPageX = (int)(word & 0xF) * 64;
         _texPageY = (int)((word >> 4) & 1) * 256;
+#if S12_2MB_VRAM
+        _texPageY += (int)((word >> 11) & 1) * 512; // 2 MB GPU: bit 11 = Y base +512 (not "texture disable")
+#endif
         _blendMode = (int)((word >> 5) & 3);
         _texDepth = (int)((word >> 7) & 3);
         _dither = (word & (1u << 9)) != 0;
+#if !S12_2MB_VRAM
         _texDisable = (word & (1u << 11)) != 0;
+#endif
     }
 
     private void SetTextureWindow(uint word)
@@ -107,9 +112,9 @@
     {
         var color = To15((int)(_fifo[0] & 0xFF), (int)((_fifo[0] >> 8) & 0xFF), (int)((_fifo[0] >> 16) & 0xFF));
         var x = (int)(_fifo[1] & 0x3F0);
-        var y = (int)((_fifo[1] >> 16) & 0x1FF);
+        var y = (int)((_fifo[1] >> 16) & (uint)(VramHeight - 1));
         var w = (int)(((_fifo[2] & 0x3FF) + 0xF) & ~0xF);
-        var h = (int)((_fifo[2] >> 16) & 0x1FF);
+        var h = (int)((_fifo[2] >> 16) & (uint)(VramHeight - 1));
 
         if (x + w <= VramWidth)
             for (var dy = 0; dy < h; dy++)
@@ -130,12 +135,12 @@
 
     private void CopyVramToVram()
     {
-        int sx = (int)(_fifo[1] & 0x3FF), sy = (int)((_fifo[1] >> 16) & 0x1FF);
-        int dx = (int)(_fifo[2] & 0x3FF), dy = (int)((_fifo[2] >> 16) & 0x1FF);
+        int sx = (int)(_fifo[1] & 0x3FF), sy = (int)((_fifo[1] >> 16) & (uint)(VramHeight - 1));
+        int dx = (int)(_fifo[2] & 0x3FF), dy = (int)((_fifo[2] >> 16) & (uint)(VramHeight - 1));
         var w = (int)(_fifo[3] & 0x3FF);
         if (w == 0) w = 0x400;
-        var h = (int)((_fifo[3] >> 16) & 0x1FF);
-        if (h == 0) h = 0x200;
+        var h = (int)((_fifo[3] >> 16) & (uint)(VramHeight - 1));
+        if (h == 0) h = VramHeight;
         for (var row = 0; row < h; row++)
         for (var col = 0; col < w; col++)
         {
@@ -155,15 +160,15 @@
     private void BeginImageLoad()
     {
         _loadX = (int)(_fifo[1] & 0x3FF);
-        _loadY = (int)((_fifo[1] >> 16) & 0x1FF);
+        _loadY = (int)((_fifo[1] >> 16) & (uint)(VramHeight - 1));
         _loadW = (int)(_fifo[2] & 0xFFFF);
         if (_loadW == 0) _loadW = 0x400;
         else _loadW &= 0x3FF;
         if (_loadW == 0) _loadW = 0x400;
         _loadH = (int)((_fifo[2] >> 16) & 0xFFFF);
-        if (_loadH == 0) _loadH = 0x200;
-        else _loadH &= 0x1FF;
-        if (_loadH == 0) _loadH = 0x200;
+        if (_loadH == 0) _loadH = VramHeight;
+        else _loadH &= VramHeight - 1;
+        if (_loadH == 0) _loadH = VramHeight;
         _loadPx = 0;
         _loadImage = true;
         HleLoadBegin();
@@ -193,11 +198,11 @@
     private void BeginImageRead()
     {
         _readX = (int)(_fifo[1] & 0x3FF);
-        _readY = (int)((_fifo[1] >> 16) & 0x1FF);
+        _readY = (int)((_fifo[1] >> 16) & (uint)(VramHeight - 1));
         _readW = (int)(_fifo[2] & 0x3FF);
         if (_readW == 0) _readW = 0x400;
-        _readH = (int)((_fifo[2] >> 16) & 0x1FF);
-        if (_readH == 0) _readH = 0x200;
+        _readH = (int)((_fifo[2] >> 16) & (uint)(VramHeight - 1));
+        if (_readH == 0) _readH = VramHeight;
         _readPx = 0;
         _readImage = true;
         if (HleOn) HleReadback(_readX, _readY, _readW, _readH);
diff --git a/RecompOne.Runtime/Gpu/GpuHleForward.cs b/RecompOne.Runtime/Gpu/GpuHleForward.cs
--- a/RecompOne.Runtime/Gpu/GpuHleForward.cs
+++ b/RecompOne.Runtime/Gpu/GpuHleForward.cs
@@ -9,7 +9,11 @@
     private int CurTPage()
     {
         return ((_texPageX / 64) & 0xf) | (((_texPageY / 256) & 1) << 4)
-                                        | ((_blendMode & 3) << 5) | ((_texDepth & 3) << 7);
+                                        | ((_blendMode & 3) << 5) | ((_texDepth & 3) << 7)
+#if S12_2MB_VRAM
+                                        | (((_texPageY / 512) & 1) << 11)
+#endif
+            ;
     }
 
     private HleDrawEnv CurEnv()
diff --git a/RecompOne.Runtime/Gpu/GpuRaster.cs b/RecompOne.Runtime/Gpu/GpuRaster.cs
--- a/RecompOne.Runtime/Gpu/GpuRaster.cs
+++ b/RecompOne.Runtime/Gpu/GpuRaster.cs
@@ -280,9 +280,14 @@
     {
         _texPageX = (int)(tp & 0xF) * 64;
         _texPageY = (int)((tp >> 4) & 1) * 256;
+#if S12_2MB_VRAM
+        _texPageY += (int)((tp >> 11) & 1) * 512;
+#endif
         _blendMode = (int)((tp >> 5) & 3);
         _texDepth = (int)((tp >> 7) & 3);
+#if !S12_2MB_VRAM
         _texDisable = (tp & (1u << 11)) != 0;
+#endif
     }
 
     private static int CoordX(uint w)
diff --git a/RecompOne.Runtime/Gpu/Hle/VramShadow.cs b/RecompOne.Runtime/Gpu/Hle/VramShadow.cs
--- a/RecompOne.Runtime/Gpu/Hle/VramShadow.cs
+++ b/RecompOne.Runtime/Gpu/Hle/VramShadow.cs
@@ -3,7 +3,11 @@
 public sealed class VramShadow //in ram vram
 {
     public const int Width = 1024;
+#if S12_2MB_VRAM
+    public const int Height = 1024; // System 12 CXD8654Q board: 2 MB VRAM
+#else
     public const int Height = 512;
+#endif
 
     // raw bgr555 buffer, index = y * Width + x
     public readonly ushort[] Pixels = new ushort[Width * Height];
diff --git a/RecompOne.Runtime/RecompOne.Runtime.csproj b/RecompOne.Runtime/RecompOne.Runtime.csproj
--- a/RecompOne.Runtime/RecompOne.Runtime.csproj
+++ b/RecompOne.Runtime/RecompOne.Runtime.csproj
@@ -13,6 +13,10 @@
         <DebugSymbols>false</DebugSymbols>
     </PropertyGroup>
 
+    <PropertyGroup Condition="'$(S12Vram)' == '1'">
+        <DefineConstants>$(DefineConstants);S12_2MB_VRAM</DefineConstants>
+    </PropertyGroup>
+
     <ItemGroup>
         <EmbeddedResource Include="Bios\Fonts\font1.raw"/>
         <EmbeddedResource Include="Bios\Fonts\font2.raw"/>
```
