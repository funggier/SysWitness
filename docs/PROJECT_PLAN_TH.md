# SysWitness — แผนโครงการหลัก (2026-11-20)

> **สถานะล่าสุด:** มี Collector ของ Windows Event Log ที่แยก `System` และ `Application` ทำงานอิสระแล้ว โดยใช้ Bookmark และฐานข้อมูล SQLite ร่วมกันแต่จำตำแหน่งแยกตาม Channel; `Security` และ `Microsoft-Windows-Sysmon/Operational` เป็นตัวเลือกเสริมที่ต้องมีแหล่งข้อมูลและสิทธิ์อ่านก่อน ถ้าอ่านไม่ได้ ระบบจะแสดง `degraded` เป็นราย Channel โดยไม่หยุดตัวอื่น API ยังคงเป็น Read-only เท่านั้น ดูผลทดสอบและงานที่เหลือได้ใน [DEVELOPMENT_STATUS.md](DEVELOPMENT_STATUS.md).

## 1. วัตถุประสงค์
สร้างแพลตฟอร์ม **Deep System Observability** ที่ติดตามกิจกรรมของคอมพิวเตอร์อย่างละเอียดมาก เริ่มจาก Windows และเปิด **Central Read-only API** ให้ Dashboard, MCP, AI Local, SDK, และโปรแกรมภายนอกเรียกใช้ข้อมูลได้โดยไม่ขึ้นกับซอฟต์แวร์ AI ยี่ห้อใด

## 2. ข้อกำหนดที่ตกลงร่วมกัน (ห้ามเปลี่ยนทิศทาง)
1. **Observe only:** ตรวจดู เก็บข้อมูล ค้นหา แสดงความสัมพันธ์ วิเคราะห์ย้อนหลัง และส่งออกหลักฐานเท่านั้น
2. **No protection / control:** ไม่มี Firewall Block, Kill Process, Quarantine, Remote Shell, Windows Configuration Control, หรือ Command Execution API
3. **API-first, data-only:** API กลางให้ผู้ใช้อ่านและค้นข้อมูล ไม่ใช้แก้การตั้งค่าการตรวจจับและไม่ใช้ควบคุมเครื่อง
4. **Independent protection:** ถ้าสร้างโปรแกรมจัดการความปลอดภัยในอนาคต จะต้องมีระบบควบคุมและสิทธิ์ของตัวเอง; อ่านจาก SysWitness API เป็นเพียงข้อมูลประกอบ
5. **AI/MCP as clients:** MCP เป็น Adapter แยก ใช้ API เดียวกัน; AI ไม่ได้สิทธิ์พิเศษในการแก้เครื่อง
6. **Facts ≠ interpretations:** Raw Evidence และผล Correlation/Analysis แยกกัน; เหตุการณ์ที่ยังไม่พิสูจน์ต้องระบุว่าเป็นข้อสันนิษฐาน
7. **Coverage honesty:** แสดงแหล่งที่มาของ Log, Collector Health, gaps, dropped events, auditing disabled, unsupported field, และช่วงข้อมูลสูญหาย
8. **Local-first:** ไม่พึ่ง Cloud เพื่อเก็บเหตุการณ์; ไม่ออกอินเทอร์เน็ตโดยอัตโนมัติ; การอนุญาต Client แบบละเอียด
9. **Source-visible proprietary:** GitHub Public แต่ **All Rights Reserved**; ห้ามรวมข้อมูลเครื่องจริงใน Git

## 3. เป้าหมายการตรวจละเอียด
- Network: การเชื่อมต่อเข้า/ออก, IP/Port, protocols, DNS, process attribution, firewall/WFP decision ตามที่ provider รองรับ
- Authentication: logon success/failure, logon type, account SID, privilege use, remote session, log clearing
- Processes: process create/exit, PID + start time/Process GUID, parent-child, executable path, command-line (ข้อมูลอ่อนไหว), signature/hash ตาม availability
- File: create/rename/delete/write และ audited access ตามสิทธิ์/แหล่งข้อมูล; ไม่อ้างว่า USN บอก process หรือ every read ได้
- Registry: การแก้ไขรายการสำคัญ รวม startup persistence ตาม telemetry ที่เปิด
- Services/drivers/tasks: install/start/stop/config changes, scheduled task, loaded drivers ตามที่ source รองรับ
- Devices: removable storage / USB changes ตาม event providers
- Security/system: Defender, Firewall, Windows Audit Policy, system boot/shutdown, error/health
- Memory-related telemetry: process access / module loads เมื่อมี provider รองรับ; ไม่อ้างว่าเห็นทุก memory access
- Optional network packet metadata; full payload capture ไม่ใช่ค่าปกติและไม่แปล TLS ให้โดยอัตโนมัติ

## 4. แหล่งข้อมูลที่วางแผน
1. **Windows Event Log API:** เริ่มจาก Application/System/Security ที่ account มีสิทธิ์อ่าน
2. **Sysmon:** เพิ่ม Process GUID, process create, network, DNS, registry, file event ตาม configuration และ version
3. **ETW:** เฉพาะ providers ที่ตรวจสอบได้จริง; loss counters และ performance budget
4. **Windows Filtering Platform / Firewall logging:** การอนุญาตหรือปฏิเสธ network ตาม audit enablement
5. **NTFS USN Journal & Object Access Audit:** ใช้ประกอบกัน; file changes ไม่เท่ากับ file reads
6. **Optional router/syslog integration:** เพิ่มภายหลังเป็น collector เสริม; router บางตัวไม่ส่ง log ให้

การ Setup/Enable Windows auditing, Sysmon หรือ Windows Service อาจต้องใช้ administrator และเปลี่ยนการตั้งค่าบนเครื่องโดยชัดแจ้ง **แต่จะไม่มี API ให้ Client สั่ง setup เหล่านี้**

## 5. Event pipeline
```text
Source -> Collector -> Normalize -> Append-only raw evidence
                                -> Search indexes
                                -> Correlation rules & views
                                -> Read-only API
```
เก็บ timestamp ต้นทาง + เวลาที่รับข้อมูล, source identifier, stable event id, provider-native record identifier และ raw payload (โดยจำกัดข้อมูลอ่อนไหว) ให้เชื่อมกลับหลักฐานได้ ถ้าข้อมูลไม่ครบ ห้ามเติมด้วยการเดา

## 6. Storage และ performance

**ความคืบหน้า:** เพิ่มการตั้งค่า Local `SYSWITNESS_STORAGE_SOFT_LIMIT_MB` (เริ่มต้น 4096 MiB) เพื่อพัก Collector เมื่อประมาณการพื้นที่ที่จะใช้เกิน Soft Limit โดยไม่เลื่อน Bookmark และเพิ่ม `SYSWITNESS_RETENTION_DAYS` ซึ่ง **ปิดไว้โดยค่าเริ่มต้น** หากผู้ใช้เปิดใช้งานเองจึงจะลบสำเนา Event เก่าตามเวลาที่เก็บเข้าระบบ พร้อมบันทึกสรุปประวัติการลบแยก Channel โดยไม่แตะ Windows Event Log ต้นทาง การลบนี้ไม่รับประกันว่าไฟล์ SQLite จะหดลงทันที และยังไม่ใช่ Hard Quota ดู [STORAGE_POLICY_TH.md](STORAGE_POLICY_TH.md).
- MVP: SQLite local + schema migrations; serialized writes, indexes ตาม workload
- รักษา raw source แยกจาก normalized event และ derived analyses
- มี retention, disk-quota, backpressure, checkpoints และ event loss diagnostics
- ควบคุม ingestion throughput ได้โดยแยก provider; ไม่ทำให้ Windows ไม่เสถียรเพื่อตรวจทุกอย่าง
- ประเมินค่าใช้จ่าย CPU/RAM/disk และจำนวน events/sec จากการทดสอบจริงก่อนเปิด collector ที่มีปริมาณสูง

**เพิ่มเติม (Disk Guard):** มีการตรวจพื้นที่ว่าง Volume ที่เก็บฐานข้อมูลก่อนเขียน Batch โดยตั้งขั้นต่ำจาก `SYSWITNESS_MIN_FREE_DISK_MB` (เริ่มต้น 1024 MiB) หากพื้นที่ไม่เพียงพอหรืออ่านค่าพื้นที่ว่างไม่ได้ ระบบจะไม่เพิ่ม Bookmark และแจ้ง `disk_free_reserve_reached` หรือ `disk_free_space_unknown` ผ่านสถานะเพื่อการตรวจดู นี่เป็นการตรวจเชิงป้องกันก่อนเขียน ไม่ใช่การจองพื้นที่หรือ Hard Quota.

## 7. API และการเชื่อมต่อ
- /api/v1 บน HTTP JSON + OpenAPI; **GET** สำหรับข้อมูล และ **POST /events/query** สำหรับ read-only complex query เท่านั้น
- Optional SSE / WebSocket feed พร้อม cursor/replay ภายใต้ retention
- AuthN/AuthZ, scopes, pagination, time bounds, rate limiting, redaction, query budgeting และ audit read access
- API ไม่มี endpoint สำหรับ Windows actions, process control, firewall, configuration, arbitrary SQL หรือ code execution
- MCP adapter เป็น optional package รองรับ read-only tools/resources เท่านั้น และเรียก API เดียวกับ Dashboard
- Client ของระบบป้องกันภายนอกอ่านข้อมูลได้ ไม่สามารถสั่งให้ SysWitness ไปป้องกันแทน

**เพิ่มเติม (API Client Keys):** Client ภายนอกเชื่อมต่อผ่าน Read-only API ด้วย Token เฉพาะโปรแกรม โดยค่า Hash (SHA-256) กับ ID/สิทธิ์อ่าน `events.read`, `coverage.read`, `storage.read` เก็บในไฟล์ Local ที่ระบุด้วย `SYSWITNESS_CLIENT_KEYS_FILE` ไม่ต้องเก็บ Token จริงใน GitHub และไม่เปิด API ให้ Client แก้ไขสิทธิ์ของตัวเอง ปัจจุบันกำหนดสิทธิ์ได้ตามหมวดเท่านั้น ยังไม่รองรับขอบเขตระดับ Event/Channel หรือการหมุน Key แบบไม่ Restart ดู [API_CLIENTS_TH.md](API_CLIENTS_TH.md).

## 8. การตรวจสอบความเที่ยงตรง
แยก **observed**, **derived**, **inferred**, **unknown**; บันทึกแหล่งที่มาพร้อม correlation IDs, method, algorithm version และ confidence อ้างอิงหลักฐาน ไม่ใช้ AI verdict แทน raw event

## 9. UI เป้าหมาย
Dashboard, Live Events, Timeline, Process Tree, Network Explorer, Authentication, File/Registry, Coverage & Collector Health, Evidence Viewer, Search, Exports, Connected Clients (permission management เป็น local administrative UI ไม่ผ่าน data API)

## 10. ข้อจำกัด
- ไม่เห็น packets ที่ Router block ก่อนถึงเครื่อง
- encrypted TLS payload ไม่เปิดเผยเอง
- USN ไม่ใช่ audit ทุกการอ่านไฟล์
- Security events ต้องการ Windows Audit Policy และสิทธิ์อ่านที่เหมาะสม
- Kernel-level compromise หรือ audit gaps อาจซ่อนเหตุการณ์ได้
- Windows Event Logs และ command lines อาจมีข้อมูลส่วนบุคคลและ secrets; จำกัด exposure, retention, transport และสิทธิ์อ่าน

## 11. ลำดับพัฒนา
M0: Documentation/ADR/License/Repository.
M1: Event Contract + Windows Event Log collector + local store + read-only localhost API + tests.
M2: Authentication + fine-grained read permissions + health/coverage + replay checkpoints.
M3: Sysmon, Network/WFP/ETW, File/Registry/device collectors แยกเปิดใช้/วัด performance.
M4: Event correlation, deep timeline, process/network graph, analytics, exports.
M5: Dashboard, SDK, MCP read-only adapter และ compatibility tests.
M6: Packaging / portable-first + installer/setup boundary + performance, security, retention validation.

## 12. เกณฑ์สำเร็จ
- Core เก็บเหตุการณ์ได้แม้ไม่มี Dashboard/MCP/AI
- API อ่านข้อมูลจริงได้และไม่มี route/operation ป้องกันหรือควบคุม OS
- Tests ยืนยัน event identity, paging, permission scope, data retention, restart/resume และเหตุการณ์สูญหาย
- ข้อมูลไม่เคยถูกแปลงเป็นข้อกล่าวอ้างการโจมตีโดยไม่มีหลักฐาน
- สถานะความสามารถที่ยังทำไม่ได้ต้องแสดงตรงไปตรงมา
