-- dma_hash_selftest.lua : runs the dma_hash() function of mame_capture.lua inside MAME on a synthetic RAM image and
-- writes the results, so that tests/difftest can compare them with the Python definition (snapshotlib.dma_summary).
-- Environment: DT_SELFTEST_INPUT (a Lua file returning {words={[addr]=value}, cases={{chcr=,madr=,bcr=},...}}),
--              DT_SELFTEST_OUT (output file), DT_CAPTURE_LUA (path of mame_capture.lua)
local m = manager.machine
local cpu = m.devices[":maincpu"]
local mem = cpu.spaces["program"]
local f = io.open(os.getenv("DT_CAPTURE_LUA"), "r"); local src = f:read("a"); f:close()
local body = src:match("%-%- BEGIN dma_hash[^\n]*\n(.-)%-%- END dma_hash")
assert(body, "dma_hash markers not found")
local RAM_PHYS_MASK_UNUSED
local chunk = assert(load(body .. "\nreturn dma_hash", "dma_hash", "t", { mem = mem }))
local dma_hash = chunk()
local input = dofile(os.getenv("DT_SELFTEST_INPUT"))
local done = false
emu.register_frame_done(function()
  if done then return end
  done = true
  for a, w in pairs(input.words) do mem:write_u32(a, w) end
  local out = io.open(os.getenv("DT_SELFTEST_OUT"), "w")
  for i, c in ipairs(input.cases) do
    local n, h = dma_hash(c.chcr, c.madr, c.bcr)
    out:write(string.format("%d %d %016x\n", i, n, h))
  end
  out:close()
  m:exit()
end)
