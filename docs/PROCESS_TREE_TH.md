# Process Tree — ความสัมพันธ์ Parent/Child จากหลักฐาน

เพิ่มเมื่อ 24 พฤศจิกายน 2026 เป็น API ข้อมูลสำหรับ Client ที่ต้องการสำรวจ Process Tree โดยไม่อ่าน SQLite โดยตรง

## การเรียกใช้

```http
GET /api/v1/processes/cccccccc-2222-4333-8444-555555555555/tree?computer=tree-fixture-host&depth=3&limit=100
X-SysWitness-Token: <private-client-token>
```

GUID และชื่อเครื่องในตัวอย่างเป็นข้อมูลจำลอง ต้องใช้ GUID ที่ไม่เป็นศูนย์และชื่อ Computer ตรงตามหลักฐาน Sysmon โดย URL-encode Query Value ไม่มีการรวมชื่อสั้นกับ FQDN หรือตัวพิมพ์ใหญ่เล็กต่างกัน

ใช้สิทธิ์ `processes.read` เท่านั้น ข้อมูลอาจมี Command Line เช่นเดียวกับ Process API เดิม ไม่เปิดสิทธิ์ Network, Raw XML หรือการควบคุม Windows

| Query | ค่าเริ่มต้น | ขอบเขต |
|---|---|---|
| `computer` | ต้องระบุ | ตรงตาม Source, ไม่ว่าง, ไม่เกิน 256 ตัวอักษร, ไม่มี Control Character |
| `depth` | 3 | 0–8 ระดับลูกหลานจาก Root; Root มี Depth 0 |
| `limit` | 100 | 1–256 โหนด รวม Root และ Parent Reference |

ไม่รับ Cursor, Time Filter, Query GUID ซ้ำ หรือ Query อื่น ค่าผิด/ซ้ำตอบ 400, ไม่ยืนยันตัวตนตอบ 401, สิทธิ์ไม่พอตอบ 403, คำสั่งที่ไม่ใช่ GET หรือ Route ที่ไม่มีตอบ 404 หลังยืนยันตัวตน

## ความหมายของ Graph

- ใช้ Sysmon Event 1 ที่เก็บไว้จาก Computer เดียวกัน
- Node ใช้ Subject ProcessGuid เป็นตัวตน ไม่ใช้ PID, ชื่อไฟล์ หรือข้อความที่บังเอิญมี GUID
- Edge ใช้ ParentProcessGuid ของ **เหตุการณ์เริ่มโปรเซสลูก** และมี `evidence` เป็น Process Event ต้นทางครบทั้ง Event ID, Provider/Channel/Record ID, Computer และเวลา
- `basis=sourceReported` หมายถึงความสัมพันธ์ที่ต้นทางรายงาน ไม่ใช่การยืนยันเชิงสาเหตุอย่างอิสระ
- ระดับ Graph มี `quality=derived` และ `method=sysmon_reported_parent_guid_and_computer_v1`
- ขยาย Root และลูกหลานแบบ Breadth-first; Parent ที่อยู่นอกเส้นทางลูกหลานเป็น Reference Node ไม่ได้ไล่บรรพบุรุษทุกชั้นหรือดึงพี่น้องผ่าน Parent โดยอัตโนมัติ

## รูปแบบผลลัพธ์

`nodes[]` มี `processGuid`, `depth`, `evidenceState`, `hasConflictingParents` และ `creations[]`

- `creationInView`: มีหลักฐานเริ่มโปรเซสของ Node นั้นในผลลัพธ์นี้
- `referenceOnly`: มีเพียงตัวตนที่อ้างอิงหรือร้องขอ ยังไม่มี Creation ใน View นี้ ไม่ได้แปลว่าต้นทางไม่มีโปรเซสนั้น
- Parent Reference มี Depth เป็น null เว้นแต่พบว่าเป็นลูกหลานจาก Root ด้วย
- ถ้ามีหลาย Creation ของ GUID เดียวกัน จะเก็บหลักฐานที่อ่านได้ไว้ทั้งหมด และแจ้ง Parent GUID หลายค่าด้วย `hasConflictingParents`

`relationships[]` มี `parentProcessGuid`, `childProcessGuid`, `basis` และ `evidence` อ้างกลับ `GET /api/v1/events/{eventId}` ได้เมื่อมีสิทธิ์ `events.read` เพิ่มเติม ไม่คืน Raw XML

`unresolved[]` เก็บหลักฐานพร้อมเหตุผล เช่น `subject_guid_unavailable`, `parent_guid_unavailable` และ `self_reference` ไม่สร้าง GUID ใหม่เพื่ออุดข้อมูลที่หายไป ส่วน `hasCycle` แจ้งวงจรในความสัมพันธ์ที่แสดงหรือ Self-reference โดยไม่วนอ่านไม่สิ้นสุด

Unknown GUID/Computer ตอบ 200 พร้อม Root แบบ `referenceOnly` และ `sourceEventsUsed=0` ไม่อ้างว่าเป็นโปรเซสที่เคยถูกสังเกต

## ขอบเขตและความไม่ครบถ้วน

มีงบหลักฐานคงที่ไม่เกิน **1,000 Event ไม่ซ้ำกันต่อคำขอ** พร้อมการอ่านล่วงหน้าหนึ่งรายการเพื่อตรวจว่ามีข้อมูลต่อหรือไม่ แต่ละรอบเรียง Creation ที่เกี่ยวข้องจากใหม่ไปเก่าตามเวลาแล้ว Event ID

- `sourceEventsUsed` นับ Event ที่นำมาพิจารณา รวมรายการที่แสดงไม่ได้เพราะข้อมูลกำกวมหรือขีดจำกัด
- `evidenceLimitReached`: พบหลักฐานเพิ่มเติมหลังงบ 1,000 Event
- `nodeLimitReached`: มี Node/Parent Reference ที่เพิ่มไม่ได้เพราะ Limit
- `depthLimitReached`: พบลูกหลานที่อยู่นอก Depth ที่ขอ
- Flag เป็น false ไม่ได้รับประกันว่าประวัติครบ เพราะอาจมี Backfill/Provider/Retention/Coverage Gap มาก่อน
- ข้อมูลที่เกินขีดจำกัดอาจไม่ปรากฏใน Graph และ Flags ความขัดแย้ง/วงจรครอบคลุมเฉพาะ View ที่สร้างได้

ทุก Query สำหรับการขยาย Tree ใช้ SQLite Read Transaction เดียวกัน เพื่อไม่ผสมสถานะหลายช่วงเมื่อมีการเก็บหรือลบหลักฐานพร้อมกัน ไม่เขียน Graph ทับหลักฐาน ไม่เปลี่ยน Bookmark และไม่ต้อง Migration ฐานข้อมูล

งบ Event และ Node เป็นขอบเขตข้อมูลที่นำมาสร้างผลลัพธ์ ไม่ใช่ขีดจำกัดเวลาสแกน SQL เพราะยังอ่าน GUID จาก XML ขณะ Query ต้องวัดประสิทธิภาพและเพิ่ม Index/Query-time budget ต่อไป

## ข้อจำกัดของ Source

ยังไม่เชื่อม Security, WMI หรือ Snapshot ที่มีเพียง PID เข้ากับ Sysmon Tree โดยคาดเดา เวลา Source ไม่ได้พิสูจน์ลำดับการทำงาน Parent ที่ Provider รายงานอาจไม่ใช่คำอธิบายเหตุและผลครบถ้วนของโปรเซส

การยืนยัน Tree/Parent GUID บน Windows ปัจจุบันใช้ Fixture จำลองจนกว่าจะมี Sysmon ที่อ่านได้จริง Regression ของ System/Application และ Local Process Collector แยกทดสอบกับแหล่งจริงตามสิทธิ์

## โครงสร้าง

Contract: `ProcessTree.cs`; Core: `ProcessTreeBuilder.cs`; Storage: `SqliteProcessTree.cs`; API: `ProcessTreeEndpoints.cs`; Tests: `ProcessTreeSmoke.cs` และ `Run-ApiSmoke.ps1`

งานต่อ: วัดและปรับ Query บนประวัติขนาดใหญ่, ยืนยันกับ Sysmon จริง และออกแบบความสัมพันธ์ข้าม Source เมื่อมี Stable Identity ที่ตรวจสอบได้
