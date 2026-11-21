# SysWitness Portable (Windows x64)

**แยกจากระบบป้องกัน/ควบคุมโดยสมบูรณ์:** โปรแกรมนี้ทำหน้าที่เก็บข้อมูลและเปิด Read-only API เท่านั้น

## วิธีใช้งาน
1. แตกไฟล์ ZIP ให้เป็นโฟลเดอร์ที่คุณเขียนไฟล์ได้ เช่น `T:\Apps\SysWitness` หรือ `D:\Portable\SysWitness` (ไม่จำเป็นต้องอยู่ C:)
2. เปิด `Start-SysWitness.cmd` โดยไม่ต้อง Run as Administrator (หาก Windows ป้องกันบาง Channel การอ่านจะรายงาน `degraded`)
3. ครั้งแรกจะสร้าง `config/settings.json`, `config/client-keys.json`, `config/owner-token.txt` และโฟลเดอร์ `data/` ภายในโปรแกรม
4. เปิด `http://127.0.0.1:8769/api/v1/health` เพื่อดูสถานะพื้นฐาน และใช้ `X-SysWitness-Token` จาก `config/owner-token.txt` สำหรับอ่านข้อมูล API
5. หยุดโปรแกรมด้วย Ctrl+C ก่อนย้ายโฟลเดอร์หรือสำรองฐานข้อมูล

## ย้ายไดรฟ์/เปลี่ยนชื่อโฟลเดอร์
ระบบเก็บเหตุการณ์โปรเซสในโหมด `auto` เป็นค่าเริ่มต้น ลองรับเหตุการณ์ WMI ด้วยสิทธิ์ของบัญชีที่เปิดโปรแกรม และสลับไปสำรวจรายการโปรเซสทุกประมาณ 2 วินาทีเมื่อรับไม่ได้ ตั้ง `processMode` ใน `config/settings.json` เป็น `auto`, `snapshot`, `wmi` หรือ `off` ได้ การติดตั้งเดิมที่ไม่มีค่านี้ใช้ `auto` ตามค่าเริ่มต้นของโปรแกรม

หากต้องการอ่านเหตุการณ์จาก Windows Event Log เพิ่ม ให้เพิ่ม `Security` หรือ `Microsoft-Windows-Sysmon/Operational` ใน `eventChannels` ของ `config/settings.json` ตามช่องที่เครื่องและบัญชีเข้าถึงได้ แล้วเปิดโปรแกรมใหม่ สิทธิ์ `processes.read` มีให้กับ Owner Token ที่สร้างใหม่ หากใช้ไฟล์ `config/client-keys.json` จากรุ่นก่อน ให้เพิ่มสิทธิ์นี้ในรายการ `scopes` ของ Client ที่ต้องการใช้

หลังจากปิดโปรแกรม สามารถย้าย **โฟลเดอร์ทั้งหมด** ได้ทันทีโดยไม่มี Path ถาวรใน Config เริ่มต้น เช่น `T:\Apps\SysWitness` -> `D:\Tools\SysWitness`. เปิด `Start-SysWitness.cmd` ในตำแหน่งใหม่และใช้ Token, SQLite, Bookmarks เดิมได้

## ที่เก็บไฟล์
```text
SysWitness/
  Start-SysWitness.cmd
  Start-SysWitness.ps1
  SysWitness.Api.exe
  settings.default.json
  config/                    # สร้างอัตโนมัติครั้งแรก - sensitive!
    settings.json
    client-keys.json
    owner-token.txt          # SECRET - ห้ามแชร์
  data/                      # สร้างอัตโนมัติ - PRIVATE HOST EVENTS!
    events.sqlite
  README-PORTABLE-TH.md
```

`config/settings.json` กำหนด `port`, `eventChannels`, `dataDirectory` (ต้องเป็น Relative Path ภายในโฟลเดอร์), `storageSoftLimitMB`, `minFreeDiskMB`, `retentionDays` (null = ไม่ลบ Event). เปลี่ยนการตั้งค่าแล้วต้อง Restart

**ความปลอดภัย:** `owner-token.txt` เก็บ Secret แบบข้อความอ่านได้เพื่อให้ใช้งานแบบ Portable และย้ายทั้งโฟลเดอร์ได้ ควรใช้โฟลเดอร์ส่วนตัวที่จำกัดสิทธิ์อ่าน และอย่าแชร์ทั้ง `config/` หรือ `data/` ไปกับ ZIP สำหรับเผยแพร่ โปรแกรมพยายามจำกัด ACL บน NTFS ในครั้งแรก แต่บน exFAT/USB หรือโฟลเดอร์แชร์อาจจำกัดสิทธิ์ไม่ได้ ควรใช้เฉพาะสื่อที่เชื่อถือได้

**ไม่จำเป็นต้องติดตั้ง .NET Runtime แยก** เพราะแพ็กเกจใช้ Self-contained win-x64 และมีเอกสารสิทธิ์ของส่วนประกอบที่แจกจ่ายมาด้วย แต่ยังใช้ Windows API ที่มีอยู่บนเครื่อง และไม่ได้ติดตั้ง Sysmon/Windows Service/Driver หรือเปลี่ยน Audit Policy เอง

ยังเป็น Prototype: บาง Channel ต้องได้รับสิทธิ์หรือมี Sysmon ติดตั้งไว้แล้ว การอ่าน Log ไม่รับประกันว่าไม่มี Event สูญหาย และยังไม่มี Dashboard หรือ MCP Server ที่พร้อมใช้งาน
