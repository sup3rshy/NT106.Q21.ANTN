# NetDraw - Ứng dụng Vẽ Chung Qua Mạng

Đồ án môn **Lập Trình Mạng Căn Bản** - Trường ĐH Công Nghệ Thông Tin (UIT), ĐHQG-HCM.

## Mô tả

NetDraw là ứng dụng vẽ cộng tác real-time qua mạng TCP, cho phép nhiều người dùng cùng vẽ trên một canvas chung. Tích hợp MCP Server (Model Context Protocol) để hỗ trợ vẽ bằng lệnh AI ngôn ngữ tự nhiên (tiếng Việt/Anh).

## Kiến trúc hệ thống

```
┌─────────────┐                        ┌──────────────┐
│  Client 1   │◄──── TCP Socket ──────►│              │
│  (WPF)      │                        │  DrawServer  │
└─────────────┘                        │  (port 5000) │
                                       │              │     TCP
┌─────────────┐                        │  - Room mgmt │◄──────────►┌────────────┐
│  Client 2   │◄──── TCP Socket ──────►│  - Broadcast │            │ MCP Server │
│  (WPF)      │                        │  - History   │            │ (port 5001)│
└─────────────┘                        │  - AI relay  │            │            │
                                       │              │            │ - AI Parse │
┌─────────────┐                        │              │            │ - Claude   │
│  Client N   │◄──── TCP Socket ──────►│              │            │   API      │
│  (WPF)      │                        └──────────────┘            └────────────┘
└─────────────┘
```

## Công nghệ sử dụng

| Thành phần | Công nghệ |
|---|---|
| Ngôn ngữ | C# (.NET 8 LTS) |
| Giao diện | WPF (Windows Presentation Foundation) |
| Giao tiếp mạng | TCP Socket (`System.Net.Sockets`) |
| Giao thức | JSON messages + newline delimiter |
| Serialization | Newtonsoft.Json |
| AI Integration | MCP Server + Claude API (tùy chọn) |

## Cấu trúc Solution

```
NetDraw/
├── NetDraw.sln
│
├── NetDraw.Shared/              # Thư viện dùng chung
│   ├── Protocol/
│   │   ├── MessageType.cs       # Enum các loại message
│   │   └── NetMessage.cs        # Định dạng message (serialize/deserialize)
│   └── Models/
│       ├── DrawAction.cs        # Model hành động vẽ (pen, line, shape, text, eraser, image...)
│       └── RoomInfo.cs          # Model Room, User, Chat, AI payload, Cursor, DrawingFile
│
├── NetDraw.Server/              # TCP Server
│   ├── Program.cs               # Entry point (mặc định port 5000)
│   ├── DrawServer.cs            # Server chính: accept connection, route message, kết nối MCP
│   ├── ClientHandler.cs         # Xử lý từng client (đọc/gửi message)
│   ├── Room.cs                  # Quản lý phòng: danh sách user, lịch sử vẽ, broadcast
│   └── FallbackAiParser.cs      # Parser AI dự phòng khi MCP chưa kết nối
│
├── NetDraw.Client/              # WPF Client
│   ├── MainWindow.xaml/cs       # Giao diện chính + logic vẽ, kết nối, chat, AI
│   ├── NetworkClient.cs         # TCP client wrapper (connect, send, receive)
│   ├── CanvasRenderer.cs        # Render DrawAction thành WPF UIElement
│   ├── ColorPickerDialog.xaml/cs    # Bảng chọn màu tùy chỉnh (RGB, HEX, 39+ quick colors)
│   ├── ImageImportDialog.xaml/cs    # Import ảnh với bộ lọc (đen trắng, sepia, sketch...)
│   ├── TemplateDialog.xaml/cs       # Chọn template mẫu (grid, wireframe, flowchart...)
│   └── InputDialog.xaml/cs          # Dialog nhập text
│
└── NetDraw.McpServer/           # MCP Server (AI)
    ├── Program.cs               # Entry point (mặc định port 5001)
    ├── McpDrawServer.cs         # MCP TCP server, tích hợp Claude API
    └── EnhancedAiParser.cs      # Parser AI nâng cao (rule-based, hỗ trợ scene phức tạp)
```

## Giao thức truyền thông

Giao thức sử dụng **JSON + newline (`\n`) delimiter** trên TCP Socket.

### Cấu trúc message

```json
{
  "type": "DrawLine",
  "senderId": "f58015ea",
  "senderName": "User818",
  "roomId": "room1",
  "timestamp": 1712419200000,
  "payload": { ... }
}
```

### Các loại message chính

| Type | Hướng | Mô tả |
|---|---|---|
| `JoinRoom` | Client → Server | Yêu cầu vào phòng |
| `RoomJoined` | Server → Client | Xác nhận đã vào phòng |
| `UserJoined` / `UserLeft` | Server → All | Thông báo user vào/rời |
| `DrawLine` | Client ↔ Server | Vẽ nét bút / đường thẳng / mũi tên |
| `DrawShape` | Client ↔ Server | Vẽ hình (rect, circle, ellipse, triangle, star) |
| `DrawText` | Client ↔ Server | Chèn text |
| `Erase` | Client ↔ Server | Xóa (eraser) |
| `ClearCanvas` | Client ↔ Server | Xóa toàn bộ canvas |
| `CanvasSnapshot` | Server → Client | Gửi lịch sử vẽ cho user mới join |
| `DrawingUpdate` | Client → Server → All | Live preview nét đang vẽ (real-time) |
| `CursorMove` | Client → Server → All | Vị trí chuột real-time |
| `MoveObject` | Client ↔ Server | Di chuyển đối tượng đã vẽ |
| `DeleteObject` | Client ↔ Server | Xóa đối tượng cụ thể |
| `Undo` / `Redo` | Client ↔ Server | Hoàn tác / Làm lại |
| `ChatMessage` | Client ↔ Server | Tin nhắn chat |
| `AiCommand` | Client → Server → MCP | Lệnh vẽ AI |
| `AiDrawResult` | MCP → Server → All | Kết quả AI trả về (danh sách DrawAction) |

## Hướng dẫn chạy

### Yêu cầu

- .NET 8 SDK trở lên
- Windows (WPF client yêu cầu Windows)

### Bước 1: Build

```bash
cd D:\NT106.Q21.ANTN
dotnet build
```

### Bước 2: Chạy (mở 3 terminal riêng)

```bash
# Terminal 1: MCP Server (khởi động trước)
dotnet run --project NetDraw.McpServer

# Terminal 2: Draw Server
dotnet run --project NetDraw.Server

# Terminal 3: Client (mở nhiều terminal để test multi-user)
dotnet run --project NetDraw.Client
```

### Bước 3: Sử dụng

1. Nhập IP server, port (mặc định `127.0.0.1:5000`), tên người dùng
2. Nhấn **Kết nối**
3. Chọn phòng và nhấn **Vào**
4. Bắt đầu vẽ!

### Tùy chọn: AI qua Claude API

Để sử dụng AI nâng cao thay vì rule-based parser:

```bash
set CLAUDE_API_KEY=sk-ant-xxxxx
dotnet run --project NetDraw.McpServer
```

## Tính năng

### Công cụ vẽ
- Bút vẽ tự do (Pen)
- Bút thư pháp (Calligraphy) - nét dày/mỏng theo hướng vẽ
- Bút highlight (Highlighter) - bán trong suốt, nét rộng
- Bút phun sơn (Spray) - hiệu ứng airbrush
- Đường thẳng (Line)
- Mũi tên (Arrow) - đường thẳng có đầu mũi tên
- Hình chữ nhật, hình tròn, hình elip, tam giác, ngôi sao
- Chèn text
- Tẩy (Eraser)

### Tùy chỉnh nét vẽ
- Chọn màu nhanh (14 màu) + bảng chọn màu tùy chỉnh (RGB slider, HEX, 39+ quick colors)
- Điều chỉnh kích thước nét (1-30)
- Điều chỉnh độ trong suốt (Opacity 10%-100%)
- Kiểu nét: Nét liền, Nét đứt, Nét chấm (Dash Style)
- Tô màu nền hình (Fill)

### Thao tác Canvas
- Chọn / Di chuyển đối tượng (Select tool)
- Xóa đối tượng đã chọn (Delete)
- Undo / Redo (hỗ trợ undo theo nhóm cho template)
- Xóa toàn bộ canvas
- Pan (kéo chuột phải) & Zoom (scroll wheel, 20%-500%)
- Lưu / Mở project (.ndr) - lưu toàn bộ bản vẽ bao gồm ảnh import
- Xuất ảnh PNG

### Hình ảnh & Template
- Import ảnh (PNG, JPG, BMP, GIF, TIFF) với bộ lọc:
  - Gốc, Đen trắng (Grayscale), Sepia (Cổ điển), Âm bản (Invert)
  - Tương phản cao (High Contrast), Phác thảo (Sketch - edge detection)
  - Điều chỉnh kích thước 10%-200%
- 10 template mẫu:
  - Grid (lưới ô vuông), Ruled Lines (giấy kẻ dòng), Dot Grid (lưới chấm)
  - Coordinate (hệ tọa độ XY), Storyboard (6 ô), Wireframe (giao diện web)
  - Flowchart (lưu đồ), Music Sheet (khuông nhạc), Calendar (lịch tháng)
  - Comic (khung truyện tranh)

### Mạng & Cộng tác
- Kết nối TCP Socket real-time
- Hệ thống phòng vẽ (tạo/join/leave)
- Đồng bộ canvas giữa tất cả user trong phòng
- **Live Drawing Preview** - user khác thấy nét vẽ đang hình thành real-time
- Con trỏ chuột real-time - hiển thị vị trí và tên user khác trên canvas
- User mới join nhận lại toàn bộ bản vẽ (canvas snapshot)
- Danh sách user online (hiển thị màu riêng mỗi user)
- Chat trong phòng

### AI Drawing (MCP)
- Gõ lệnh bằng tiếng Việt hoặc tiếng Anh
- Hỗ trợ hình đơn: `vẽ hình tròn màu đỏ ở giữa`
- Hỗ trợ vật thể: `vẽ ngôi nhà`, `vẽ cây`, `vẽ mặt trời`, `vẽ xe hơi`
- Hỗ trợ scene phức tạp: `vẽ phong cảnh có mặt trời mây cây`
- Hỗ trợ đặc biệt: `vẽ cầu vồng`, `vẽ mặt cười`, `vẽ hoa`, `vẽ trái tim`

## Phím tắt

| Phím | Chức năng |
|---|---|
| V | Chọn / Di chuyển |
| P | Bút vẽ |
| L | Đường thẳng |
| A | Mũi tên |
| R | Hình chữ nhật |
| C | Hình tròn |
| E | Hình elip |
| T | Tam giác |
| H | Bút highlight |
| X | Tẩy |
| Delete | Xóa đối tượng đã chọn |
| Ctrl+Z | Undo |
| Ctrl+Y | Redo |
| Ctrl+S | Lưu project |
| Ctrl+O | Mở project |
| Ctrl+0 | Reset zoom |
| Scroll wheel | Zoom in/out |
| Chuột phải kéo | Pan canvas |

## Phân công nhóm (3 thành viên)

| Thành viên | Module | Nhiệm vụ chính |
|---|---|---|
| TV1 | Server | TCP Server, quản lý phòng, broadcast, protocol, xử lý kết nối |
| TV2 | Client | Giao diện WPF, canvas vẽ, toolbar, rendering, UX |
| TV3 | MCP + AI | MCP Server, AI parser, tích hợp Claude API, chat, shared models |

## Ví dụ lệnh AI

```
vẽ hình tròn màu đỏ ở giữa
vẽ hình vuông xanh dương to ở góc trái trên
vẽ ngôi nhà ở giữa
vẽ mặt trời ở góc phải trên
vẽ cây ở bên trái
vẽ phong cảnh có mặt trời mây cây nhà
vẽ cầu vồng
vẽ mặt cười vàng
vẽ hoa hồng ở giữa
vẽ trái tim đỏ
vẽ xe hơi màu xanh
draw red circle at center
draw blue rectangle big
```
