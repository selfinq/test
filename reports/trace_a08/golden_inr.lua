-- Golden: value the H8 actually reads at 0x300000 / 0x300002 (the in_r handler output) while each MAME input field is held.
-- Also DSW (H8 port 7) via ioport. Output: golden_inr.txt  (FIELD <port> <name> <word0> <word1>)
KEEP = {}
local m = manager.machine
local sub = m.devices[":sub"]; local smem = sub.spaces["program"]
local out = io.open("/home/user/test/reports/trace_a08/golden_inr.txt", "w")
local w = {}
KEEP[#KEEP+1] = smem:install_read_tap(0x300000, 0x300003, "in", function(off, data, mask) w[off] = data return data end)
local ports = m.ioport.ports
local list = {}
for ptag, port in pairs(ports) do
  if ptag == ":IN0" or ptag == ":IN1" then
    for name, f in pairs(port.fields) do list[#list+1] = {ptag, name} end
  end
end
table.sort(list, function(a,b) if a[1] ~= b[1] then return a[1] < b[1] end return a[2] < b[2] end)
local frame, START = 0, 700
out:write(string.format("DSW %02x\n", ports[":DSW"]:read() & 0xff))
emu.register_frame_done(function()
  frame = frame + 1
  if frame < START then return end
  local k = frame - START
  local slot = k // 12 + 1
  local sf = k % 12
  if slot == 1 and sf == 0 then out:write(string.format("IDLE %04x %04x\n", w[0x300000] or -1, w[0x300002] or -1)) end
  local e = list[slot]
  if not e then out:close(); m:exit(); return end
  local fld = ports[e[1]].fields[e[2]]
  if sf == 2 then fld:set_value(1) end
  if sf == 8 then out:write(string.format("FIELD %s %s %04x %04x\n", e[1], e[2], w[0x300000] or -1, w[0x300002] or -1)) end
  if sf == 9 then fld:set_value(0) end
end)
