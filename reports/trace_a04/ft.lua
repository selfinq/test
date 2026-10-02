-- Forced differential test: executes ft_ops.lua through MAME's address space (real bus semantics)
-- then exits. Run with a fresh frame count of ~5 so the boot ROM has not started using the scratch RAM.
local m = manager.machine
local cpu = m.devices[":maincpu"]
local mem = cpu.spaces["program"]
local ops = dofile(os.getenv("FTOPS") or "ft_ops.lua")
local out = io.open(os.getenv("FTOUT") or "ft_result.txt", "w")
local PAT = 0xA5A5A5A5
local frame = 0
local function run()
  for i, op in ipairs(ops) do
    local k = op[1]
    if k == "w32" then mem:write_u32(op[2], op[3]); out:write(i, " ok\n")
    elseif k == "w16" then mem:write_u16(op[2], op[3]); out:write(i, " ok\n")
    elseif k == "w8" then mem:write_u8(op[2], op[3]); out:write(i, " ok\n")
    elseif k == "r32" then out:write(string.format("%d %08x\n", i, mem:read_u32(op[2])))
    elseif k == "r16" then out:write(string.format("%d %08x\n", i, mem:read_u16(op[2])))
    elseif k == "r8" then out:write(string.format("%d %08x\n", i, mem:read_u8(op[2])))
    elseif k == "dma" then
      local madr, bcr, chcr, dpcr, nread = op[2], op[3], op[4], op[5], op[6]
      local dest = (madr & 0x3fffff) & ~3
      local maxw = (0x400000 - dest) // 4
      if nread > maxw then nread = maxw end
      for j = 0, nread - 1 do mem:write_u32(dest + j * 4, (PAT + j * 0x01010101) & 0xffffffff) end
      mem:write_u32(0x1f8010f0, dpcr)
      mem:write_u32(0x1f8010d0, madr)
      mem:write_u32(0x1f8010d4, bcr)
      mem:write_u32(0x1f8010d8, chcr)
      local w = {}
      for j = 0, nread - 1 do w[#w + 1] = string.format("%x", mem:read_u32(dest + j * 4)) end
      out:write(string.format("%d chcr=%08x dicr=%08x data=%s\n", i, mem:read_u32(0x1f8010d8), mem:read_u32(0x1f8010f4), table.concat(w, ",")))
    end
  end
  out:close()
end
emu.register_frame_done(function()
  frame = frame + 1
  if frame == 5 then
    local ok, err = pcall(run)
    if not ok then out:write("LUA ERROR: " .. tostring(err) .. "\n"); out:close() end
    m:exit()
  end
end)
