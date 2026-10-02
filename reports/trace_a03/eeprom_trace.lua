-- a03: main CPU AT28C16 accesses with a FRESH nvram (first-boot writes)
local m = manager.machine
local cpu = m.devices[":maincpu"]
local mem = cpu.spaces["program"]
local st = cpu.state
local out = io.open("eeprom_trace.txt","w")
local frame, nr, nw, handles = 0, 0, 0, {}
local wlog, rpc = {}, {}
local wwidth = {}
handles[#handles+1] = mem:install_write_tap(0x1f140000, 0x1f140fff, "eew", function(off, data, mask)
  nw = nw + 1
  local k = string.format("mask=%08x", mask); wwidth[k] = (wwidth[k] or 0) + 1
  if #wlog < 80 then wlog[#wlog+1] = string.format("f%d pc=%08x W %08x=%08x/%08x", frame, st["pc"].value, off, data, mask) end
  return data end)
handles[#handles+1] = mem:install_read_tap(0x1f140000, 0x1f140fff, "eer", function(off, data, mask)
  nr = nr + 1
  local p = string.format("%08x", st["pc"].value); rpc[p] = (rpc[p] or 0) + 1
  return data end)
emu.register_frame_done(function()
  frame = frame + 1
  for _, h in ipairs(handles) do h:reinstall() end
  if frame >= 800 then
    out:write(string.format("reads=%d writes=%d\n", nr, nw))
    for k, v in pairs(wwidth) do out:write(string.format("write %s x%d\n", k, v)) end
    for k, v in pairs(rpc) do out:write(string.format("read pc %s x%d\n", k, v)) end
    for _, s in ipairs(wlog) do out:write(s, "\n") end
    out:close(); m:exit()
  end
end)
