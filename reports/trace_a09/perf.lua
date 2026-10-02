local m = manager.machine
local cpu = m.devices[":maincpu"]
local mem = cpu.spaces["program"]
local V = os.getenv("V") or "0"
local WACT = 'printf "W %08x %08x\\n",wpaddr,wpdata;g'
local function wp(kind, base, len, cond, act) cpu.debug:wpset(mem, kind, base, len, cond or "", act) end
local stops=0
if V=="1" or V=="2" or V=="3" then
  wp("w", 0x1f000000, 4, nil, WACT)
  wp("w", 0x1f080000, 0x4000, nil, WACT)
  wp("w", 0x1f140000, 0x1000, nil, WACT)
  wp("w", 0x1f700000, 0x10000, nil, WACT)
  wp("w", 0x1fb00000, 4, nil, WACT)
  wp("w", 0x1fb80000, 4, nil, WACT)
end
if V=="2" or V=="3" then
  local cur = 0x1f801000
  for ch=0,6 do
    local c = 0x1f801088 + 0x10*ch
    wp("w", cur, c-cur, nil, WACT); cur=c+4
    wp("w", c, 4, "(wpdata & 0x01000000) == 0", WACT)
    if V=="2" then wp("w", c, 4, "(wpdata & 0x01000000) != 0", 'printf "W %08x %08x\\n",wpaddr,wpdata;g')
    else wp("w", c, 4, "(wpdata & 0x01000000) != 0", 'printf "W %08x %08x\\n",wpaddr,wpdata') end
  end
  wp("w", cur, 0x1f802000-cur, nil, WACT)
end
local t0=os.time()
local n=0
emu.register_periodic(function()
  if m.debugger.execution_state=="stop" then stops=stops+1 m.debugger.execution_state="run" end
end)
emu.register_frame_done(function()
  n=n+1
  if n==480 then local f=io.open("perf_"..V..".txt","w") f:write(string.format("V%s secs=%d stops=%d\n",V,os.time()-t0,stops)) f:close() m:exit() end
end)
