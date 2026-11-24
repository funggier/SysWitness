# Process Activity Timeline — การเชื่อมหลักฐานรุ่นแรก

สถานะ: เพิ่มเมื่อ 24 พฤศจิกายน 2026 เป็นการจัดกลุ่มหลักฐานที่เก็บไว้แบบ Derived ไม่ใช่การตรวจจับการโจมตีหรือระบบควบคุม Windows

## สิ่งที่อ่านได้

`GET /api/v1/processes/{processGuid}/activity?computer={recordedComputer}`

รวม Sysmon Event 1 (เริ่มโปรเซส), 5 (จบโปรเซส), 22 (DNS) และ 3 (การเชื่อมต่อ) ใน Timeline เดียว โดยใช้ **Subject ProcessGuid + Computer ที่ตรงกันตามค่าที่บันทึกไว้** เป็นขอบเขต ตัวอย่างต่อไปนี้เป็นข้อมูลจำลอง:

```http
GET /api/v1/processes/aaaaaaaa-2222-4333-8444-555555555555/activity?computer=activity-fixture-host&limit=100
X-SysWitness-Token: <private-client-token>
```

ต้องมีทั้ง `processes.read` และ `network.read` ใน Client เดียวกัน หากขาดอย่างใดอย่างหนึ่งตอบ 403 แม้ผลลัพธ์จะว่าง ไม่มี Scope ใหม่และไม่ขยายสิทธิ์ของ Token เดิม

ชื่อเครื่องอ่านได้จาก `source.computer` ของ Process/Network/DNS API ใช้ค่าตรงตามต้นทางและ URL-encode เมื่อส่งใน Query ระบบไม่เดาชื่อเครื่อง ไม่รวมชื่อสั้นกับ FQDN และไม่รวมตัวพิมพ์ใหญ่เล็กต่างกันโดยอัตโนมัติ ถ้าไม่มีชื่อเครื่องที่เชื่อถือได้ จะยังไม่รวมเหตุการณ์นั้นใน Timeline นี้

## รูปแบบข้อมูล

- ระดับ Page: `processGuid`, `computer`, `correlationMethod=sysmon_subject_guid_and_computer_v1`, `quality=derived`, `items`, `limit`, `returned`, `nextCursor` และ `limitations`
- แต่ละ Item: `eventId`, `occurredAtUtc`, `observedAtUtc`, `kind` และ Payload ชนิดเดียวที่มีข้อมูล: `lifecycle`, `connection` หรือ `dnsQuery`
- `kind`: `process.start`, `process.exit`, `dns.query`, `network.connection`
- Payload ใช้ Contract ของ Process/Network/DNS เดิม จึงมีแหล่งที่มา Provider, Channel, Source Event/Record ID และ Computer ตามเดิม
- `eventId` อ้างถึงหลักฐานที่เก็บจริง และใช้กับ `GET /api/v1/events/{eventId}` เมื่อ Client มี `events.read` เพิ่มเติม API นี้ไม่คืน Raw XML และไม่ให้สิทธิ์อ่าน Event API โดยอัตโนมัติ

รองรับ `limit` (1–500, ค่าเริ่มต้น 100), `cursor`, `channel`, `provider`, `sourceEventId`, `fromUtc` และ `toUtc` เช่นเดียวกับ Typed Feed เดิม ไม่รับ `category` หรือ `processGuid` ซ้ำใน Query ขอบเขตเริ่มเวลารวม From และไม่รวม To

ผลลัพธ์เรียงใหม่ไปเก่าตาม `occurredAtUtc` แล้ว `eventId` ใช้ `nextCursor` พร้อม GUID, Computer และ Filter เดิมเพื่ออ่านหน้าถัดไป การกรอง GUID/Computer เกิดก่อนแบ่งหน้า Unknown GUID/Computer ตอบ 200 พร้อมรายการว่าง แต่ GUID ศูนย์, Computer ว่าง/ซ้ำ/ยาวเกิน 256 ตัวอักษร/มี Control Character และ Query ผิดตอบ 400 ไม่มี Snapshot Isolation ระหว่างหน้าเมื่อมีการเขียนหรือลบข้อมูลพร้อมกัน

## สิ่งที่ผลลัพธ์ยืนยันและยังยืนยันไม่ได้

ยืนยันได้เพียงว่าหลักฐาน Sysmon ที่เก็บไว้รายงาน Subject GUID และ Computer ตรงกัน การจัดกลุ่มจึงเป็น **Derived จากค่าต้นทาง** ไม่ใช่การพิสูจน์ตัวโปรเซสอย่างอิสระ ไม่มีคะแนนความเชื่อมั่นเป็นเปอร์เซ็นต์ที่ไม่ได้วัด

- ไม่ใช้ PID, ชื่อไฟล์, Command Line, ParentProcessGuid หรือข้อความใน DNS Results แทน Subject GUID
- ไม่จับ Security, WMI หรือ Snapshot ที่มีเพียง PID มารวมโดยคาดเดา
- DNS และ Connection ในโปรเซสเดียวกันไม่ได้พิสูจน์ว่า DNS นั้นเป็นสาเหตุของ Connection นั้น หรือปลายทางเชื่อมต่อสำเร็จ
- DNS ล้มเหลวยังคงอยู่ตามสถานะต้นทาง และไม่ได้ถูกเปลี่ยนเป็นผลสำเร็จจากการพบ Connection ใกล้กัน
- อาจไม่มี Event เริ่ม/จบโปรเซส เพราะช่วง Backfill, Provider, การเก็บตกหล่นหรือ Retention ไม่ควรตีความรายการว่างว่าไม่เคยมีกิจกรรม
- เวลาเกิดจาก Source อาจคลาดเคลื่อน การเรียงเวลาไม่พิสูจน์ลำดับการทำงานจริง
- ไม่มีการแก้ Raw Evidence, Collector Bookmark, Audit Policy หรือ Windows Configuration

## โครงสร้างและการตรวจสอบ

Contract อยู่ใน `SysWitness.Contracts`, Projection ใน `SysWitness.Core`, Query ใน `SysWitness.Storage` และ Endpoint ใน `SysWitness.Api` อ่านจากหลักฐานเดิมโดยไม่ต้องสร้างฐานข้อมูลใหม่หรืออ่าน Event Log ย้อนซ้ำ

ชุด `ProcessActivitySmoke` ตรวจการรวมประเภท, อ้างกลับ Event, PID reuse, Host boundary, GUID ที่อยู่ใน Parent/ข้อความ, Provider ผิด, GUID กำกวม/ไม่มี, XML เสีย, SQL parameter binding, เวลาเท่ากันและ Restart/Reopen

`tests/Run-ApiSmoke.ps1` ตรวจ HTTP 200/400/401/403/404, สิทธิ์สองหมวดพร้อมกัน, Pagination, Host boundary และการไม่คืน Raw XML ด้วย Fixture ในฐานข้อมูลชั่วคราว พร้อม Regression ของ System/Application Event Log จริง

การทดสอบ Correlation กับ Sysmon เป็น Fixture จนกว่าจะมี Sysmon Channel จริงที่อ่านได้ การทดสอบบน Windows ไม่ได้แปลว่าได้ยืนยัน End-to-End กับ Sysmon จริงแล้ว

## งานต่อที่เหลือ

- ยืนยันกับ Sysmon จริงที่มี Process/DNS/Network ครบตามสิทธิ์
- เพิ่ม Process Tree และความสัมพันธ์ Parent/Child ที่มีหลักฐานรองรับ
- ออกแบบการเชื่อมกับแหล่งอื่นเมื่อมี Stable Identity ที่ตรวจได้ โดยแยก Candidate/Inferred ออกจาก Exact Match
- เพิ่ม Query-cost budgets และ Index ที่เหมาะสมจากผลวัดข้อมูลจริง ปัจจุบันยัง Decode GUID จาก XML ตอน Query เช่นเดียวกับ Timeline เดิม
