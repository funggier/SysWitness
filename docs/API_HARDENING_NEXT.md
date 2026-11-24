# API hardening — ข้อออกแบบสำหรับงานถัดไป

สถานะ: ตรวจแบบแล้ว **ยังไม่ได้ Implementation** ณ checkpoint24พฤศจิกายน2026

## ข้อสำคัญ

- Registry เดิมยังโหลดครั้งเดียวตอนเริ่ม; scopes ใหม่ลงทะเบียนแล้วแต่ยังไม่รองรับ Channels/rotation
- กรอง Channel ต้องอยู่ใน SQL ก่อน Paging/ID lookup/Graph expansion ทุกจุด ไม่ใช่ตรวจเฉพาะ Query ที่ระบุ Channel
- Coverage primary/top-level/configured/enabled/degraded, gap default, retention history และ storage aggregates ต้องไม่รั่ว Channel ที่ห้าม
- Restricted Client ควรถูกปฏิเสธ global storage physical metrics หรือได้รับ DTO ที่ตัดข้อมูล global ออก ไม่ติดป้ายว่าสถิติ global เป็นข้อมูล scoped
- Typed projections ต้องอ่าน XML ภายในได้ตาม scope หมวด; raw scope ควบคุมการเปิดเผยเท่านั้น ไม่บล็อก trusted decoding

## Client registry

เพิ่ม immutable ClientIdentity Channels(null=unrestricted,[]=none), generation
รักษา tokenSha256 เดิม; optional active hashes หลายค่าเพื่อ overlap rotation; stable client ID
อนุญาต clients=[] เพื่อ revoke ทั้งหมด; reject duplicate JSON properties/IDs/hashes, unknown scopes/channels/properties และ plaintext tokens
Native+synthetic channel catalog ต้องรวม WindowsProcessSnapshot/Wmi/WindowsNetworkSnapshot และ Source ใหม่
อ่านและตรวจไฟล์ครบแล้วสลับ snapshot atomically; malformed/missing runtime file deny protected reads โดยไม่หยุด Collector
ห้ามใช้ last-known-good เก็บ Key ที่ revoke ไว้ไม่จำกัด; รีโหลดก่อน authorization ของ request ใหม่
Legacy environment token ต้องมี explicit local disable/tombstone หากต้อง revoke โดยไม่ restart; เดิมได้แค่ events/coverage/storage scopes
Requests ที่อนุญาตไปแล้วอาจจบภายใต้ query budget ต้องบันทึก semantics นี้

## Reader constraints

- EventQuery เพิ่ม AllowedChannels หรือ explicit ReadAccess overload โดยรักษา trusted collector/fixture overload
- Generic list, GetById, shared ReadEvidencePage, process/network/activity/tree, SystemActivity/NativeInventory ต้องใช้ข้อจำกัดเดียวกัน
- ID ที่ไม่มีและ ID ที่ห้ามคืน404เหมือนกัน
- Activity/Tree ต้องตรวจ Sysmon channel permission ก่อนขยาย Graph
- Parent references จาก Event ที่อนุญาตเก็บได้ แต่ห้ามไป enrich ด้วย Channel ที่ห้าม

## API guard และ Audit

- Fixed global unauthenticated rate bucket ก่อนอ่าน registry/hash; per-client quota ตาม stable ID, bounded partitions
- Global/per-client concurrency leases ไม่มีคิวไม่จำกัด; health lightweight
- Max returned/source rows, response bytes, input bounds; ไม่คืน partial invalid JSON เมื่อเกินขนาด
- SQLite CommandTimeout/Cancel ไม่ใช่หลักฐานว่า CPU/XML scan หยุดจริงใน Microsoft.Data.Sqlite10.0.12 ต้องตรวจ pinned implementation/measure actual stopped work
- Normalized GUID index ลด XML scanning แล้ว แต่ยังไม่มี hard query-duration guarantee หรือ benchmark
- Audit sink แยก local bounded rotating files: UTC/clientId/fixed operation/status/duration/raw flag/fixed failurecode เท่านั้น
- ห้าม URL/filter/cursor/EventID/headers/tokens/hashes/payload/exception-message ใน Audit
- หากต้อง guaranteed audit ให้ persist attempt ก่อนอ่าน; audit failure denyเฉพาะAPI ไม่หยุดCollector

## Raw evidence

เสนอ GET /api/v1/events/{id}/raw: ต้อง events.read AND evidence.raw.read และ local enable flag
Single ID only, channel predicate, strict UTF-8 byte cap, tighter rate, JSON DTO, source content untrusted
Legacy scopesไม่เพิ่ม และ summaries/typed pagesไม่เปิด XML

## Tests ที่ต้องมี

Rotation old→old+new→new, remove client/all, malformed/partial/missing file, duplicate fields/hashes, concurrent auth/reload
Omitted/forbidden channel, mixed-page cursor, forbidden ID, graph/source/metadata leaks
Snapshot/WMI/Sysmon/Security permissionsแยกกัน; same-key stable quota across rotation
Rawทุก gate, oversize, empty unknown; canary secretsในtoken/provider/command/DNS/cursor/XML/exceptionต้องไม่อยู่Audit
Flood/concurrency/cancel/large-history/retentionร่วมกัน; Collectorต้องเดินต่อและ leaseต้องคงอยู่จนDBหยุดจริง
