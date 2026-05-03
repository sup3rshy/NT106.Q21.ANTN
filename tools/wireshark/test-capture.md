# NetDraw capture demo

A one-page recipe for showing the dissector at work on the loopback interface.

## Prereqs

- Dissector installed (see [README.md](README.md)).
- DrawServer running on `localhost:5000`.
- At least one client connected and joined to a room.

## Live tshark view

```
sudo tshark -i lo -Y "netdraw" -T fields \
    -e frame.number \
    -e netdraw.type \
    -e netdraw.sender \
    -e netdraw.room \
    -e netdraw.payload_summary
```

Expected output while a user joins the `demo` room, draws a pen stroke, sends
a chat message, and the cursor moves:

```
12  JoinRoom    alice  demo  (no detail)
13  RoomJoined  Server demo  (no detail)
27  CursorMove  alice  demo  x=120 y=85 color=#FF0000
31  Draw        alice  demo  Pen pts=42 color=#FF0000 stroke=3
38  ChatMessage alice  demo  text="Đẹp quá!"
44  AiCommand   alice  demo  prompt="vẽ con mèo"
```

## Capture to a file for the demo

```
sudo tshark -i lo -f "tcp port 5000" -w /tmp/netdraw.pcap
```

Open `/tmp/netdraw.pcap` in the Wireshark GUI, sort by `Protocol` and confirm
`NetDraw` rows appear. Click any one to see the per-field tree:

```
NetDraw collaborative drawing protocol
    Protocol version: 1
    Message type: Draw
    Message type id: 6
    Sender name: alice
    Sender id: u-1
    Room id: demo
    Timestamp (ms): 1700000000123
    Payload summary: Pen pts=42 color=#FF0000 stroke=3
```

## Filter recipes

| Goal | Filter |
| --- | --- |
| All NetDraw traffic | `netdraw` |
| Only chat from one user | `netdraw.type == "ChatMessage" && netdraw.sender == "alice"` |
| Only drawing in a room | `netdraw.type == "Draw" && netdraw.room == "demo"` |
| AI commands only | `netdraw.type == "AiCommand"` |
| Cursor traffic out (usually noisy) | `!(netdraw.type == "CursorMove")` |

## Troubleshooting

- **Nothing decodes as NetDraw**: confirm the server port. The dissector binds
  to 5000 and 5500 by default; for any other port, `Decode As... -> netdraw`.
- **Frames show `Type#N`**: a new `MessageType` enum value was added without
  updating the lookup table in `netdraw.lua`. Append the new id to
  `MESSAGE_TYPE_NAMES`.
- **Garbled UTF-8 in the Info column**: that's a terminal encoding issue, not
  a dissector bug. The fields are valid UTF-8 - the GUI renders them fine.
