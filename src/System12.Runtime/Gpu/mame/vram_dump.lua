-- MAME Lua probe: dump System 12 GPU VRAM + GPU/display state at chosen frames.
-- Validated against MAME 0.264 (Ubuntu apt) with tektagt. See docs/system12/GPU.md.
--
-- Usage (never commit the outputs, they are derived from game data):
--   S12_OUT=/some/dir S12_FRAMES=1500,2500,4000 \
--   /usr/games/mame tektagt -rompath input/roms -video none -sound none -nothrottle \
--        -seconds_to_run 120 -autoboot_script vram_dump.lua \
--        -nvram_directory ./nvram -cfg_directory ./cfg -snapshot_directory ./snap
-- (-video none needs -seconds_to_run, otherwise MAME warns; the script exits MAME after the last frame.)
--
-- Output per frame N:
--   vram_fN.bin  raw VRAM, 1024x1024 little-endian uint16 (2 MB), index = y*1024+x, bgr555+mask bit 15
--   gpu_fN.txt   key=value lines: screen size/refresh, GPU registers readable through MAME save items
--   (optional) snap_fN.png if S12_SNAP=1 (screen:snapshot; may not work with -video none)
--
-- How VRAM is reached: MAME 0.264 registers the GPU frame buffer as save item "0/p_vram" on device
-- ":gpu" (uint16 x 1048576 = 2 MB). device.items maps item name -> id; emu.item(id) gives read/read_block.
-- If a MAME build does not register it, the script says so and dumps only the register state.

local m = manager.machine
local outdir = os.getenv("S12_OUT") or "."
local frames = {}
for tok in string.gmatch(os.getenv("S12_FRAMES") or "1500", "%d+") do frames[#frames + 1] = tonumber(tok) end
table.sort(frames)
local want_snap = os.getenv("S12_SNAP") == "1"
local every = tonumber(os.getenv("S12_LOG_EVERY") or "0") -- >0: log screen/GPU regs every N frames

local gpu = m.devices[":gpu"]
local scr = m.screens[":screen"]
local vram_item
if gpu and gpu.items["0/p_vram"] then vram_item = emu.item(gpu.items["0/p_vram"]) end

local function reg(name)
  local id = gpu and gpu.items["0/" .. name]
  if not id then return nil end
  return emu.item(id):read(0)
end

local regs = { "n_gpustatus", "n_displaystarty", "m_n_displaystartx", "n_horiz_disstart", "n_horiz_disend",
  "n_vert_disstart", "n_vert_disend", "n_drawarea_x1", "n_drawarea_y1", "n_drawarea_x2", "n_drawarea_y2",
  "n_drawoffset_x", "n_drawoffset_y", "n_tx", "m_n_tx", "m_n_ty", "n_tp", "n_abr", "n_ix", "n_iy", "b_reverseflag",
  "n_lightgun_x", "n_lightgun_y" }

local function state_text(n)
  local t = {}
  t[#t + 1] = string.format("frame=%d", n)
  t[#t + 1] = string.format("screen_w=%d screen_h=%d refresh_hz=%.6f", scr.width, scr.height, scr.refresh)
  local va = scr.visible_area
  if va then t[#t + 1] = string.format("visible=%d,%d-%d,%d", va.min_x, va.min_y, va.max_x, va.max_y) end
  t[#t + 1] = string.format("vram_item=%s", vram_item and string.format("%d x %d bytes", vram_item.count, vram_item.size) or "MISSING")
  for _, r in ipairs(regs) do
    local v = reg(r)
    if v then t[#t + 1] = string.format("%s=0x%x", r, v) end
  end
  local st = reg("n_gpustatus")
  if st then
    t[#t + 1] = string.format("gpustatus: hres_bits=%d hres368=%d vres480=%d pal=%d rgb24=%d interlace=%d disp_off=%d dma_dir=%d",
      (st >> 17) & 3, (st >> 16) & 1, (st >> 19) & 1, (st >> 20) & 1, (st >> 21) & 1, (st >> 22) & 1, (st >> 23) & 1, (st >> 29) & 3)
  end
  return table.concat(t, "\n") .. "\n"
end

local idx, n = 1, 0
emu.register_frame_done(function()
  n = n + 1
  if every > 0 and n % every == 0 then
    print((state_text(n):gsub("\n", " | ")))
  end
  if frames[idx] and n == frames[idx] then
    local f = assert(io.open(string.format("%s/gpu_f%d.txt", outdir, n), "w"))
    f:write(state_text(n)); f:close()
    if vram_item then
      local bytes = vram_item.count * vram_item.size
      local g = assert(io.open(string.format("%s/vram_f%d.bin", outdir, n), "wb"))
      g:write(vram_item:read_block(0, bytes)); g:close()
    end
    if want_snap then
      pcall(function() scr:snapshot(string.format("%s/snap_f%d.png", outdir, n)) end)
    end
    print("dumped frame " .. n)
    idx = idx + 1
    if not frames[idx] then m:exit() end
  end
end)
