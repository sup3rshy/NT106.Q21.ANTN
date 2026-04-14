namespace NetDraw.Shared.Protocol;

/// <summary>
/// All message types in the NetDraw protocol, grouped by domain.
/// </summary>
public enum MessageType
{
    // === Connection & Room ===
    JoinRoom,           // Client -> Server: request to join a room
    LeaveRoom,          // Client -> Server: leave room
    RoomJoined,         // Server -> Client: joined successfully (carries history + user list)
    UserJoined,         // Server -> All: a new user entered the room
    UserLeft,           // Server -> All: a user left the room
    RoomList,           // Server -> Client: list of available rooms

    // === Drawing ===
    Draw,               // Client -> Server -> All: committed draw action
    DrawPreview,        // Client -> Server -> All: live preview (not persisted)
    ClearCanvas,        // Client -> Server -> All: clear entire canvas
    Undo,               // Client -> Server -> All: undo last action
    Redo,               // Client -> Server -> All: redo last undone action
    MoveObject,         // Client -> Server -> All: move an object by delta
    DeleteObject,       // Client -> Server -> All: delete a specific object
    CanvasSnapshot,     // Server -> Client: full canvas state for late joiners

    // === Presence ===
    CursorMove,         // Client -> Server -> All: real-time cursor position

    // === Chat & AI ===
    ChatMessage,        // Client <-> Server: chat text
    AiCommand,          // Client -> Server: AI prompt
    AiResult,           // Server -> All: AI-generated drawing actions

    // === System ===
    Error               // Server -> Client: error notification
}
