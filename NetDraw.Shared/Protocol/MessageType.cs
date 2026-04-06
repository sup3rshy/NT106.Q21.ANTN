namespace NetDraw.Shared.Protocol;

/// <summary>
/// Các loại message trong giao thức NetDraw
/// </summary>
public enum MessageType
{
    // === Kết nối & Phòng ===
    JoinRoom,           // Client -> Server: xin vào phòng
    LeaveRoom,          // Client -> Server: rời phòng
    RoomJoined,         // Server -> Client: đã vào phòng thành công
    RoomLeft,           // Server -> Client: đã rời phòng
    UserJoined,         // Server -> All: user mới vào phòng
    UserLeft,           // Server -> All: user rời phòng
    RoomUserList,       // Server -> Client: danh sách user trong phòng
    RoomList,           // Server -> Client: danh sách phòng
    RequestRoomList,    // Client -> Server: xin danh sách phòng

    // === Vẽ ===
    DrawLine,           // Vẽ đường thẳng/nét bút
    DrawShape,          // Vẽ hình (circle, rect, ellipse)
    DrawText,           // Vẽ text
    Erase,              // Xóa vùng
    ClearCanvas,        // Xóa toàn bộ canvas
    Undo,               // Hoàn tác
    Redo,               // Làm lại
    CanvasSnapshot,     // Server -> Client: snapshot canvas hiện tại (cho user mới join)

    // === Chat ===
    ChatMessage,        // Tin nhắn chat

    // === AI / MCP ===
    AiCommand,          // Client -> Server: lệnh AI (text)
    AiDrawResult,       // Server -> All: kết quả AI vẽ (shapes)
    AiError,            // Server -> Client: lỗi AI

    // === Cursor ===
    CursorMove,         // Client -> Server -> All: vị trí chuột real-time

    // === Object manipulation ===
    MoveObject,         // Di chuyển object
    DeleteObject,       // Xóa object cụ thể

    // === Hệ thống ===
    Ping,
    Pong,
    Error
}
