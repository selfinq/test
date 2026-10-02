#!/usr/bin/env bash
# Build and run the System 12 GPU self test against a RecompOne.Runtime source tree.
#   run_selftest.sh [RECOMPONE_RUNTIME_DIR] [patched]
# RECOMPONE_RUNTIME_DIR defaults to /home/user/work/recompone/RecompOne.Runtime (copied, never modified or built in place).
# "patched" additionally builds with -p:S12Vram=1 (needs a tree that carries patches/0004-gpu-vram.md).
# Env S12_RUN_ARGS="<vram.bin> <x> <y> <w> <h> <rgb24> <out.rgba>" runs System12DisplayTool instead (see tools/compare_display.py).
# Exit code = number of required checks that failed (XFAIL on an unpatched tree does not count).
set -euo pipefail
here="$(cd "$(dirname "$0")/.." && pwd)"
rc="${1:-/home/user/work/recompone/RecompOne.Runtime}"
mode="${2:-}"
work="$(mktemp -d "${TMPDIR:-/tmp}/s12gpu_selftest.XXXXXX")"
mkdir -p "$work/rt"
(cd "$(dirname "$rc")" && tar cf - --exclude=bin --exclude=obj "$(basename "$rc")") | (cd "$work/rt" && tar xf -)
rtdir="$work/rt/$(basename "$rc")"
cat > "$work/S12GpuSelfTest.csproj" <<XML
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="$here/*.cs" />
    <Compile Include="Program.cs" />
    <ProjectReference Include="$rtdir/RecompOne.Runtime.csproj" />
  </ItemGroup>
</Project>
XML
cat > "$work/Program.cs" <<'CS'
return args.Length > 0 ? System12.Runtime.Gpu.System12DisplayTool.Run(args) : System12.Runtime.Gpu.System12GpuSelfTest.Run(Console.Out);
CS
props=()
[ "$mode" = "patched" ] && props+=("-p:S12Vram=1")
cd "$work"
dotnet build -c Release -nologo -v q "${props[@]}" 2>&1 | tail -20
# S12_RUN_ARGS (space separated) switches to the offline display tool instead of the self test
dotnet "$work/bin/Release/net10.0/S12GpuSelfTest.dll" ${S12_RUN_ARGS:-}
