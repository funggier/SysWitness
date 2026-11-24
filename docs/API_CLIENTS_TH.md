# SysWitness — การเชื่อมต่อโปรแกรมผ่าน Read-only API

> สถานะ: **มีระบบ Token แยก Client และสิทธิ์อ่านรายหมวดแล้ว** ใน Prototype M2. MCP Adapter ยังไม่ได้สร้าง และ API **ไม่มี** คำสั่งควบคุม Windows

## รูปแบบการทำงาน

โปรแกรมทุกตัวเป็นผู้ใช้งาน **Central Read-only API**: Dashboard, MCP, AI Local, SDK หรือโปรแกรมป้องกันภายนอกที่อ่านข้อมูลจาก SysWitness เพื่อนำไปวิเคราะห์ด้วยตนเอง ไม่สามารถสั่งให้ SysWitness บล็อก IP, หยุด Process หรือปรับ Windows ผ่าน API ได้

API รับ `X-SysWitness-Token` ผ่าน HTTP บน `127.0.0.1` เท่านั้นโดยค่าเริ่มต้น. Client มี ID และชุดสิทธิ์อ่านของตัวเอง การยืนยันตัวตนผิดตอบ HTTP 401, Token ถูกต้องแต่สิทธิ์ไม่พอตอบ HTTP 403, Endpoint ที่ไม่มีหรือไม่ใช่คำสั่งอ่านตอบ 404 (ยกเว้น Health แบบ GET ที่เป็นข้อมูลทั่วไป)

## สิทธิ์ที่รองรับ

| Scope | Endpoint ที่สามารถอ่านได้ |
|---|---|
| `events.read` | `GET /api/v1/events`, `GET /api/v1/events/{eventId}` |
| `coverage.read` | `GET /api/v1/monitoring/coverage`, `GET /api/v1/monitoring/gaps` |
| `storage.read` | `GET /api/v1/storage/status`, `GET /api/v1/storage/retention` |
| `network.read` | `GET /api/v1/network/connections`, `GET /api/v1/network/dns` |
| `processes.read` | `GET /api/v1/processes/events`, `GET /api/v1/processes/{processGuid}/timeline` |

**Timeline รวม Process/DNS/Network:** `GET /api/v1/processes/{processGuid}/activity?computer=<recorded-computer>` ต้องมี **ทั้ง** `processes.read` และ `network.read` ใน Client เดียวกัน ขาดอย่างใดอย่างหนึ่งตอบ 403; Token เดิมไม่ได้สิทธิ์เพิ่ม ไม่มี Scope ใหม่ ดู [คู่มือ Activity](PROCESS_ACTIVITY_TH.md).

ไม่มี Scope `*.write`, `*.execute`, `firewall.*`, `protection.*` หรือสิทธิ์แก้ไขการตั้งค่า. เพิ่มสิทธิ์ใหม่ในอนาคตต้องเพิ่มทั้ง API Contract, การตรวจสิทธิ์, และ Test อย่างชัดเจน

## ตั้งค่า Client ใหม่ (PowerShell)

ตัวอย่างสำหรับใช้ระหว่างการพัฒนาในโฟลเดอร์ Root ของ Repository:

```powershell
# 1. สร้าง Secret ที่คาดเดาได้ยากและเก็บไว้เฉพาะในโปรแกรม Client
$randomBytes = New-Object byte[] 32
$rng = [Security.Cryptography.RandomNumberGenerator]::Create()
try { $rng.GetBytes($randomBytes) } finally { $rng.Dispose() }
$clientToken = -join ($randomBytes | ForEach-Object { $_.ToString('x2') })

# 2. สร้าง SHA-256 Hash สำหรับฝั่ง SysWitness (ไม่ใช่ Token ต้นฉบับ)
$sha = [Security.Cryptography.SHA256]::Create()
try { $bytes = $sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($clientToken)) }
finally { $sha.Dispose() }
$tokenHash = -join ($bytes | ForEach-Object { $_.ToString('x2') })

# 3. บันทึกเฉพาะ Hash ลงไฟล์ Local ที่ Git ignore แล้ว
$localDir = Join-Path (Get-Location) 'data'
New-Item -ItemType Directory -Path $localDir -Force | Out-Null
$clientConfig = Join-Path $localDir 'client-keys.json'
@{
    clients = @(
        @{
            id = 'mcp-reader'
            tokenSha256 = $tokenHash
            scopes = @('events.read', 'coverage.read')
        }
    )
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $clientConfig -Encoding UTF8

# 4. ตั้งค่าไฟล์ Client ก่อนเริ่ม SysWitness
$env:SYSWITNESS_CLIENT_KEYS_FILE = $clientConfig
# จากนั้นสั่ง dotnet run --project src/SysWitness.Api
```

**สำคัญ:** ตัวอย่างนี้ไม่ได้เขียน `$clientToken` ลงไฟล์ และคุณต้องส่งค่า Secret นี้ให้โปรแกรม Client ด้วยวิธีที่ปลอดภัย (ไม่ใส่ Token ลง GitHub, Source Code หรือคำสั่งที่ถูกบันทึกประวัติ). ตัวอย่างแสดงเพียงรูปแบบการตั้งค่า ไม่ได้สร้าง Secret ให้โดยอัตโนมัติใน Repository

### รูปแบบ JSON ภายใน

```json
{
  "clients": [
    {
      "id": "mcp-reader",
      "tokenSha256": "<SHA-256-HEX-64-CHARACTERS>",
      "scopes": ["events.read", "coverage.read"]
    }
  ]
}
```

รูปแบบรองรับสูงสุด 32 Client ต่อไฟล์ มี ID ไม่ซ้ำกัน และ Hash ไม่ซ้ำกัน; Client ID จำกัดอักขระตัวอักษร ตัวเลข จุด ขีดกลาง และขีดล่าง. Hash ไม่ใช่ Secret ต้นฉบับ แต่ต้องจำกัดสิทธิ์อ่านไฟล์ Local อยู่ดี. ตั้งค่า `SYSWITNESS_CLIENT_KEYS_FILE` เป็น Full Path ที่มีอยู่จริง และ **Restart โปรแกรม** เพื่อโหลดการเปลี่ยนแปลง หากต้องการเพิกถอนสิทธิ์ Client ให้ลบรายการแล้ว Restart

### การใช้ร่วมกับ Token เดิม

`SYSWITNESS_API_TOKEN` ที่มีอยู่เดิมยังใช้ได้เพื่อความเข้ากันได้ โดยได้รับสิทธิ์เดิม `events.read`, `coverage.read` และ `storage.read`. สำหรับรายละเอียดโปรเซสให้เพิ่ม `processes.read` ใน `scopes` ของ Client ที่ต้องใช้ แล้วเปิดโปรแกรมใหม่ ข้อมูลที่อ่านได้รวมถึง command line. สำหรับข้อมูลการเชื่อมต่อและ DNS ให้เพิ่ม `network.read` เฉพาะ Client ที่ต้องการอ่านข้อมูลเครือข่าย. ถ้าตั้งค่าไฟล์ Client เพิ่ม จะใช้ทั้งสองรูปแบบร่วมกันได้; หากตั้งเฉพาะ `SYSWITNESS_CLIENT_KEYS_FILE` และไม่ตั้ง Token เดิมก็สามารถใช้ Client-scoped API ได้

เพื่อให้ Client มีสิทธิ์เฉพาะหน้าที่ แนะนำให้หลีกเลี่ยงการนำ Token เดิมไปใส่ในแอปเสริม ใช้ Token เฉพาะแต่ละโปรแกรมแทน หากต้องการยกเลิก Token เดิมให้ลบตัวแปร `SYSWITNESS_API_TOKEN` ออกจากสภาพแวดล้อมก่อนเริ่มโปรแกรมใหม่

## ข้อจำกัดด้านความปลอดภัยที่ยังต้องพัฒนา

- Scope จำกัดตาม **หมวด API** เท่านั้น ยังไม่สามารถจำกัดบาง Provider, Channel, Process หรือช่วงเวลาเป็นราย Client
- ยังไม่มีสิทธิ์แยก `evidence.read` เพราะ API ยังไม่เปิด Raw Event XML ให้ Client
- ยังไม่มี Audit Log ของการอ่านข้อมูลแบบละเอียด, Rate Limit และ Rotation แบบไม่ต้อง Restart
- ใช้ HTTP Localhost ไม่ใช่ Secure Remote API; ไม่ควรเปิดออก LAN/Internet โดยไม่มีการออกแบบ Authentication/Transport ใหม่
- MCP Server ในอนาคตเป็น Adapter อ่านข้อมูลเท่านั้น และต้องผ่านสิทธิ์ของ Client ตัวเอง

## วิธีทดสอบ

รัน `dotnet run --project tests/SysWitness.SmokeTests` เพื่อทดสอบ Hash/Scope/การปฏิเสธ Config ที่ผิด แล้วรัน `powershell -NoProfile -ExecutionPolicy Bypass -File tests/Run-ApiSmoke.ps1` เพื่อทดสอบการอนุญาตอ่านจริง: HTTP 200/401/403/404 โดยใช้ Client Token แบบทดสอบและฐานข้อมูลชั่วคราว ไม่ใช้ข้อมูล Secret จริงใน Repo
