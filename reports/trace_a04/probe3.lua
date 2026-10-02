local m = manager.machine
local cpu = m.devices[":maincpu"]
local st = cpu.state
local mem = cpu.spaces["program"]
local out = io.open("probe3_log.txt", "w")
local cnt = {}
local frame = 0
local function hook(kind)
  return function(off, data, mask)
    local k = string.format("%s %08x", kind, off)
    local c = (cnt[k] or 0) + 1
    cnt[k] = c
    if c <= 4 then out:write(string.format("f%d %s off=%08x data=%08x mask=%08x pc=%08x\n", frame, kind, off, data, mask, st["pc"].value)) end
    return data
  end
end
local ranges = {{0x1f000000,0x1f017fff},{0x1f019000,0x1f1bff07},{0x1f1bff10,0x1fbfffff}}
KEEP = {}
for _,r in ipairs(ranges) do
  KEEP[#KEEP+1] = mem:install_write_tap(r[1], r[2], "w", hook("W"))
  KEEP[#KEEP+1] = mem:install_read_tap(r[1], r[2], "r", hook("R"))
end
local N = tonumber(os.getenv("NFRAMES") or "6000")
emu.register_frame_done(function()
  frame = frame + 1
  if frame == N then
    local keys = {}
    for k,_ in pairs(cnt) do keys[#keys+1]=k end
    table.sort(keys)
    for _,k in ipairs(keys) do out:write(string.format("COUNT %s = %d\n", k, cnt[k])) end
    out:close(); m:exit()
  end
end)
