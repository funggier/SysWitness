# HANDOFF ล่าสุด — SysWitness Completion Checkpoint

วันที่: 24 พฤศจิกายน 2026 (Asia/Bangkok)
จุดพัก: ผู้ใช้แจ้งว่าโควต้า 5 ชั่วโมงใกล้หมด จึงหยุดเปิดงานใหม่และบันทึกโค้ดที่ทดสอบแล้ว
เป้าหมายถาวร: ผู้ใช้สั่งพัฒนาต่อจนความสามารถครอบคลุมแผนหลัก ไม่ใช่เพียงออก release หนึ่งรอบ

## Repository และการเริ่มใหม่

- Local: `T:\Space\Projects\ProjectsProgram\SysWitness`
- GitHub: `funggier/SysWitness`, branch `main`, Public, All Rights Reserved
- ฐานก่อนงานรอบนี้: `1ca9573679379052bc398bc40f48ce38d4fa7153`
- Checkpoint นี้รวมเป็น commit ชื่อ `feat: checkpoint native telemetry and read-only clients`; ดู SHA จริงจาก Git/GitHub ก่อนทำต่อ
- **ตรวจ git status, branch/HEAD, GitHub, CI จริงก่อนแก้ไขเสมอ** เอกสารนี้อาจเก่ากว่าโค้ด
- เริ่มจากอ่านไฟล์นี้, `COMPLETION_PLAN_TH.md`, `API_HARDENING_NEXT.md`, `ROADMAP.md` และโค้ดจริง รักษางานที่มีอยู่ ห้าม reset/clean ทับงานผู้ใช้

## Release ที่ยืนยันแล้ว

`v0.1.0-preview.3` เผยแพร่แล้วจาก commit ฐานข้างต้น หลัง CI success
Asset: `SysWitness-0.1.0-preview.3-win-x64-portable.zip`
SHA-256: `58B06819231070C90EDC189F00EA6D3B0C31729ACC3ACF7E3B4EFAB4D1698EBA`
Release URL: https://github.com/funggier/SysWitness/releases/tag/v0.1.0-preview.3
**Release นี้ไม่รวมงานใหม่ทั้งหมดด้านล่าง** ยังไม่ได้สร้าง/ยืนยัน Portable release ใหม่สำหรับ checkpoint นี้

## งานใหม่ใน Source ที่รวมและทดสอบแล้ว

1. **Process identity projection**
   - `process_identity_projection` แยก Derived identity จาก Raw Evidence, มี Subject/Parent GUID indexes
   - สร้าง/Backfill เดิมเป็น Batch; Record ใหม่เขียน Projection ใน Transaction เดียวกับ Event/Bookmark
   - Foreign key cascade ตาม Retention; ทดสอบ Restart/Partial backfill/Rollback/Retention
   - Process/Network GUID feeds, Activity และ Tree ใช้ Index แทน XML UDF query matching
   - `GET /api/v1/storage/projections` แสดง Version/Eligible/Indexed/Pending
   - ยังไม่ได้ทำ benchmark ประวัติขนาดใหญ่หรือ migration disk-reserve/error recovery แบบครบ

2. **Native Network**
   - โปรเจกต์ `SysWitness.Collectors.Windows` อ่าน TCP/UDP IPv4/6 ผ่าน IP Helper
   - `SYSWITNESS_NETWORK_MODE=auto|off`, default auto; Poll ประมาณ 3 วินาที
   - เก็บ firstObserved/changed/notObserved ตาม Snapshot ไม่อ้างว่าเป็น connect/disconnect จริง
   - PID-only, UDP ไม่มี Remote/Direction ที่เดา; Partition อ่านล้มเหลวไม่ทำให้เกิดการลบเทียม
   - `GET /api/v1/network/inventory` ต้อง `network.read`; Coverage มี `networkCollector`
   - ทดสอบ Layout/bounds/retry/partial failure และ Loopback ของ Process ทดสอบจริง
   - DNS ETW ยังไม่ได้เพิ่ม; nonzero live link-local IPv6 scope ยังไม่ยืนยัน

3. **System semantic activity**
   - `GET /api/v1/activity/{kind}` แบบ Fixed Routes: files, registry, authentication, services, drivers, tasks, devices, modules, process-access
   - DTO/Core/Storage/API แยกไฟล์; Provider+Channel+ID และ ObjectType/DriverType กรองก่อน Paging
   - เฉพาะ fields ที่ยืนยันและ Whitelist; ไม่มี Raw XML/Task definition XML; ไม่ใช้ System/Execution PID เป็น Actor
   - scopes ใหม่: files.read, registry.read, authentication.read, services.read (รวม drivers/tasks), devices.read; modules/process-access ใช้ processes.read
   - ระบบอ่าน retained Sysmon/Security/System; Source เหล่านี้บางส่วนยังยืนยันด้วย Fixture เท่านั้น
   - SCM semantic รองรับ install7045; ยังไม่เดา7036/7040 XML mapping
   - ค่า Owner scopes ใน portable bootstrap เดิมยังไม่ได้ขยายสำหรับหมวดใหม่ ต้องจัดการ explicit local key configuration ก่อนใช้งานจริง

4. **Dashboard**
   - `dashboard/` ภาษาไทย, Optional static client; Source copy ไป `dashboard/` ใน build/publish output
   - API serve whitelist /, /index.html, /styles.css, /model.js, /app.js พร้อม CSP/self และ nosniff
   - Token เฉพาะหน่วยความจำและ Header, ไม่ LocalStorage/URL; text-only rendering
   - มี overview/events/process/timeline/activity/tree/network/system categories/storage
   - 10 Node behavior tests ผ่าน; **ยังไม่ได้เปิดตรวจด้วย Browser จริง**
   - Core ไม่ต้องมีโฟลเดอร์ Dashboard จึงเก็บข้อมูลต่อได้; ไม่มี UI ควบคุม Windows

5. **SDK และ MCP**
   - `SysWitness.Sdk`: Named GET methods, literal loopback only, no proxy/redirect, response caps และ token protection
   - `SysWitness.Mcp`: stdio JSON-RPC, 24 Fixed read-only tools, bounded input/concurrency/rate/cancellation
   - รองรับ MCP **2025-11-25 เท่านั้น**; ไม่อ้างว่าเป็น revision ล่าสุดหรือรองรับ2026 lifecycle
   - 15 behavioral cases ผ่านทั้ง Debug และ Release; ใช้ Fake backend/ข้อมูลจำลอง
   - ยังไม่ยืนยันกับ external MCP host และยังไม่แยกแพ็กเกจ Client ใน release
   - .sln รวม Collectors, SDK, MCP, ClientTests แล้ว; CI เพิ่ม ClientTests และ Dashboard tests

## ผลทดสอบก่อน Commit

- `dotnet build SysWitness.sln -c Release --nologo -warnaserror`: PASS 0 warnings/0 errors
- `dotnet run --project tests/SysWitness.SmokeTests -c Release --no-build`: PASS (รวม Projection/SystemActivity/NativeNetwork)
- `powershell -NoProfile -ExecutionPolicy Bypass -File tests/Run-ApiSmoke.ps1`: PASS รวม Windows System/Application จริงและ Restart
- `powershell -NoProfile -ExecutionPolicy Bypass -File tests/Test-NativeNetwork.ps1 -Mode auto`: PASS endpoint ของ Process ทดสอบและ disappearance diff
- `node --test tests/DashboardTests/model.test.cjs tests/DashboardTests/app.test.cjs`: PASS 10/10
- `dotnet run --project tests/SysWitness.ClientTests -c Release --no-build`: PASS 15/15
- Projection isolated RED/GREEN ทดสอบ Missing API ก่อน Implementation แล้วตามด้วย Backfill/Atomic rollback/Retention PASS
- CI ของ checkpoint ต้องตรวจแยกจาก CI release ฐาน ไม่ใช้ผลเก่าอ้างว่าผ่าน

## งานที่ยังต้องทำก่อนถือว่าความสามารถครบ

ลำดับถัดไปที่แนะนำ:
1. อ่าน `API_HARDENING_NEXT.md`: Key reload/revoke, per-channel SQL constraints ทุก route/metadata, Rate/Concurrency/Response budgets, Read audit และ Raw evidence opt-in ยังไม่ทำ
2. Browser integration ของ Dashboard, HTTP tests สำหรับทุก System Activity kind/scopes และ Native -Mode off; external MCP host integration
3. เพิ่ม independent File watch / Registry / Services / Tasks / Devices observations พร้อม gaps, local opt-in และ actor unknown เมื่อไม่มีหลักฐาน
4. เพิ่ม DNS ETW/WFP/Existing USN adapters โดยไม่ติดตั้ง/เปลี่ยน Windows policy อัตโนมัติ; admin/provider unavailable ต้องบอกตรง ๆ
5. SQLite safe compaction/IO recovery/backpressure, multi-batch checkpoint replay regression และ performance benchmark
6. OpenAPI/schema/export policy และ API examples ที่ตรงกับโค้ดจริง
7. Packaging SDK/MCP แยก, Owner scopes/local settings ใหม่, full regression/CI/notices/artifact audit/relocation และ Release ใหม่

**ข้อควรระวัง Packaging:** Build-Portable.ps1 และ Test-PortableRelocation.ps1 ยังมีชื่อ default preview.3 เดิม อย่า overwrite release เดิม ใช้ Version ใหม่และส่ง ZipPath ชัดเจนจนกว่าจะอัปเดต default/version/docs อย่างสอดคล้อง

## ขอบเขตที่ห้ามเปลี่ยน

Monitoring-only, GET read API, Core independent of clients, Portable/Relocatable, local-first, All Rights Reserved
ไม่มี Kill/Block/Quarantine/Remote Shell/OS configuration endpoints
การจัดการ Subscription ชั่วคราวของ Collector ต่างจากการสั่งควบคุม Windows ผ่าน Data API; ห้ามติดตั้ง Driver/Provider หรือเปลี่ยน audit policy/elevate อัตโนมัติ

ไม่มี user production service ที่เปิดใหม่เพื่อ Dashboard ในรอบนี้ งานทดสอบใช้ process/port/data ชั่วคราว และสคริปต์ปิดของตัวเอง
