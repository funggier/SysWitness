# SysWitness — แผนเดินงานจนความสามารถครบ

อำนาจงาน: ผู้ใช้สั่งให้พัฒนาต่อจนความสามารถครอบคลุมแผนหลัก เมื่อ 24 พฤศจิกายน 2026
ฐานล่าสุด: `1ca9573679379052bc398bc40f48ce38d4fa7153`; preview.3 CI ผ่านและเผยแพร่ Portable ที่ตรวจ SHA-256 แล้ว

## หลักการส่งมอบ

- Monitoring / Observation เท่านั้น; API เป็น GET อ่านข้อมูล ไม่มีการควบคุม Windows, บล็อก, Shell หรือ Arbitrary SQL
- ใช้โครงสร้าง Modular เดิม, Portable/Relocatable, Local-first และ All Rights Reserved
- แยก implementation, fixture verification และ live verification ให้ชัด; Source ที่อ่านไม่ได้ต้อง Degraded/Unavailable ไม่แต่งข้อมูล
- ไม่ติดตั้ง Provider/Driver หรือเปลี่ยน Windows Audit Policy อัตโนมัติ; Collector ดูแล Subscription ของตัวเองเท่านั้น
- Dashboard, SDK และ MCP อ่านผ่าน API กลาง; Core เก็บข้อมูลต่อได้โดยไม่มี Client เหล่านี้

## งานและเกณฑ์ยืนยัน

| งาน | สถานะ | เกณฑ์ตรวจ |
|---|---|---|
| ปิด preview.3 | สำเร็จ | Release/Tag/Asset digest ตรงกับ Commit และ CI |
| Process identity index และ Query ที่ไม่สแกน XML ทุกแถว | กำลังทำ | Backfill เดิม, Transaction/Bookmark/Rollback, Retention, GUID/Host/Paging เดิม, Benchmark |
| สิทธิ์ราย Channel, Key reload/revoke, Rate/Concurrency limit, Read Audit, Raw evidence opt-in | วางแบบ/กำลังทำ | Rotation ในโปรเซสเดิม, Deny ทุก Query/ID/Graph/Metadata, Canary secrets, Bounded resource use |
| Native TCP/UDP IPv4/6 inventory fallback | กำลังทำ | Native buffers, TCP/UDP loopback ของ Process ทดสอบ, PID-only/Unknown, Partial capture failure |
| Sysmon/Security/System semantic views สำหรับ File/Registry/Auth/Services/Drivers/Tasks/Devices/Modules/Process access | กำลังทำ | Provider+Channel+ID, XML optional/ambiguous fields, Source provenance, Scopes |
| File watch / Registry / Services / Tasks / Devices independent observations | รอทำ | Local opt-in, Scoped reads, Snapshot/notification loss/gaps, No actor invention |
| DNS ETW / WFP / USN source adapters | รอทำ | Local opt-in, Permission/Provider diagnostics, Owned session/cursor/loss, Existing journal only |
| SQLite maintenance / IO recovery / restart replay / storage pressure | รอทำ | Safe compaction preconditions, Transaction integrity, Bounded maintenance, Failures preserve cursor |
| Dashboard ภาษาไทย | กำลังทำ | Same API, Token memory only, Safe text rendering, Actual empty/partial/degraded/error states |
| .NET SDK และ MCP adapter แยก | กำลังทำ | Named GET only, Bounded protocol/HTTP, Token secrecy, Backend scopes/errors, Protocol compatibility tests |
| Evidence export / schema description / OpenAPI / client examples | รอทำ | Explicit scopes, Bounded export, Sensitive data policy, Tests |
| Packaging / Full regression / Performance / Notices / Release | รอทำต่อเนื่อง | Windows build/tests/CI, ZIP relocation, No private files, Artifact digest |

## การแยกงาน

Root เป็นผู้รวมโค้ด ทดสอบ Solution ตรวจสิทธิ์/ข้อมูล และ Commit/Push
ส่วนงาน Client/MCP, Dashboard, Native network และ Semantic views แยกเจ้าของไฟล์ใหม่; ไม่แก้ไฟล์กลางพร้อมกัน
งานที่ต้องใช้สิทธิ์หรือ Provider ที่เครื่องไม่มีจะบันทึกเป็นข้อจำกัดในการยืนยันจริง แต่พัฒนา Adapter/Contract/Fixture ที่ถูกต้องต่อได้

## สถานะปัจจุบันและขั้นถัดไป

- preview.3 เผยแพร่แล้ว หลังตรวจ CI และ Draft digest
- กำลังทำ normalized process identity index เพื่อรองรับการค้น Tree/Activity ขนาดใหญ่
- กำลังพัฒนางานอิสระในตารางข้างต้นคู่ขนาน
- ขั้นต่อไป: รวมสิทธิ์/การหมุน Key และ Reader constraints ก่อนเปิด Client และแหล่งข้อมูลเพิ่ม

## จุดพักก่อนโควต้าหมด

Checkpoint source รวมแล้วและ Build/Smoke/API/Native auto/Dashboard10/SDK-MCP15 ผ่าน ดู HANDOFF_LATEST_TH.md และ API_HARDENING_NEXT.md ก่อนทำต่อ ไม่ถือว่าแผนทั้งหมดครบ ยังไม่มี Portable release ใหม่สำหรับโค้ดชุดนี้
