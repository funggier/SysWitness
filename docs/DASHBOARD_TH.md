# Dashboard ภาษาไทย

Dashboard นี้เป็น Client อ่านข้อมูลในเครื่องผ่าน API เดิมเท่านั้น ไม่ใช่ Collector และไม่เปลี่ยนวิธีเก็บหลักฐานเมื่อปิดหน้าเว็บหรือไม่มีโฟลเดอร์ Dashboard

## เปิดใช้งาน

เปิด URL หลักของ SysWitness บน Loopback เช่น http://127.0.0.1:8769/ โดยใช้ Port ที่ตั้งไว้ในเครื่อง หน้าเว็บต้องมาจาก Origin เดียวกับ API ไม่เปิดไฟล์ index.html ด้วย file:// และไม่มีช่องกรอก API URL ภายนอก

กรอก Client Token ในช่อง Password แล้วกด **เชื่อมต่อ** สิทธิ์ของ Token ถูกตรวจในแต่ละ API แยกกัน คำว่า “พร้อมส่งคำขอ” ไม่ได้รับรองว่ามีสิทธิ์ทุกหน้า เมื่อเสร็จให้กด **ตัดการเชื่อมต่อ** ซึ่งล้าง Token ยกเลิกคำขอที่ค้าง และล้างข้อมูลที่แสดง แต่ยังเก็บค่าตัวกรองใน Form ไว้

Token อยู่ในหน่วยความจำ JavaScript ของหน้านี้เท่านั้น ช่องกรอกถูกล้างหลังเชื่อมต่อ ไม่มีการบันทึกใน Local Storage, Cookie, URL หรือ Log ของ Client ไม่มี Telemetry ภายนอก, CDN, Font หรือ Library ที่ดาวน์โหลดจากเครือข่าย ทั้งนี้ Browser/Password Manager เป็นซอฟต์แวร์แยกจาก Client และอาจมีนโยบายของตนเอง

## ส่วนข้อมูล

| หน้า | ข้อมูล / ขอบเขต |
|---|---|
| ภาพรวม | /health, /monitoring/coverage และประวัติ /monitoring/gaps ตามคำขอ แสดง Source status ตาม Server รวม Process Collector, Fallback และ Dropped events |
| เหตุการณ์ | /events ด้วย Channel, Provider, Category, Source Event ID, UTC และ Limit; เปิด /events/{id} เพื่ออ่านสรุป ไม่มี Raw XML |
| โปรเซส | Feed Start/Exit ทุก Source, Sysmon Timeline, Activity และ Tree โดยแสดง Provenance กับ Payload ที่มีจริง |
| เครือข่ายและ DNS | Connections/WFP decisions และ DNS พร้อม Process Attribution และ Endpoint roles |
| กิจกรรมระบบ | ไฟล์, Registry, Authentication, Services, Drivers, Tasks, Devices, Modules, Process Access จาก /activity/{kind} เท่าที่ API/Source/สิทธิ์รองรับ |
| พื้นที่จัดเก็บ | /storage/status และประวัติ Retention 50 รายการล่าสุด ไม่มีคำสั่งลบหรือแก้นโยบาย |

มีเพียงคำขอ GET แบบ Same-origin ด้วย Header X-SysWitness-Token ไม่มีการอ่าน SQLite, File System หรือ Raw XML จาก Browser ไม่มีปุ่มสั่ง Process, Firewall, Windows Service, Task Scheduler, Setup หรือ Configuration

## การค้นหาและแบ่งหน้า

- กรอกเวลา ISO 8601 พร้อมเขตเวลา เช่น 2026-11-24T00:00:00Z; From รวมเวลาเริ่ม ส่วน To ไม่รวมเวลาสิ้นสุด เวลา Source แสดง UTC
- กด **ค้นหา / อ่านใหม่** เพื่อใช้ค่าตัวกรองปัจจุบันและเริ่มจากหน้าแรก อ่านได้ 1–500 รายการต่อหน้า ค่าเริ่มต้น Client คือ 50 ไม่ใช่การค้นหาข้อความอิสระ
- หน้าถัดไป/หน้าก่อนใช้ Snapshot ของตัวกรองที่ส่งสำเร็จในชุดคำค้นนั้น การแก้ Form ระหว่างแบ่งหน้าจะมีผลเมื่อกดค้นหาอีกครั้ง ไม่เปลี่ยน Cursor Query กลางทาง
- แต่ละหน้าแทนที่ข้อมูลเดิม ไม่สะสมข้อมูลทั้งหมดใน DOM; Cursor ใช้ต่อหน้าจาก Server ไม่ได้รับประกัน Snapshot isolation เมื่อมีการเพิ่มหรือลบหลักฐานพร้อมกัน
- ไม่มีข้อมูลหมายถึงไม่มีหลักฐานที่ตรงกันและยังเก็บอยู่ ไม่ได้รับรองว่าไม่เคยเกิดกิจกรรม

## Process Identity และ Tree

Activity ต้องมี processes.read และ network.read ใน Token เดียวกัน ใช้ GUID แบบมีขีดที่ไม่เป็นศูนย์กับ Computer ตรงตาม Source รวมตัวพิมพ์ใหญ่เล็ก ไม่เดาชื่อสั้น/FQDN และไม่รวม PID-only Source โดยคาดเดา Timeline เดิมใช้ Subject Sysmon GUID โดยไม่มี Computer Filter

Tree ใช้เพียง GUID, Computer, Depth (0–8), Node limit (1–256) ตัวกรอง Feed ที่กรอกอยู่ไม่ถูกส่งไป Tree; งบ Source ของ Server คือ 1,000 Creation events ผลแสดง Nodes, Relationships และ Unresolved แยกตาราง ไม่มี Recursive expansion ใน Client จึงไม่วนไม่สิ้นสุดเมื่อมี Cycle/Self-reference เก็บ Reference-only และ Conflicting-parent ตามที่ Server รายงาน มี Provenance ในแต่ละ Child creation evidence

Flags Depth/Node/Evidence limit, Cycle, Source events used และ Method/Quality แสดงตาม Server หากข้อมูลเกินขอบเขต DOM ของ Client จะแสดง partial และตัดที่ 256 Nodes/1,000 Relationships/1,000 Unresolved ขอบเขตนี้ไม่ใช่หลักฐานว่าครบทั้งหมด แม้ Flags เป็น false

WMI คือ Observation; Snapshot appearance/disappearance เป็น Derived และพลาด Process อายุสั้นระหว่าง Sample ได้ Sysmon reported parent หรือการจับ GUID+Computer ไม่ใช่การยืนยันเชิงเหตุและผลอย่างอิสระ DNS ไม่ได้รับรองความสำเร็จของ Connection

## สถานะและการอัปเดต

- Loading, Empty, Error, 400, 401, 403, 404, 429, degraded และ partial แสดงชัดเจน ไม่มีการเติมค่าข้อมูลที่หายไป ช่องว่างใช้ —
- 401 ยกเลิกการเชื่อมต่อและล้างข้อมูล; 403 แสดงว่าสิทธิ์ไม่พอ; 404 แสดงว่าไม่พบข้อมูลหรือ API ยังไม่พร้อม; Error Code จาก Server แสดงเป็นข้อความ
- ภาพรวมเท่านั้นที่อัปเดตอัตโนมัติ ทุก 15 วินาทีหลังคำขอก่อนหน้าสิ้นสุด เมื่อหน้านี้ Active และ Browser มองเห็นอยู่ มี Checkbox หยุดได้ ไม่มี Overlapping overview requests
- 403/404 ของ Coverage หยุด Poll อัตโนมัติจนมีคำขอด้วยผู้ใช้/การเชื่อมต่อใหม่ เพื่อลดคำขอที่ไม่มีสิทธิ์; 429 ใช้ Retry-After เป็น Backoff 30–300 วินาที สำเร็จแล้วกลับ 15 วินาที
- หน้าอื่นอ่านเมื่อผู้ใช้ร้องขอ พื้นที่จัดเก็บอ่านหนึ่งครั้งเมื่อเปิดหน้าและกดอ่านใหม่ได้ ไม่มี SSE หรือ Background polling ของแต่ละ Feed
- แต่ละคำขอมี Client timeout 20 วินาที; จำกัด UTC range เพิ่มเมื่อข้อมูลมาก Timeout นี้ไม่ใช่งบเวลาของ SQL ฝั่ง Server
- ข้อมูลที่เก็บในหน้าไม่คงอยู่เมื่อ Reload, Disconnect หรือออกจากหน้า Browser ค่า Form ที่ยังอยู่ไม่ใช่ข้อมูลใน Persistent Storage

## ความปลอดภัยของการแสดงผล

ข้อความ Provider, Command Line, Paths และ Payload ใช้ DOM textContent เท่านั้น ไม่มี innerHTML หรือการประเมิน Source string เป็น Script/URL เปิดรายละเอียดหลักฐานแล้วแสดง JSON เป็น Text ไม่มี Clickable URL ที่สร้างจาก Source

Static public assets มีเพียง index.html, styles.css, model.js และ app.js การ Serving/Allowlist/CSP อยู่ใน API integration โฟลเดอร์ Dashboard เป็นตัวเลือก การไม่มีไฟล์ต้องไม่ทำให้ Collector หยุด

## ตรวจสอบ

ไม่ต้องติดตั้ง Node package หรือเพิ่ม Build step:

    node --check dashboard/app.js
    node --test tests/DashboardTests/model.test.cjs tests/DashboardTests/app.test.cjs

10 Automated checks ผ่าน ณ รอบพัฒนา: Immutable cursor filters, GUID validation, cyclic/reference/conflicting tree bounds, text-only sink, HTTP states, no initial requests, Token header-only, 403 poll stop, preserving unsubmitted filters across Cursor, disconnect abort/late-response guard, 401 clear, 429 Retry-After, active-overview-only polling, Tree query isolation และ unavailable Activity state

Harness เป็น DOM/Fetch จำลองสำหรับ State transition ไม่ทดแทน Browser rendering หรือ API integration ในเครื่องจริง ต้องตรวจ Browser หลังรวม Static serving และตรวจ Source จริงตาม Coverage/สิทธิ์ที่มี ไม่มีการอ้างว่าทุก Source หรือทุกประเภท Activity เปิดใช้งานแล้ว
