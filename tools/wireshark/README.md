# NetDraw Wireshark dissector

Custom Lua dissector that decodes NetDraw's newline-delimited JSON protocol so
TCP frames render with the message type, sender, room, and a one-line payload
summary instead of a wall of bytes.

## Install

Drop `netdraw.lua` into Wireshark's personal plugin directory. Wireshark loads
all `.lua` files in that directory at startup.

| OS | Path |
| --- | --- |
| Linux (Wireshark >= 4.0) | `~/.local/lib/wireshark/plugins/` |
| Linux (Wireshark < 4.0)  | `~/.config/wireshark/plugins/` |
| macOS | `~/.config/wireshark/plugins/` |
| Windows | `%APPDATA%\Wireshark\plugins\` |

If you don't know the exact path on your machine, open Wireshark and check
`Help -> About Wireshark -> Folders -> Personal Lua Plugins`.

After copying the file, restart Wireshark (or `Analyze -> Reload Lua Plugins`
on builds that expose it).

## Usage

The dissector auto-registers on TCP ports `5000` (DrawServer default) and
`5500` (reserved for the future load-balancer port). Capture on the loopback
interface while the server and a client are running. Frames carrying NetDraw
traffic show `Protocol: NetDraw` and an Info column like:

```
NetDraw  Draw v=1 sender=alice room=demo  Pen pts=42 color=#FF0000 stroke=3
NetDraw  ChatMessage v=1 sender=bob room=demo  text="Đẹp quá!"
NetDraw  AiCommand v=1 sender=alice room=demo  prompt="vẽ con mèo"
NetDraw  CursorMove v=1 sender=alice room=demo  x=120 y=85 color=#FF0000
```

If the server is bound to a non-default port, decode it on the fly:
right-click any frame -> `Decode As...` -> set the destination port to
`netdraw`.

## Verify it's loaded

```
nix run nixpkgs#lua5_4 -- -e 'local f, err = loadfile("netdraw.lua"); print(f and "syntax OK" or err)'
```

In Wireshark itself, `Analyze -> Enabled Protocols` should list **NetDraw**
under the `N` section.

## Limitations

- Targets the v1 newline-delimited JSON protocol only. The custom binary
  framing planned for P7.T4 will need a separate dissector branch.
- Unknown JSON fields are ignored, so additive protocol changes (new envelope
  fields, new payload variants) won't break decoding even before the dissector
  is updated. New `MessageType` enum values render as `Type#<N>` until added
  to the lookup table in `netdraw.lua`.
- Trusts the bytes it sees. Not hardened against malformed traffic; this is a
  course-grading aid, not a production analyzer.

See [test-capture.md](test-capture.md) for an end-to-end demo with `tshark`.
