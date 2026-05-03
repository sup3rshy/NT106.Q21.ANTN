-- NetDraw protocol dissector for Wireshark.
--
-- Wire format: two encodings on one TCP stream, distinguished by the first
-- byte at every frame boundary.
--   * 0x7B ('{')  -> newline-delimited JSON envelope.
--   * 0xFE        -> length-prefixed binary frame (6-byte header + 48-byte
--                    envelope + per-type body); see docs/design/binary-frame.md.
-- Phase 1 of the binary path ships only the framer + Error stub, so binary
-- frames are dissected at the envelope level only; per-type body decoders
-- land in the Phase 5 dissector update.

local netdraw_proto = Proto("netdraw", "NetDraw collaborative drawing protocol")

local f_version  = ProtoField.uint32("netdraw.version",         "Protocol version", base.DEC)
local f_type     = ProtoField.string("netdraw.type",            "Message type")
local f_type_id  = ProtoField.int32 ("netdraw.type_id",         "Message type id", base.DEC)
local f_sender   = ProtoField.string("netdraw.sender",          "Sender name")
local f_senderid = ProtoField.string("netdraw.sender_id",       "Sender id")
local f_room     = ProtoField.string("netdraw.room",            "Room id")
local f_ts       = ProtoField.uint64("netdraw.timestamp",       "Timestamp (ms)", base.DEC)
local f_summary  = ProtoField.string("netdraw.payload_summary", "Payload summary")
local f_raw      = ProtoField.string("netdraw.raw",             "Raw JSON")

local f_bin_magic   = ProtoField.uint8 ("netdraw.bin.magic",   "Magic", base.HEX)
local f_bin_version = ProtoField.uint8 ("netdraw.bin.version", "Binary version", base.DEC)
local f_bin_type_id = ProtoField.uint8 ("netdraw.bin.type_id", "Binary type id", base.DEC)
local f_bin_type    = ProtoField.string("netdraw.bin.type",    "Binary message type")
local f_bin_length  = ProtoField.uint32("netdraw.bin.length",  "Payload length (bytes)", base.DEC)
local f_bin_ts      = ProtoField.uint64("netdraw.bin.ts",      "Timestamp (ms)", base.DEC)
local f_bin_sender  = ProtoField.uint32("netdraw.bin.sender",  "Sender uint32", base.HEX)
local f_bin_room    = ProtoField.uint32("netdraw.bin.room",    "Room hash", base.HEX)
local f_bin_token8  = ProtoField.bytes ("netdraw.bin.token8",  "Session token (first 8 B)")
local f_bin_summary = ProtoField.string("netdraw.bin.summary", "Body summary")

netdraw_proto.fields = {
    f_version, f_type, f_type_id, f_sender, f_senderid,
    f_room, f_ts, f_summary, f_raw,
    f_bin_magic, f_bin_version, f_bin_type_id, f_bin_type,
    f_bin_length, f_bin_ts, f_bin_sender, f_bin_room, f_bin_token8, f_bin_summary,
}

-- Mirrors the declaration order of MessageType in
-- NetDraw.Shared/Protocol/MessageType.cs. Newtonsoft.Json serializes enums
-- as integers by default (no StringEnumConverter is registered on the
-- write path), so the wire field "type" is normally a number.
local MESSAGE_TYPE_NAMES = {
    [0]  = "JoinRoom",
    [1]  = "LeaveRoom",
    [2]  = "RoomJoined",
    [3]  = "UserJoined",
    [4]  = "UserLeft",
    [5]  = "RoomList",
    [6]  = "Draw",
    [7]  = "DrawPreview",
    [8]  = "ClearCanvas",
    [9]  = "Undo",
    [10] = "Redo",
    [11] = "MoveObject",
    [12] = "DeleteObject",
    [13] = "CanvasSnapshot",
    [14] = "CursorMove",
    [15] = "ChatMessage",
    [16] = "AiCommand",
    [17] = "AiResult",
    [18] = "Error",
}

-- Mirrors ShapeType in NetDraw.Shared/Models/Actions/ShapeAction.cs.
local SHAPE_TYPE_NAMES = {
    [0] = "Rect",
    [1] = "Ellipse",
    [2] = "Circle",
    [3] = "Triangle",
    [4] = "Star",
}

----------------------------------------------------------------------
-- Minimal JSON parser.
--
-- The protocol JSON is small (a single envelope per line, payloads at
-- most a few KB). Wireshark's bundled Lua does not ship dkjson on every
-- platform/installer, so a hand-roll is the portable choice.
--
-- Limitations to know:
--   * `null` values are discarded (Lua tables cannot store nil; using a
--     sentinel would complicate every summarizer for a case the wire
--     format avoids via NullValueHandling.Ignore on the producer).
--   * `\uXXXX` only decodes BMP codepoints; surrogate pairs are not
--     reassembled. The producer emits raw UTF-8 for non-ASCII rather
--     than escapes, so this only bites if a future client serializes
--     emoji as `😀`.
----------------------------------------------------------------------

local Json = {}

local function skip_ws(s, i)
    local n = #s
    while i <= n do
        local c = s:byte(i)
        if c ~= 32 and c ~= 9 and c ~= 10 and c ~= 13 then
            return i
        end
        i = i + 1
    end
    return i
end

local parse_value

local function parse_string(s, i)
    -- Caller guarantees s:sub(i,i) == '"'.
    local n = #s
    local j = i + 1
    local buf = {}
    while j <= n do
        local c = s:sub(j, j)
        if c == '"' then
            return table.concat(buf), j + 1
        elseif c == "\\" then
            j = j + 1
            local esc = s:sub(j, j)
            if     esc == '"' then buf[#buf+1] = '"'
            elseif esc == "\\" then buf[#buf+1] = "\\"
            elseif esc == "/"  then buf[#buf+1] = "/"
            elseif esc == "b"  then buf[#buf+1] = "\b"
            elseif esc == "f"  then buf[#buf+1] = "\f"
            elseif esc == "n"  then buf[#buf+1] = "\n"
            elseif esc == "r"  then buf[#buf+1] = "\r"
            elseif esc == "t"  then buf[#buf+1] = "\t"
            elseif esc == "u"  then
                local hex = s:sub(j + 1, j + 4)
                local code = tonumber(hex, 16)
                if code and code < 0x80 then
                    buf[#buf+1] = string.char(code)
                elseif code and code < 0x800 then
                    buf[#buf+1] = string.char(
                        0xC0 + math.floor(code / 0x40),
                        0x80 + (code % 0x40))
                elseif code then
                    buf[#buf+1] = string.char(
                        0xE0 + math.floor(code / 0x1000),
                        0x80 + (math.floor(code / 0x40) % 0x40),
                        0x80 + (code % 0x40))
                end
                j = j + 4
            else
                buf[#buf+1] = esc
            end
            j = j + 1
        else
            buf[#buf+1] = c
            j = j + 1
        end
    end
    return nil, j
end

local function parse_number(s, i)
    local n = #s
    local j = i
    while j <= n do
        local c = s:byte(j)
        if (c >= 48 and c <= 57)        -- 0-9
            or c == 45 or c == 43        -- - +
            or c == 46                   -- .
            or c == 101 or c == 69 then  -- e E
            j = j + 1
        else
            break
        end
    end
    return tonumber(s:sub(i, j - 1)), j
end

local function parse_object(s, i)
    local out = {}
    i = skip_ws(s, i + 1)
    if s:sub(i, i) == "}" then return out, i + 1 end
    while true do
        i = skip_ws(s, i)
        if s:sub(i, i) ~= '"' then return nil, i end
        local key
        key, i = parse_string(s, i)
        if key == nil then return nil, i end
        i = skip_ws(s, i)
        if s:sub(i, i) ~= ":" then return nil, i end
        i = skip_ws(s, i + 1)
        local val
        val, i = parse_value(s, i)
        out[key] = val
        i = skip_ws(s, i)
        local c = s:sub(i, i)
        if c == "," then
            i = i + 1
        elseif c == "}" then
            return out, i + 1
        else
            return nil, i
        end
    end
end

local function parse_array(s, i)
    local out = {}
    i = skip_ws(s, i + 1)
    if s:sub(i, i) == "]" then return out, i + 1 end
    while true do
        local val
        val, i = parse_value(s, skip_ws(s, i))
        out[#out + 1] = val
        i = skip_ws(s, i)
        local c = s:sub(i, i)
        if c == "," then
            i = i + 1
        elseif c == "]" then
            return out, i + 1
        else
            return nil, i
        end
    end
end

parse_value = function(s, i)
    i = skip_ws(s, i)
    local c = s:sub(i, i)
    if     c == "{" then return parse_object(s, i)
    elseif c == "[" then return parse_array(s, i)
    elseif c == '"' then return parse_string(s, i)
    elseif c == "t" then return true,  i + 4
    elseif c == "f" then return false, i + 5
    elseif c == "n" then return nil,   i + 4
    else
        return parse_number(s, i)
    end
end

function Json.parse(s)
    local ok, val = pcall(function()
        local v, _ = parse_value(s, 1)
        return v
    end)
    if ok then return val end
    return nil
end

----------------------------------------------------------------------
-- Payload summarizers.
----------------------------------------------------------------------

local function truncate(s, n)
    if s == nil then return "" end
    s = tostring(s)
    if #s <= n then return s end
    -- s:sub counts bytes, not codepoints. Walk back over UTF-8 continuation
    -- bytes (10xxxxxx) so the truncation never lands mid-codepoint and
    -- emits invalid UTF-8 in the column for Vietnamese/emoji input.
    local cut = n
    while cut > 0 do
        local b = s:byte(cut)
        if b < 0x80 or b >= 0xC0 then break end
        cut = cut - 1
    end
    if cut > 0 then
        local b = s:byte(cut)
        if b >= 0xC0 then cut = cut - 1 end
    end
    return s:sub(1, cut) .. "..."
end

local function shape_name(v)
    if type(v) == "number" then return SHAPE_TYPE_NAMES[v] or tostring(v) end
    if type(v) == "string" then return v end
    return "?"
end

local function summarize_draw(payload)
    local action = payload and payload.action
    if type(action) ~= "table" then return "(no action)" end
    local subtype = action.type or "?"
    local color = action.color or ""
    local stroke = action.strokeWidth
    if subtype == "pen" then
        local points = action.points
        local n = (type(points) == "table") and #points or 0
        return string.format("Pen pts=%d color=%s stroke=%s",
            n, color, tostring(stroke))
    elseif subtype == "shape" then
        return string.format("Shape=%s color=%s stroke=%s",
            shape_name(action.shapeType), color, tostring(stroke))
    elseif subtype == "line" then
        return string.format("Line color=%s stroke=%s", color, tostring(stroke))
    elseif subtype == "text" then
        return string.format("Text color=%s text=%q",
            color, truncate(action.text, 30))
    elseif subtype == "image" then
        return "Image"
    elseif subtype == "erase" then
        return string.format("Erase stroke=%s", tostring(stroke))
    end
    return string.format("action=%s", tostring(subtype))
end

local function summarize_chat(payload)
    if not payload then return "(empty chat)" end
    return string.format("text=%q", truncate(payload.message, 40))
end

local function summarize_ai_command(payload)
    if not payload then return "(empty ai cmd)" end
    return string.format("prompt=%q", truncate(payload.prompt, 40))
end

local function summarize_cursor(payload)
    if not payload then return "(no cursor)" end
    return string.format("x=%s y=%s color=%s",
        tostring(payload.x), tostring(payload.y), tostring(payload.color or ""))
end

local function summarize_error(payload)
    if not payload then return "(no detail)" end
    return string.format("error=%q", truncate(payload.message or payload.error, 60))
end

local function summarize_ai_result(payload)
    if not payload then return "(no detail)" end
    if payload.error and payload.error ~= "" then
        return string.format("error=%q", truncate(payload.error, 60))
    end
    local actions = payload.actions
    local n = (type(actions) == "table") and #actions or 0
    return string.format("actions=%d", n)
end

local function summarize(type_name, payload)
    if     type_name == "Draw"        then return summarize_draw(payload)
    elseif type_name == "DrawPreview" then return summarize_draw(payload)
    elseif type_name == "ChatMessage" then return summarize_chat(payload)
    elseif type_name == "AiCommand"   then return summarize_ai_command(payload)
    elseif type_name == "AiResult"    then return summarize_ai_result(payload)
    elseif type_name == "CursorMove"  then return summarize_cursor(payload)
    elseif type_name == "Error"       then return summarize_error(payload)
    end
    return "(no detail)"
end

----------------------------------------------------------------------
-- Per-message dissection.
----------------------------------------------------------------------

local function resolve_type(type_field)
    if type(type_field) == "number" then
        return MESSAGE_TYPE_NAMES[type_field] or string.format("Type#%d", type_field),
               type_field
    elseif type(type_field) == "string" then
        return type_field, -1
    end
    return "Unknown", -1
end

local function dissect_message(line_tvb, pinfo, root_tree)
    local subtree = root_tree:add(netdraw_proto, line_tvb,
        "NetDraw message (" .. line_tvb:len() .. " bytes)")

    local raw = line_tvb:raw()
    -- Strip trailing newline (and optional CR) before parsing.
    local trimmed = raw:gsub("[\r\n]+$", "")
    local doc = Json.parse(trimmed)

    if type(doc) ~= "table" then
        subtree:add(f_raw, line_tvb, truncate(trimmed, 200))
        pinfo.cols.info:append(" [malformed]")
        return
    end

    local type_name, type_id = resolve_type(doc.type)
    local sender   = doc.senderName or ""
    local sender_id = doc.senderId  or ""
    local room     = doc.roomId     or ""
    local version  = tonumber(doc.version) or 0
    local ts       = tonumber(doc.timestamp) or 0
    local summary  = summarize(type_name, doc.payload)

    subtree:add(f_version,  line_tvb, version)
    subtree:add(f_type,     line_tvb, type_name)
    if type_id >= 0 then subtree:add(f_type_id, line_tvb, type_id) end
    subtree:add(f_sender,   line_tvb, sender)
    subtree:add(f_senderid, line_tvb, sender_id)
    subtree:add(f_room,     line_tvb, room)
    subtree:add(f_ts,       line_tvb, ts)
    subtree:add(f_summary,  line_tvb, summary)

    subtree:append_text(string.format(
        ": %s v=%d sender=%s room=%s  %s",
        type_name, version, sender, room, summary))

    -- Wireshark's Info column is shared across all PDUs in the segment;
    -- append so multiple framed messages all show up.
    if pinfo.cols.info ~= nil then
        local prefix = (tostring(pinfo.cols.info) == "") and "" or " | "
        pinfo.cols.info:append(string.format(
            "%s%s sender=%s room=%s %s",
            prefix, type_name, sender, room, summary))
    end
end

----------------------------------------------------------------------
-- Binary frame dissection (Phase 1).
--
-- The binary frame is fully length-prefixed (6-byte header) so reassembly
-- is exact: read the 6 header bytes to learn payload-length, then desegment
-- until the full frame is in the buffer. Only the envelope is decoded in
-- Phase 1; per-type body summarizers ship in the Phase 5 dissector update.
----------------------------------------------------------------------

local function dissect_binary_message(frame_tvb, pinfo, root_tree)
    local frame_len = frame_tvb:len()
    local subtree = root_tree:add(netdraw_proto, frame_tvb,
        "NetDraw binary frame (" .. frame_len .. " bytes)")

    local magic   = frame_tvb(0, 1):uint()
    local version = frame_tvb(1, 1):uint()
    local type_id = frame_tvb(2, 1):uint()
    local payload_len = frame_tvb(3, 1):uint() * 0x10000
                      + frame_tvb(4, 1):uint() * 0x100
                      + frame_tvb(5, 1):uint()

    subtree:add(f_bin_magic,   frame_tvb(0, 1), magic)
    subtree:add(f_bin_version, frame_tvb(1, 1), version)
    subtree:add(f_bin_type_id, frame_tvb(2, 1), type_id)
    subtree:add(f_bin_length,  frame_tvb(3, 3), payload_len)

    local type_name = MESSAGE_TYPE_NAMES[type_id] or string.format("Type#%d", type_id)
    subtree:add(f_bin_type, frame_tvb(2, 1), type_name)

    -- 48-byte envelope: ts(8) + senderUint(4) + roomHash(4) + sessionToken(32).
    if frame_len >= 6 + 48 then
        local ts          = frame_tvb(6, 8):uint64()
        local sender_uint = frame_tvb(14, 4):uint()
        local room_hash   = frame_tvb(18, 4):uint()
        subtree:add(f_bin_ts,     frame_tvb(6, 8),  ts)
        subtree:add(f_bin_sender, frame_tvb(14, 4), sender_uint)
        subtree:add(f_bin_room,   frame_tvb(18, 4), room_hash)
        subtree:add(f_bin_token8, frame_tvb(22, 8))
    end

    -- Phase 1: every binary frame is acknowledged with BINARY_NOT_IMPLEMENTED.
    -- Per-type body decoders land in the Phase 5 dissector update; for now we
    -- only label the type and leave the body opaque so a mixed JSON+binary
    -- capture still walks cleanly.
    local body_len = math.max(0, payload_len - 48)
    local label = string.format("BinaryFrame[type=%s (not-impl-yet) bodyLen=%d]",
        type_name, body_len)
    subtree:add(f_bin_summary, frame_tvb, label)
    subtree:append_text(": " .. label)

    if pinfo.cols.info ~= nil then
        local prefix = (tostring(pinfo.cols.info) == "") and "" or " | "
        pinfo.cols.info:append(prefix .. label)
    end
end

----------------------------------------------------------------------
-- Top-level dissector with TCP reassembly.
----------------------------------------------------------------------

function netdraw_proto.dissector(tvb, pinfo, tree)
    local len = tvb:len()
    if len == 0 then return 0 end

    pinfo.cols.protocol = netdraw_proto.name
    pinfo.cols.info = ""

    -- Cache the segment's raw bytes once and search by absolute index;
    -- the prior `tvb(offset):raw()` per-iteration was O(n²) per segment
    -- on a TCP push that contained many framed messages.
    local raw = tvb:raw()
    local offset = 0
    while offset < len do
        local first = raw:byte(offset + 1)

        if first == 0xFE then
            -- Binary frame: read the 6-byte header to learn payload-length,
            -- then desegment until the whole frame is in hand.
            if len - offset < 6 then
                pinfo.desegment_offset = offset
                pinfo.desegment_len = DESEGMENT_ONE_MORE_SEGMENT
                return len
            end
            local payload_len = raw:byte(offset + 4) * 0x10000
                              + raw:byte(offset + 5) * 0x100
                              + raw:byte(offset + 6)
            local total = 6 + payload_len
            if len - offset < total then
                pinfo.desegment_offset = offset
                pinfo.desegment_len = total - (len - offset)
                return len
            end
            local frame_tvb = tvb(offset, total)
            dissect_binary_message(frame_tvb, pinfo, tree)
            offset = offset + total

        elseif first == 0x7B then
            -- JSON line: original path, find next '\n' from absolute offset.
            local nl = raw:find("\n", offset + 1, true)
            if nl == nil then
                pinfo.desegment_offset = offset
                pinfo.desegment_len = DESEGMENT_ONE_MORE_SEGMENT
                return len
            end
            local line_len = nl - offset
            local line_tvb = tvb(offset, line_len)
            dissect_message(line_tvb, pinfo, tree)
            offset = offset + line_len + 1

        elseif first == 0x0D or first == 0x0A or first == 0x09 or first == 0x20 then
            offset = offset + 1

        else
            -- Unknown framing byte. Consume one and keep going so a single
            -- corrupt byte in a capture doesn't hide every well-formed frame
            -- after it. The server's framer closes on this case; the dissector
            -- can't ask for a fresh capture, so the trade-off lands the other
            -- way (see docs/design/binary-frame.md "Wireshark dissector").
            offset = offset + 1
        end
    end

    return offset
end

local tcp_table = DissectorTable.get("tcp.port")
tcp_table:add(5000, netdraw_proto)  -- DrawServer default
tcp_table:add(5500, netdraw_proto)  -- Reserved for the future load-balancer port
