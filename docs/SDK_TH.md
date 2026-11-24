# SysWitness .NET SDK

SDK สำหรับ .NET 10 ใช้อ่าน Central API ผ่าน GET เท่านั้น ไม่มี dependency ภายนอกหรือ OpenAI API key และไม่เริ่ม Collector เอง

## Build และใช้

```powershell
dotnet build src/SysWitness.Sdk -c Release
```

อ้างอิง Project `src/SysWitness.Sdk/SysWitness.Sdk.csproj` แล้วสร้าง Client ด้วย Origin `http://127.0.0.1:8769` และ Token ของ Client ที่ได้รับสิทธิ์ไว้แล้ว:

```csharp
using SysWitness.Sdk;

using var client = new SysWitnessClient(
    new Uri("http://127.0.0.1:8769"),
    Environment.GetEnvironmentVariable("SYSWITNESS_MCP_TOKEN")!);
var page = await client.GetEventsAsync(new EventReadOptions(Limit: 25));
Console.WriteLine(page.GetRawText());
```

ผลลัพธ์เป็น JsonElement ที่เก็บข้อมูล provenance, quality, limitations, coverage และ nextCursor จาก Backend ไว้ ต้องใช้ Filter เดิมเมื่ออ่านหน้าถัดไป และไม่ตีความหน้าว่างว่าไม่มีเหตุการณ์เกิดขึ้น

## ขอบเขต

มี Named Methods สำหรับ Health, Events/ID, Coverage/Gaps, Storage Status/Retention/Projections, Process Events/Timeline/Activity/Tree, Network Connections/DNS/Inventory และ Activity Families ได้แก่ Files, Registry, Authentication, Services, Drivers, Tasks, Devices, Modules และ Process Access ไม่มี Method รับ URL, Route, SQL หรือคำสั่งทั่วไปจากผู้เรียก

Origin ยอมรับ HTTP บน 127.0.0.1 เท่านั้น ไม่รับ Path, Query, Fragment หรือ Userinfo ปิด Proxy, Redirect และ Cookie; ส่ง Token เฉพาะ Header X-SysWitness-Token ไม่ติดตั้งหรือยกระดับสิทธิ์ให้ Client เมื่อ Backend ตอบ 401/403/404/429/5xx จะโยน SysWitnessApiException พร้อม ErrorCode และ HttpStatus โดยไม่แนบ Response Body หรือ Secret

ค่าปริยาย: Timeout 15 วินาที, Response ไม่เกิน 1 MiB, JSON Depth 32. Constructor ปรับ Response ได้ 256 ไบต์ถึง 4 MiB และ Timeout 1–120 วินาที ทุก Named Method รับ CancellationToken. ตรวจจำนวนไบต์ระหว่างอ่าน Stream แม้ไม่มี Content-Length; JSON ผิดรูปแบบ, Property ซ้ำหรือ Content-Type ที่ไม่ใช่ JSON จะถูกปฏิเสธ

Filter หน้าอ่าน: Limit 1–500, Cursor ไม่เกิน 4096 หน่วย UTF-16, Channel/Provider/Computer ไม่เกิน 256 และไม่มี Control Character, Category ไม่เกิน 128 และใช้ได้เฉพาะ Events. Activity/Tree ต้องใช้ Computer ที่บันทึกไว้ตรงตัวและ GUID ไม่เป็นศูนย์; Tree Depth 0–8 และ Limit 1–256. ช่วงเวลาต้อง FromUtc < ToUtc. ขอบเขต Client นี้เข้มกว่าบาง Filter ใน Backend

SDK แทนที่ค่า Credential ที่ใช้เชื่อมต่อหาก Backend Echo ค่าเดียวกันกลับมา ด้วย [REDACTED] ไม่ใช่เครื่องมือตรวจลบ Secret ทุกชนิดจาก Telemetry. Process Command Line, Registry Details และข้อมูลต้นทางอื่นอาจมีข้อมูลอ่อนไหวตาม Scope ที่ Backend อนุญาต ผู้เรียกต้องไม่ส่งออกข้อมูลโดยไม่มีสิทธิ์

## ทดสอบ

```powershell
dotnet run --project tests/SysWitness.ClientTests -c Release
```

Test Harness ใช้ TCP Loopback จำลองและข้อมูลสังเคราะห์ ไม่เปิด API จริงหรืออ่าน Telemetry/Token ของผู้ใช้ ไม่มีการเขียนฐานข้อมูลทดสอบ
