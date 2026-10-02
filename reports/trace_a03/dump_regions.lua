-- a03: dump MAME's loaded maincpu:rom and bankedroms regions (as seen by region:read_u32 on a LE 32-bit region) for byte-compare with tools/build_regions.py output
local m = manager.machine
local outdir = os.getenv("A03_DUMPDIR") or "."
local function dump(name, file)
  local r = m.memory.regions[name]
  local f = io.open(file, "wb")
  local n = r.size
  local buf = {}
  for off = 0, n - 4, 4 do
    buf[#buf+1] = string.pack("<I4", r:read_u32(off))
    if #buf == 4096 then f:write(table.concat(buf)); buf = {} end
  end
  if #buf > 0 then f:write(table.concat(buf)) end
  f:close()
  print(name, n)
end
dump(":maincpu:rom", outdir.."/mame_maincpu_rom.bin")
dump(":bankedroms", outdir.."/mame_bankedroms.bin")
m:exit()
