#!/bin/sh
# Builds a throw-away console project (outside the repo) that compiles the two Banked/*.cs files plus Program.cs,
# then replays the recorded MAME output through the C# implementation.
set -e
ROOT=$(cd "$(dirname "$0")/../../.." && pwd)
W=${TMPDIR:-/tmp}/banked_cs_harness
mkdir -p "$W"
cat > "$W/h.csproj" <<EOP
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable><ImplicitUsings>disable</ImplicitUsings><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup>
  <ItemGroup>
    <Compile Include="$ROOT/src/System12.Runtime/Banked/*.cs" />
    <Compile Include="$ROOT/tests/py_banked/cs_harness/Program.cs" />
  </ItemGroup>
</Project>
EOP
cd "$W"
dotnet build -c Release -nologo -v q
R="$ROOT/generated/regions"; T="$ROOT/reports/trace_a04"
dotnet bin/Release/net10.0/h.dll ft "$T/ft_ops.lua" "$T/ft_result.txt" "$R/bankedroms.bin" "$R/maincpu_rom.bin"
dotnet bin/Release/net10.0/h.dll ev "$T/run1500/ev_1500.txt" "$T/run1500" "$R/bankedroms.bin" "$R/maincpu_rom.bin"
[ -f "$T/run8k/ev_8k.txt" ] && dotnet bin/Release/net10.0/h.dll ev "$T/run8k/ev_8k.txt" "$T/run8k" "$R/bankedroms.bin" "$R/maincpu_rom.bin"
exit 0
