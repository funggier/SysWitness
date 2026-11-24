# SysWitness MCP Adapter

Executable .NET 10 แยกจาก API และ SDK เชื่อมต่อ Central API ที่ผู้ใช้เริ่มไว้แล้วผ่าน Loopback GET ไม่มีคำสั่งควบคุม Windows และไม่ต้องมี OpenAI API key

## Protocol ที่รองรับ

รองรับ MCP Revision **2025-11-25** เท่านั้น: initialize → notifications/initialized → tools/list หรือ tools/call; รองรับ ping, notifications/cancelled และ Shutdown เมื่อ stdin ปิด

Revision ล่าสุดในเอกสาร MCP ณ วันที่ตรวจสอบคือ 2026-07-28 ซึ่งเปลี่ยนเป็น Per-request Metadata และ server/discover รุ่นนี้ยังไม่รองรับกลไกนั้น server/discover จึงตอบ Method not found เพื่อให้ Client ที่รองรับทั้งสองยุคเลือกใช้ Legacy Handshake ได้ ไม่อ้างความเข้ากันได้กับทุก Host หรือ Revision อื่น

- UTF-8, JSON-RPC 2.0 หนึ่ง Message ต่อบรรทัด; stdout มีเฉพาะ Protocol, stderr มีข้อความวินิจฉัยที่ไม่แนบ Token
- Input จำกัด 65536 ไบต์ต่อ Frame และ JSON Depth 32; ปฏิเสธ Batch, Property ซ้ำ, ID ที่ไม่ใช่ String/Integer หรือ ID ซ้ำใน Session
- ID String ไม่เกิน 128 หน่วย UTF-16, Integer ภายใน Int64; Session จำกัด 100000 Request แล้วต้องเริ่ม Process ใหม่
- Tool Arguments ตรวจ Type, Required Field, Unknown Field, GUID, ช่วงเวลาและ Bounds จริงตาม Catalog ไม่เชื่อ Schema เพียงอย่างเดียว
- Protocol Error ใช้ JSON-RPC Error; Argument Value และ Backend Failure ใช้ Tool Result isError=true โดยคง HttpStatus ที่ทราบ
- Read พร้อมกันไม่เกิน 4, Token Bucket Burst 20 และเติม 10 Read/วินาที; Busy/Rate Limit เป็น Tool Error และไม่มี Automatic Retry
- Backend Timeout 15 วินาที, Response 1 MiB และ JSON Depth 32; ใช้ขีดจำกัดระหว่างอ่าน Stream
- Cancellation ทำงานได้ระหว่าง Backend รอ ไม่ส่งผลลัพธ์ของ Request ที่ยกเลิกแล้ว; ปิด stdin เพื่อยกเลิกงานค้างและออก
- ไม่ประกาศ Prompts, Resources, Tasks, Sampling, Elicitation, Subscriptions หรือ List Change Notifications

Tool Catalog คงที่ 24 รายการและไม่ได้ยืนยันว่า Token มีสิทธิ์ใช้ทั้งหมด Backend ตรวจ Scope ทุกครั้งและ 403 ยังคงเป็น Error. Catalog ไม่อ่าน Telemetry เพื่อ Discovery และไม่สร้าง/แก้ Token หรือ Scope

## เริ่มใช้งาน

```powershell
dotnet build src/SysWitness.Mcp -c Release
# ตั้ง SYSWITNESS_MCP_TOKEN ผ่าน Secret/Environment ของ Host ก่อนเริ่ม
$env:SYSWITNESS_API_BASE_URL = 'http://127.0.0.1:8769'
dotnet src/SysWitness.Mcp/bin/Release/net10.0/SysWitness.Mcp.dll
```

MCP Host ต้อง Launch dotnet พร้อม Absolute Path ของ DLL ที่ Build แล้ว หลีกเลี่ยง dotnet run ใน Transport เพราะข้อความ Build อาจปน stdout. ส่ง Token ผ่าน Environment ที่ Host จัดการอย่างปลอดภัย ไม่ใส่ Token ใน Command Line, Source หรือ Example Config. Environment ที่จำเป็น: SYSWITNESS_MCP_TOKEN (32–1024 Printable ASCII); SYSWITNESS_API_BASE_URL เป็น Origin HTTP บน 127.0.0.1 (Default Port 8769) ไม่รับ localhost ชื่อ DNS หรือ Remote Address

ตัวอย่าง Request หลัง Initialization:

```json
{"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"syswitness_get_events","arguments":{"limit":25,"channel":"System"}}}
```

ผลอ่านสำเร็จอยู่ใน content[0].text เป็น JSON. ไม่ประกาศ outputSchema หรือ structuredContent เพื่อไม่สร้างคำมั่น DTO ที่ยังไม่ได้ตรึง Backend ข้อมูลยังคงมีข้อจำกัดและความอ่อนไหวของต้นทาง; Adapter ไม่ตัดสินเหตุร้ายจาก Event เดียว

## Named Tools

ทุกชื่อมี Prefix syswitness_get_. Suffix ได้แก่ health, events, event, coverage, gaps, storage_status, retention, storage_projections, process_events, process_timeline, process_activity, process_tree, connections, dns, network_inventory, file_activity, registry_activity, authentication_activity, service_activity, driver_activity, task_activity, device_activity, module_activity, process_access_activity

คำอธิบายและ inputSchema จริงดู tools/list. ไม่รับ URL, Path, SQL, Shell, OS Action หรือ Activity Kind แบบอิสระ. ใช้ SDK ที่ปิด Proxy/Redirect และปกป้อง Credential ตาม [SDK](SDK_TH.md)

## Verification

```powershell
dotnet run --project tests/SysWitness.ClientTests -c Release
```

ทดสอบผ่าน Subprocess จริงและ TCP Backend จำลอง ครอบคลุม Catalog/Routes, Filter Encoding, ปฏิเสธ Input, HTTP Denial, Redirect, Response Bounds/JSON, Credential Echo, Configuration, Framing, Cancellation, Rate และ EOF. ยังไม่ได้ทดสอบกับ MCP Host ภายนอกหรือข้อมูลผู้ใช้จริง

อ้างอิงข้อกำหนดที่ตรึง:
- https://modelcontextprotocol.io/specification/2025-11-25/basic/lifecycle
- https://modelcontextprotocol.io/specification/2025-11-25/basic/transports
- https://modelcontextprotocol.io/specification/2025-11-25/server/tools
- https://modelcontextprotocol.io/specification/2025-11-25/basic/utilities/cancellation
- https://modelcontextprotocol.io/specification/2026-07-28/basic/versioning
