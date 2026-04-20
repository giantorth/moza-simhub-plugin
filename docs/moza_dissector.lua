-- Moza Racing Protocol — Wireshark Lua Dissector
--
-- Generic dissector that identifies all known message types from the
-- Moza serial protocol.  Hooks into the usbcom (CDC bulk data) layer
-- on Moza Racing bases (VID 346E / PID 0006, Interface 0, endpoints
-- 0x02 OUT / 0x82 IN).
--
-- Installation: copy to your Wireshark personal plugins folder, then
--   Wireshark > Analyze > Reload Lua Plugins  (or restart Wireshark)
--
-- Personal plugin folder locations:
--   Linux/Mac:  ~/.config/wireshark/plugins/
--   Windows:    %APPDATA%\Wireshark\plugins\
--
-- Frame format:
--   7E [N] [group] [device] [N bytes payload] [checksum]
--
-- Responses: group = request_group + 0x80, device nibbles swapped.
-- Checksum = (0x0D + sum of all preceding bytes including 0x7E) % 256.
--
-- See docs/moza-protocol.md for full protocol documentation.

local moza = Proto("moza", "Moza Racing Protocol")

-- ─── Group names ────────────────────────────────────────────────────────────
-- Request groups (bit 7 clear)

local GROUP_NAMES = {
    -- Heartbeat / keep-alive
    [0x00] = "Heartbeat",

    -- Identity probes (wheel connection sequence)
    [0x02] = "Identity: Protocol Version",
    [0x04] = "Identity: Hardware ID",
    [0x05] = "Identity: Capabilities",
    [0x06] = "Identity: Hardware Identifier",
    [0x07] = "Identity: Model Name",
    [0x08] = "Identity: HW/FW Version",
    [0x09] = "Identity: Presence Check",
    [0x0A] = "EEPROM Direct Access",
    [0x0E] = "Parameter Table / Debug",
    [0x0F] = "Identity: FW Version",
    [0x10] = "Identity: Serial Number",
    [0x11] = "Identity: Unknown",

    -- Main controller (0x12)
    [0x1E] = "Main: Output",
    [0x1F] = "Main: Settings",
    [0x20] = "Base Ambient LED Write",
    [0x22] = "Base Ambient LED Read",

    -- Base (0x13)
    [0x28] = "Base: Parameters",
    [0x29] = "Base: Timing/Rate",
    [0x2B] = "State Change",
    [0x2D] = "Sequence Counter",

    -- Wheel (0x17)
    [0x3F] = "Wheel LED",
    [0x40] = "Wheel/Dash Config",
    [0x41] = "Dash Telemetry Enable",
    [0x43] = "Telemetry / SerialStream",

    -- Response groups (request + 0x80)
    [0x80] = "Heartbeat Response",
    [0x82] = "Identity Response: Protocol Version",
    [0x84] = "Identity Response: Hardware ID",
    [0x85] = "Identity Response: Capabilities",
    [0x86] = "Identity Response: Hardware Identifier",
    [0x87] = "Identity Response: Model Name",
    [0x88] = "Identity Response: HW/FW Version",
    [0x89] = "Identity Response: Presence Check",
    [0x8A] = "EEPROM Response",
    [0x8E] = "Parameter / Debug Response",
    [0x8F] = "Identity Response: FW Version",
    [0x90] = "Identity Response: Serial Number",
    [0x91] = "Identity Response: Unknown",

    [0x9E] = "Main: Output Response",
    [0x9F] = "Main: Settings Response",
    [0xA0] = "Base Ambient LED Write Response",
    [0xA2] = "Base Ambient LED Read Response",

    [0xA8] = "Base: Parameters Response",
    [0xA9] = "Base: Timing/Rate Response",
    [0xAB] = "State Change Response",
    [0xAD] = "Seq Counter Response",

    [0xBF] = "Wheel LED Response",
    [0xC0] = "Wheel/Dash Config Response",
    [0xC1] = "Dash Telemetry Enable Response",
    [0xC3] = "Telemetry / SerialStream Response",
}

-- ─── Device names ────────────────────────────────────────────────────────────
-- Devices on the internal serial bus (request addresses)

local DEVICE_NAMES = {
    -- Request addresses
    [0x11] = "Base(0x11)",
    [0x12] = "Main/Hub(0x12)",
    [0x13] = "Base(0x13)",
    [0x14] = "Dash(0x14)",
    [0x15] = "Dev(0x15)",
    [0x17] = "Wheel(0x17)",
    [0x19] = "Pedals(0x19)",
    [0x1A] = "Shifter(0x1A)",
    [0x1B] = "Handbrake(0x1B)",
    [0x1C] = "E-Stop(0x1C)",
    [0x1D] = "Dev(0x1D)",
    [0x1E] = "Dev(0x1E)",

    -- Response addresses (nibbles swapped)
    [0x21] = "Main-resp(0x21)",
    [0x31] = "Base-resp(0x31)",
    [0x41] = "Dash-resp(0x41)",
    [0x51] = "Dev-resp(0x51)",
    [0x71] = "Wheel-resp(0x71)",
    [0x91] = "Pedals-resp(0x91)",
    [0xA1] = "Shifter-resp(0xA1)",
    [0xB1] = "Handbrake-resp(0xB1)",
    [0xC1] = "E-Stop-resp(0xC1)",
}

-- ─── Sub-command tables ─────────────────────────────────────────────────────

-- Group 0x43 / 0xC3 — Telemetry / SerialStream
local CMD_NAMES_43 = {
    [0x7D23] = "Live Telemetry",
    [0x7C00] = "SerialStream Data",
    [0x7C23] = "Dashboard Activation",
    [0x7C27] = "Display Config",
    [0xFC00] = "Session Ack",
}

-- Group 0x3F / 0xBF — Wheel LEDs
local CMD_NAMES_3F = {
    [0x1A00] = "RPM LED Position",
    [0x1A01] = "Button LED State",
}

-- Group 0x40 / 0xC0 — Wheel/Dash Config
local CMD_NAMES_40 = {
    [0x0900] = "Config Reset",
    [0x1B00] = "Brightness Page 0",
    [0x1B01] = "Brightness Page 1",
    [0x1C00] = "Page Config 0",
    [0x1C01] = "Page Config 1",
    [0x1D00] = "Page Config 0 (alt)",
    [0x1D01] = "Page Config 1 (alt)",
    [0x1E00] = "Channel Enable Page 0",
    [0x1E01] = "Channel Enable Page 1",
    [0x1F00] = "LED Color Page 0",
    [0x1F01] = "LED Color Page 1",
    [0x2800] = "Get Dashboard Mode",
    [0x2801] = "Get Active Page",
    [0x2802] = "Set Multi-Channel Mode",
}

-- Group 0x41 / 0xC1 — Telemetry Enable
local CMD_NAMES_41 = {
    [0xFDDE] = "Telemetry Enable Signal",
}

-- Group 0x2D / 0xAD — Sequence Counter
local CMD_NAMES_2D = {
    [0xF531] = "Sequence Counter",
}

-- Group 0x0E / 0x8E — Parameter Table / Debug
local CMD_NAMES_0E = {
    [0x0000] = "Parameter Read",
    [0x0001] = "Parameter Read (idx 1)",
    -- 0x05XX = Debug log text (handled dynamically)
}

-- Group 0x0A — EEPROM Direct Access
local CMD_NAMES_0A = {
    [0x0005] = "EEPROM Select Table",
    [0x0006] = "EEPROM Read Table",
    [0x0007] = "EEPROM Select Address",
    [0x0008] = "EEPROM Read Address",
    [0x0009] = "EEPROM Write Int",
    [0x000A] = "EEPROM Read Int",
    [0x000B] = "EEPROM Write Float",
    [0x000C] = "EEPROM Read Float",
}

-- Group 0x28 — Base Parameters
local CMD_NAMES_28 = {
    [0x0100] = "Base Param 0x01",
    [0x0200] = "Base Param 0x02",
    [0x1700] = "Wheel Param 0x17",
}

-- Group 0x1F / 0x9F — Main Settings
local CMD_NAMES_1F = {
    [0x0800] = "Get LED Status",
    [0x0900] = "Set LED Status",
    [0x1300] = "Set Compat Mode",
    [0x1700] = "Get Compat Mode",
    [0x3300] = "Set Work Mode",
    [0x3400] = "Get Work Mode",
    [0x3500] = "Set Default FFB Status",
    [0x3600] = "Get Default FFB Status",
    [0x4600] = "Get BLE Mode",
    [0x4700] = "Set BLE Mode",
    [0x4C00] = "Set Interpolation",
    [0x4D00] = "Get Interpolation",
}

-- Map group byte to its sub-command table (works for both request and response)
local function cmd_table_for_group(g)
    local base_g = bit.band(g, 0x7F)  -- strip response bit
    if base_g == 0x43 then return CMD_NAMES_43 end
    if base_g == 0x3F then return CMD_NAMES_3F end
    if base_g == 0x40 then return CMD_NAMES_40 end
    if base_g == 0x41 then return CMD_NAMES_41 end
    if base_g == 0x2D then return CMD_NAMES_2D end
    if base_g == 0x0E then return CMD_NAMES_0E end
    if base_g == 0x0A then return CMD_NAMES_0A end
    if base_g == 0x28 then return CMD_NAMES_28 end
    if base_g == 0x1F then return CMD_NAMES_1F end
    return nil
end

-- ─── Proto fields ────────────────────────────────────────────────────────────

local pf = {
    -- Frame skeleton
    start       = ProtoField.uint8 ("moza.start",       "Start (0x7E)",       base.HEX),
    n           = ProtoField.uint8 ("moza.n",            "Payload Length (N)", base.DEC),
    group       = ProtoField.uint8 ("moza.group",        "Group",              base.HEX, GROUP_NAMES),
    device      = ProtoField.uint8 ("moza.device",       "Device",             base.HEX, DEVICE_NAMES),
    cmd         = ProtoField.bytes ("moza.cmd",          "Cmd ID"),
    data        = ProtoField.bytes ("moza.data",         "Data"),
    checksum    = ProtoField.uint8 ("moza.checksum",     "Checksum",           base.HEX),
    chk_status  = ProtoField.string("moza.checksum_status", "Checksum Status"),

    -- Direction indicator
    is_response = ProtoField.bool  ("moza.is_response",  "Is Response"),

    -- Telemetry header (0x43 / 7D23)
    t_const4    = ProtoField.bytes ("moza.telem.const4",   "Const (32 00 23 32)"),
    t_flag      = ProtoField.uint8 ("moza.telem.flag",     "Flag Byte (tier ID)", base.HEX),
    t_const20   = ProtoField.uint8 ("moza.telem.const20",  "Const (0x20)",        base.HEX),
    t_live      = ProtoField.bytes ("moza.telem.live",     "Bit-packed Live Data"),

    -- RPM LED (0x3F / 1A00) — 4 x uint16 LE
    rpm_pos     = ProtoField.uint16("moza.rpm.position",   "RPM Position (0-1023)", base.DEC),
    rpm_zero1   = ProtoField.uint16("moza.rpm.zero1",      "Padding (0)",           base.DEC),
    rpm_max     = ProtoField.uint16("moza.rpm.max",        "RPM Max (1023)",        base.DEC),
    rpm_zero2   = ProtoField.uint16("moza.rpm.zero2",      "Padding (0)",           base.DEC),

    -- SerialStream chunk fields (0x43 / 7C00)
    ss_session  = ProtoField.uint8 ("moza.ss.session",     "Session ID",            base.HEX),
    ss_type     = ProtoField.uint8 ("moza.ss.type",        "Chunk Type",            base.HEX),
    ss_seq      = ProtoField.uint16("moza.ss.seq",         "Sequence Number",       base.DEC),
    ss_payload  = ProtoField.bytes ("moza.ss.payload",     "Chunk Payload"),

    -- Session ack fields (0x43 / FC00)
    ack_session = ProtoField.uint8 ("moza.ack.session",    "Ack Session ID",        base.HEX),
    ack_seq     = ProtoField.uint16("moza.ack.seq",        "Ack Sequence",          base.DEC),

    -- Sequence counter (0x2D / F531)
    seq_counter = ProtoField.uint8 ("moza.seq.counter",    "Counter Value",         base.DEC),

    -- Telemetry enable (0x41 / FDDE)
    enable_data = ProtoField.bytes ("moza.enable.data",    "Enable Data"),

    -- Identity string fields
    id_string   = ProtoField.string("moza.identity.string", "Identity String"),
    id_subcmd   = ProtoField.uint8 ("moza.identity.subcmd", "Sub-command",           base.HEX),
    id_bytes    = ProtoField.bytes ("moza.identity.bytes",  "Identity Bytes"),

    -- 0x0E debug/parameter fields
    dbg_text    = ProtoField.string("moza.debug.text",     "Debug Log Text"),
    param_table = ProtoField.uint8 ("moza.param.table",    "EEPROM Table",          base.HEX),
    param_index = ProtoField.uint8 ("moza.param.index",    "Parameter Index",       base.HEX),

    -- 0x40 config fields
    cfg_subcmd  = ProtoField.bytes ("moza.cfg.subcmd",     "Config Sub-command"),
    cfg_data    = ProtoField.bytes ("moza.cfg.data",       "Config Data"),

    -- Display config (7C27)
    dcfg_data   = ProtoField.bytes ("moza.dcfg.data",      "Display Config Data"),
}

moza.fields = {}
for _, v in pairs(pf) do moza.fields[#moza.fields + 1] = v end

-- ─── Helpers ────────────────────────────────────────────────────────────────

local function group_label(g)
    return GROUP_NAMES[g] or string.format("Group 0x%02X", g)
end

local function device_label(d)
    return DEVICE_NAMES[d] or string.format("Dev 0x%02X", d)
end

local function is_response_group(g)
    return bit.band(g, 0x80) ~= 0
end

local function base_group(g)
    return bit.band(g, 0x7F)
end

-- Compute the Moza checksum over a range of bytes in a tvb
-- checksum = (0x0D + sum of all preceding bytes including 0x7E) % 256
local function compute_checksum(tvb, offset, count)
    local sum = 0x0D
    for i = 0, count - 1 do
        sum = sum + tvb(offset + i, 1):uint()
    end
    return bit.band(sum, 0xFF)
end

-- Attempt to read a null-terminated ASCII string from a tvb range
local function read_ascii(tvb, offset, maxlen)
    local s = {}
    for i = 0, maxlen - 1 do
        local b = tvb(offset + i, 1):uint()
        if b == 0 then break end
        if b >= 0x20 and b <= 0x7E then
            s[#s + 1] = string.char(b)
        else
            s[#s + 1] = "."
        end
    end
    return table.concat(s)
end

-- Check if a byte range looks like printable ASCII
local function is_ascii(tvb, offset, len)
    local printable = 0
    for i = 0, len - 1 do
        local b = tvb(offset + i, 1):uint()
        if b >= 0x20 and b <= 0x7E then
            printable = printable + 1
        elseif b == 0x00 then
            -- null padding is fine
        else
            return false
        end
    end
    return printable > 0
end

-- ─── SerialStream chunk type names ──────────────────────────────────────────

local SS_TYPE_NAMES = {
    [0x00] = "Control/End",
    [0x01] = "Data",
    [0x81] = "Session Open",
}

-- ─── Per-command decoders ───────────────────────────────────────────────────

-- 0x43 / 7D23 — Live Telemetry
local function decode_7d23(tvb, subtree, payload_off, n)
    -- Structure: cmd(2) + const4(4) + flag(1) + 0x20(1) + live_data(N-8)
    if n < 8 then return end

    subtree:add(pf.t_const4,  tvb(payload_off + 2, 4))
    local flag = tvb(payload_off + 6, 1):uint()
    subtree:add(pf.t_flag,    tvb(payload_off + 6, 1)):append_text(
        string.format("  [tier stream identifier]"))
    subtree:add(pf.t_const20, tvb(payload_off + 7, 1))

    local live_len = n - 8
    if live_len <= 0 then
        subtree:add(tvb(payload_off + 6, 2),
            string.format("Stub frame (flag=0x%02X, no data — tier inactive)", flag))
        return
    end

    local live_off = payload_off + 8
    local live_tree = subtree:add(pf.t_live, tvb(live_off, live_len))
    live_tree:set_text(string.format(
        "Bit-packed Live Data: %d bytes (%d bits), flag=0x%02X",
        live_len, live_len * 8, flag))
end

-- 0x3F / 1A00 — RPM LED Position
local function decode_rpm_led(tvb, subtree, payload_off, n)
    if n < 10 then return end  -- cmd(2) + 8 data bytes
    local pos = tvb(payload_off + 2, 2):le_uint()
    local pct = pos / 1023 * 100
    subtree:add(pf.rpm_pos,   tvb(payload_off + 2, 2)):append_text(
        string.format("  (%.1f%% of redline)", pct))
    subtree:add(pf.rpm_zero1, tvb(payload_off + 4, 2))
    subtree:add(pf.rpm_max,   tvb(payload_off + 6, 2))
    subtree:add(pf.rpm_zero2, tvb(payload_off + 8, 2))
end

-- 0x43 / 7C00 — SerialStream Data (chunk-based transport)
local function decode_7c00(tvb, subtree, payload_off, n)
    if n < 6 then return end  -- cmd(2) + session(1) + type(1) + seq(2) minimum
    local session = tvb(payload_off + 2, 1):uint()
    local chunk_type = tvb(payload_off + 3, 1):uint()
    local seq = tvb(payload_off + 4, 2):le_uint()

    local type_name = SS_TYPE_NAMES[chunk_type] or string.format("0x%02X", chunk_type)

    subtree:add(pf.ss_session, tvb(payload_off + 2, 1)):append_text(
        string.format("  [session %d]", session))
    subtree:add(pf.ss_type,    tvb(payload_off + 3, 1)):append_text(
        string.format("  [%s]", type_name))
    subtree:add(pf.ss_seq,     tvb(payload_off + 4, 2))

    if chunk_type == 0x81 then
        -- Session open: payload has session_id(2 LE) + window(2 LE)
        if n >= 10 then
            local port = tvb(payload_off + 6, 2):le_uint()
            local window = tvb(payload_off + 10, 2):le_uint()
            subtree:add(tvb(payload_off + 6, 2),
                string.format("Port: %d (0x%04X)", port, port))
            subtree:add(tvb(payload_off + 10, 2),
                string.format("Receive Window: %d", window))
        end
    end

    local chunk_data_len = n - 6
    if chunk_data_len > 0 then
        subtree:add(pf.ss_payload, tvb(payload_off + 6, chunk_data_len))
    end
end

-- 0x43 / FC00 — Session Acknowledgment
local function decode_fc00(tvb, subtree, payload_off, n)
    if n < 5 then return end  -- cmd(2) + session(1) + ack_seq(2)
    local session = tvb(payload_off + 2, 1):uint()
    local ack_seq = tvb(payload_off + 3, 2):le_uint()

    subtree:add(pf.ack_session, tvb(payload_off + 2, 1)):append_text(
        string.format("  [session %d]", session))
    subtree:add(pf.ack_seq,     tvb(payload_off + 3, 2))
end

-- 0x2D / F531 — Sequence Counter
local function decode_seq_counter(tvb, subtree, payload_off, n)
    if n < 6 then return end  -- cmd(2) + 4 data bytes (00 00 00 XX)
    local counter = tvb(payload_off + 5, 1):uint()
    subtree:add(pf.seq_counter, tvb(payload_off + 5, 1)):append_text(
        string.format("  [frame sequence %d]", counter))
    if n > 2 then
        subtree:add(pf.data, tvb(payload_off + 2, n - 2))
    end
end

-- 0x41 / FDDE — Telemetry Enable
local function decode_enable(tvb, subtree, payload_off, n)
    if n < 6 then return end  -- cmd(2) + 4 data bytes
    subtree:add(pf.enable_data, tvb(payload_off + 2, n - 2)):append_text(
        "  [expected: 00 00 00 00 = telemetry active]")
end

-- Identity response groups (0x82, 0x84, 0x85, 0x87, 0x88, 0x8F, 0x90, etc.)
local function decode_identity_response(tvb, subtree, payload_off, n, group)
    if n < 1 then return end

    local bg = base_group(group)

    -- Groups with a sub-command byte before string data
    if bg == 0x07 or bg == 0x08 or bg == 0x0F or bg == 0x10 then
        if n >= 2 then
            subtree:add(pf.id_subcmd, tvb(payload_off, 1))
            local str_off = payload_off + 1
            local str_len = n - 1
            if str_len > 0 and is_ascii(tvb, str_off, str_len) then
                local s = read_ascii(tvb, str_off, str_len)
                subtree:add(pf.id_string, tvb(str_off, str_len), s):append_text(
                    string.format("  [\"%s\"]", s))
            else
                subtree:add(pf.id_bytes, tvb(str_off, str_len))
            end
        end
    elseif bg == 0x04 or bg == 0x05 then
        -- 4-byte capability/hardware ID data
        subtree:add(pf.id_bytes, tvb(payload_off, n))
    elseif bg == 0x06 then
        -- 12-byte hardware identifier
        subtree:add(pf.id_bytes, tvb(payload_off, n))
    elseif bg == 0x02 then
        -- Protocol version (1 byte)
        subtree:add(pf.id_bytes, tvb(payload_off, n))
    elseif bg == 0x09 then
        -- Presence check response (2 bytes, e.g. 00 01)
        subtree:add(pf.id_bytes, tvb(payload_off, n)):append_text(
            "  [sub-device count?]")
    else
        -- Generic identity data
        if n > 0 and is_ascii(tvb, payload_off, n) then
            local s = read_ascii(tvb, payload_off, n)
            subtree:add(pf.id_string, tvb(payload_off, n), s)
        else
            subtree:add(pf.id_bytes, tvb(payload_off, n))
        end
    end
end

-- Identity request groups (0x02, 0x04, 0x05, 0x07, 0x08, 0x0F, 0x10, etc.)
local function decode_identity_request(tvb, subtree, payload_off, n, group)
    if n < 1 then return end
    if group == 0x07 or group == 0x08 or group == 0x0F or group == 0x10 then
        -- Sub-command byte (e.g. 0x01 for model name, 0x02 for HW rev)
        subtree:add(pf.id_subcmd, tvb(payload_off, 1))
        if n > 1 then
            subtree:add(pf.data, tvb(payload_off + 1, n - 1))
        end
    elseif group == 0x04 or group == 0x05 then
        -- 0x00 + 3 zero bytes
        subtree:add(pf.data, tvb(payload_off, n))
    else
        subtree:add(pf.data, tvb(payload_off, n))
    end
end

-- 0x0E / 0x8E — Parameter Table / Debug
local function decode_param_debug(tvb, subtree, payload_off, n, group)
    if n < 2 then return end
    local cmd_hi = tvb(payload_off, 1):uint()

    if is_response_group(group) then
        if cmd_hi == 0x05 then
            -- Debug log text (0x05:XX + ASCII)
            if n > 2 and is_ascii(tvb, payload_off + 2, n - 2) then
                local text = read_ascii(tvb, payload_off + 2, n - 2)
                subtree:add(pf.dbg_text, tvb(payload_off + 2, n - 2), text):append_text(
                    string.format("  [\"%s\"]", text))
            else
                subtree:add(pf.data, tvb(payload_off + 2, n - 2))
            end
        elseif cmd_hi == 0x00 then
            -- Parameter value response (cmd=00:00, n=7)
            subtree:add(pf.data, tvb(payload_off + 2, n - 2)):append_text(
                "  [parameter value]")
        else
            subtree:add(pf.data, tvb(payload_off + 2, n - 2))
        end
    else
        -- Request: 00 [table] [index]
        if n >= 3 then
            subtree:add(pf.param_table, tvb(payload_off + 1, 1))
            subtree:add(pf.param_index, tvb(payload_off + 2, 1))
        end
        if n > 3 then
            subtree:add(pf.data, tvb(payload_off + 3, n - 3))
        end
    end
end

-- 0x40 / 0xC0 — Wheel/Dash Config
local function decode_config_40(tvb, subtree, payload_off, n)
    if n < 2 then return end
    -- Show the 2-byte sub-command and any data after it
    subtree:add(pf.cfg_subcmd, tvb(payload_off, 2))
    if n > 2 then
        subtree:add(pf.cfg_data, tvb(payload_off + 2, n - 2))
    end
end

-- 0x43 / 7C27 — Periodic Display Config
local function decode_7c27(tvb, subtree, payload_off, n)
    if n < 2 then return end
    subtree:add(pf.dcfg_data, tvb(payload_off + 2, n - 2))
end

-- 0x43 / 7C23 — Dashboard Activation
local function decode_7c23(tvb, subtree, payload_off, n)
    if n > 2 then
        subtree:add(pf.data, tvb(payload_off + 2, n - 2)):append_text(
            "  [dashboard activation parameters]")
    end
end

-- 0x1F — Main Settings
local function decode_main_settings(tvb, subtree, payload_off, n, group)
    if n < 1 then return end
    -- The cmd byte is the sub-command ID; rest is data
    subtree:add(pf.cfg_subcmd, tvb(payload_off, math.min(n, 2)))
    if n > 2 then
        subtree:add(pf.cfg_data, tvb(payload_off + 2, n - 2))
    elseif n > 1 then
        subtree:add(pf.cfg_data, tvb(payload_off + 1, n - 1))
    end
end

-- 0x2B — State Change
local function decode_state_change(tvb, subtree, payload_off, n)
    if n > 0 then
        subtree:add(pf.data, tvb(payload_off, n)):append_text(
            "  [state change payload]")
    end
end

-- 0x28 — Base Parameters
local function decode_base_params(tvb, subtree, payload_off, n)
    if n < 1 then return end
    subtree:add(pf.cfg_subcmd, tvb(payload_off, math.min(n, 2)))
    if n > 2 then
        subtree:add(pf.cfg_data, tvb(payload_off + 2, n - 2)):append_text(
            "  [parameter value]")
    elseif n > 1 then
        subtree:add(pf.cfg_data, tvb(payload_off + 1, n - 1))
    end
end

-- 0x1F — Unknown group (0x1F to device 0x12)
local function decode_1f_main(tvb, subtree, payload_off, n)
    if n > 0 then
        subtree:add(pf.data, tvb(payload_off, n)):append_text(
            string.format("  [settings command, %d bytes]", n))
    end
end

-- ─── Core frame parser ──────────────────────────────────────────────────────

local function parse_frames(tvb, pinfo, tree)
    local len         = tvb:len()
    local offset      = 0
    local frame_count = 0
    local info_items  = {}

    while offset < len do
        -- Scan for start byte
        if tvb(offset, 1):uint() ~= 0x7E then
            offset = offset + 1
        else
            if offset + 4 >= len then break end
            local n         = tvb(offset + 1, 1):uint()
            local frame_len = n + 5   -- 7E + N + group + device + N*payload + checksum
            if offset + frame_len > len then break end

            local group  = tvb(offset + 2, 1):uint()
            local device = tvb(offset + 3, 1):uint()
            local is_resp = is_response_group(group)
            local bg = base_group(group)

            -- Extract 16-bit command ID when payload >= 2
            local cmd16 = -1
            if n >= 2 then
                cmd16 = bit.bor(bit.lshift(tvb(offset + 4, 1):uint(), 8),
                                tvb(offset + 5, 1):uint())
            end

            -- Look up command name from the appropriate table
            local cmd_tbl = cmd_table_for_group(group)
            local grp_s = group_label(group)
            local dev_s = device_label(device)
            local cmd_s = ""
            local cmd_name = nil

            if cmd16 >= 0 and cmd_tbl then
                cmd_name = cmd_tbl[cmd16]
            end

            -- Special handling for 0x0E debug log detection
            if cmd_name == nil and bg == 0x0E and cmd16 >= 0 then
                local cmd_hi = bit.rshift(cmd16, 8)
                if cmd_hi == 0x05 then
                    cmd_name = "Debug Log"
                elseif cmd_hi == 0x00 then
                    cmd_name = "Parameter Read"
                end
            end

            if cmd_name then
                cmd_s = string.format(" [%s]", cmd_name)
            elseif cmd16 >= 0 then
                cmd_s = string.format(" cmd:%04X", cmd16)
            end

            -- Direction label
            local dir_s = is_resp and "RSP" or "REQ"

            -- Heartbeat with n=0 is common
            local heartbeat = (bg == 0x00 and n == 0)

            -- Frame label
            local frame_label
            if heartbeat then
                frame_label = string.format(
                    "Moza #%d [%s]: Heartbeat → %s",
                    frame_count + 1, dir_s, dev_s)
            elseif n == 1 and bg == 0x43 then
                -- Bare 0x43 keepalive (n=1, payload=0x00 or 0x80)
                local kbyte = tvb(offset + 4, 1):uint()
                if kbyte == 0x00 then
                    frame_label = string.format(
                        "Moza #%d [%s]: Dash Keepalive → %s",
                        frame_count + 1, dir_s, dev_s)
                elseif kbyte == 0x80 then
                    frame_label = string.format(
                        "Moza #%d [%s]: Dash Keepalive Ack → %s",
                        frame_count + 1, dir_s, dev_s)
                else
                    frame_label = string.format(
                        "Moza #%d [%s]: %s → %s data:%02X  (N=%d)",
                        frame_count + 1, dir_s, grp_s, dev_s, kbyte, n)
                end
            else
                frame_label = string.format(
                    "Moza #%d [%s]: %s → %s%s  (N=%d)",
                    frame_count + 1, dir_s, grp_s, dev_s, cmd_s, n)
            end

            local ftree = tree:add(moza, tvb(offset, frame_len), frame_label)
            ftree:add(pf.start,   tvb(offset,     1))
            ftree:add(pf.n,       tvb(offset + 1, 1))
            ftree:add(pf.group,   tvb(offset + 2, 1)):append_text(
                is_resp and "  [RESPONSE]" or "  [REQUEST]")
            ftree:add(pf.device,  tvb(offset + 3, 1))

            local payload_off = offset + 4  -- absolute start of N-byte payload

            -- Checksum verification
            local expected_chk = compute_checksum(tvb, offset, 4 + n)
            local actual_chk   = tvb(offset + 4 + n, 1):uint()
            local chk_ok       = (expected_chk == actual_chk)
            local chk_item     = ftree:add(pf.checksum, tvb(offset + 4 + n, 1))
            if chk_ok then
                chk_item:append_text("  [OK]")
            else
                chk_item:append_text(
                    string.format("  [BAD: expected 0x%02X]", expected_chk))
                ftree:add(pf.chk_status, tvb(offset + 4 + n, 1), "CHECKSUM MISMATCH")
            end

            -- Decode payload based on group + command
            if heartbeat then
                -- No payload for heartbeat frames
            elseif n >= 2 then
                ftree:add(pf.cmd, tvb(payload_off, 2))

                -- Dispatch to specialized decoders
                if bg == 0x43 then
                    if cmd16 == 0x7D23 then
                        decode_7d23(tvb, ftree, payload_off, n)
                    elseif cmd16 == 0x7C00 then
                        decode_7c00(tvb, ftree, payload_off, n)
                    elseif cmd16 == 0xFC00 then
                        decode_fc00(tvb, ftree, payload_off, n)
                    elseif cmd16 == 0x7C27 then
                        decode_7c27(tvb, ftree, payload_off, n)
                    elseif cmd16 == 0x7C23 then
                        decode_7c23(tvb, ftree, payload_off, n)
                    elseif n > 2 then
                        ftree:add(pf.data, tvb(payload_off + 2, n - 2))
                    end
                elseif bg == 0x3F and cmd16 == 0x1A00 then
                    decode_rpm_led(tvb, ftree, payload_off, n)
                elseif bg == 0x3F and cmd16 == 0x1A01 then
                    -- Button LED state
                    if n > 2 then
                        ftree:add(pf.data, tvb(payload_off + 2, n - 2)):append_text(
                            "  [button LED data]")
                    end
                elseif bg == 0x2D and cmd16 == 0xF531 then
                    decode_seq_counter(tvb, ftree, payload_off, n)
                elseif bg == 0x41 and cmd16 == 0xFDDE then
                    decode_enable(tvb, ftree, payload_off, n)
                elseif bg == 0x0E then
                    decode_param_debug(tvb, ftree, payload_off, n, group)
                elseif bg == 0x40 then
                    decode_config_40(tvb, ftree, payload_off, n)
                elseif bg == 0x1F then
                    decode_main_settings(tvb, ftree, payload_off, n, group)
                elseif bg == 0x28 then
                    decode_base_params(tvb, ftree, payload_off, n)
                elseif bg == 0x2B then
                    decode_state_change(tvb, ftree, payload_off, n)
                elseif bg == 0x0A then
                    -- EEPROM direct access
                    if n > 2 then
                        ftree:add(pf.data, tvb(payload_off + 2, n - 2))
                    end
                -- Identity groups (request or response)
                elseif (bg >= 0x02 and bg <= 0x11) then
                    if is_resp then
                        decode_identity_response(tvb, ftree, payload_off, n, group)
                    else
                        decode_identity_request(tvb, ftree, payload_off, n, group)
                    end
                else
                    -- Unknown group: show raw data
                    if n > 2 then
                        ftree:add(pf.data, tvb(payload_off + 2, n - 2))
                    end
                end
            elseif n == 1 then
                -- Single-byte payload (e.g. bare 0x43 keepalive)
                ftree:add(pf.data, tvb(payload_off, 1))
            end

            -- Accumulate info column
            local info_entry
            if heartbeat then
                info_entry = string.format("HB→%s", dev_s)
            elseif n == 1 and bg == 0x43 then
                info_entry = string.format("KA→%s", dev_s)
            else
                info_entry = string.format("0x%02X→%s%s", group, dev_s, cmd_s)
            end
            info_items[#info_items + 1] = info_entry

            frame_count = frame_count + 1
            offset = offset + frame_len
        end
    end

    if frame_count > 0 then
        pinfo.cols.protocol:set("MOZA")
        pinfo.cols.info:set(table.concat(info_items, " | "))
    end

    return frame_count
end

-- ─── Hook: post-dissector on usbcom layer ───────────────────────────────────
--
-- usbcom.data.out_payload / in_payload are FT_BYTES fields; Lua returns a
-- ByteArray from FieldInfo.value, which we promote to a Tvb for parsing.

local fi_out = Field.new("usbcom.data.out_payload")
local fi_in  = Field.new("usbcom.data.in_payload")

function moza.dissector(tvb, pinfo, tree)
    local fi = fi_out() or fi_in()
    if fi == nil then return end

    local ba = fi.value          -- ByteArray
    if ba == nil or ba:len() == 0 then return end
    if ba:get_index(0) ~= 0x7E then return end   -- quick rejection

    local payload_tvb = ba:tvb("Moza Protocol")
    parse_frames(payload_tvb, pinfo, tree)
end

register_postdissector(moza)
