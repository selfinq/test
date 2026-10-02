local m = manager.machine
local cpu = m.devices[":maincpu"]
local st = cpu.state
local mem = cpu.spaces["program"]
local out = io.open("probe1_log.txt", "w")
local cnt = {}
local function key(kind, a) return kind .. string.format(" %08x", a) end
local function hook(kind, lo, hi)
  local f = function(off, data, mask)
    local k = key(kind, off)
    local c = (cnt[k] or 0) + 1
    cnt[k] = c
    if c <= 3 then out:write(string.format("%s off=%08x data=%08x mask=%08x pc=%08x\n", kind, off, data, mask, st["pc"].value)) end
    return data
  end
  return f
end
mem:install_write_tap(0x1f000000, 0x1fffffff, "w", hook("W"))
mem:install_read_tap(0x1f000000, 0x1fffffff, "r", hook("R"))
mem:install_write_tap(0x1f801000, 0x1f802fff, "iow", hook("IOW"))
local n = 0
emu.register_frame_done(function()
  n = n + 1
  if n == 1500 then
    local keys = {}
    for k,_ in pairs(cnt) do keys[#keys+1]=k end
    table.sort(keys)
    for _,k in ipairs(keys) do out:write(string.format("COUNT %s = %d\n", k, cnt[k])) end
    out:close(); m:exit()
  end
end)
