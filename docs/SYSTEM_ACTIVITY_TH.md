# System activity จากหลักฐานที่เก็บไว้

API ชุดนี้แสดงข้อมูลจาก Windows Event Log ที่ SysWitness เก็บไว้แล้ว โดยอ้างกลับไปยัง EventId เดิม ไม่ใช่การเฝ้าดูทุกการกระทำของ Windows และไม่ติดตั้ง Sysmon, เปลี่ยน audit policy/SACL, เปิด channel, เริ่ม ETW session หรือควบคุม service/task/device

## เส้นทางและตัวกรอง

GET /api/v1/activity/{kind} รองรับ kind ตามตารางด้านล่าง ใช้ limit (1–500), cursor, channel, provider, sourceEventId, fromUtc และ toUtc เช่นเดียวกับ event API โดยไม่รับ category เวลาเริ่มรวมขอบเขตและเวลาสิ้นสุดไม่รวมขอบเขต ต้องระบุเขตเวลา

ผลลัพธ์มี kind, items, limit, returned, nextCursor และ limitations แต่ละ item มี EventId, เวลาที่ source รายงาน, เวลาที่โปรแกรมรับข้อมูล, source channel/provider/event ID/record ID/computer, payloadStatus และ payload แบบมีชนิดเพียงหนึ่งชุด ผู้เรียกใช้สิทธิ์และ channel ที่ registry ส่วนกลางอนุญาตเท่านั้น

Actor เป็น process ที่ payload ระบุโดยตรง ส่วน Subject เป็นบัญชีตามบทบาทของ source ไม่ใช่ process ที่เขียน log ลง Windows ช่อง System/Execution/ProcessID จะไม่ถูกนำมาใช้เป็น Actor ถ้าข้อมูลไม่มี/ผิดรูปแบบ/ซ้ำ/ยาวเกินขอบเขต จะคืน null ไม่เดาค่า

| kind | Source ที่รองรับ | ความหมาย |
|---|---|---|
| files | Sysmon 2, 9, 11, 15, 23, 26–29 | เปลี่ยน creation time, raw device read, สร้าง/เขียนทับไฟล์, สร้าง stream, ลบ, block/detect executable หรือ shredding ตาม source |
| files | Security 4656, 4663, 4670 เมื่อ ObjectType=File | ขอใช้สิทธิ์, ใช้สิทธิ์, เปลี่ยน permissions ตามลำดับ |
| registry | Sysmon 12–14; Security 4657 | สร้าง/ลบ registry object, set value, rename; Security value modification |
| registry | Security 4656, 4663, 4670 เมื่อ ObjectType=Key | ขอใช้สิทธิ์, ใช้สิทธิ์, เปลี่ยน permissions ของ key |
| authentication | Security 4624, 4625, 4634, 4647, 4648, 4672, 4776 | logon สำเร็จ/ล้มเหลว, session จบ, เริ่ม logoff, explicit credentials attempt, privileges assigned, NTLM validation |
| services | Security 4697; System / Service Control Manager 7045 | ติดตั้ง service รวม driver service |
| drivers | Sysmon 6; Security 4697 ที่ ServiceType เป็น 1, 2 หรือ 8 | driver load; ติดตั้ง driver service |
| tasks | Security 4698–4702 | สร้าง/ลบ/enable/disable/update task |
| devices | Security 6416, 6419–6424 | recognize device, ขอ disable/enable, disabled/enabled, installation forbidden/allowed ตาม source |
| modules | Sysmon 7 | module ถูกโหลดใน process |
| process-access | Sysmon 8, 10 | สร้าง remote thread; เปิด process อื่น |

Sysmon หมายถึง provider Microsoft-Windows-Sysmon และ channel Microsoft-Windows-Sysmon/Operational เท่านั้น Security หมายถึง provider Microsoft-Windows-Security-Auditing ใน channel Security เท่านั้น ID เดียวกันจาก provider/channel อื่นไม่ถือเป็นหลักฐานชนิดเดียวกัน SQL ตรวจ source และเงื่อนไข ObjectType/ServiceType ก่อนแบ่งหน้า ไม่ได้แบ่งหน้าข้อมูลทุกชนิดแล้วค่อยกรองทิ้ง

## ฟิลด์และการตีความ

- File เก็บ path/devicePath, creation timestamps, accessMask/accessList และ handle ตาม source โดยไม่แปลง audit สิทธิ์เป็นข้อสรุปว่าอ่านเนื้อหาไฟล์จริง
- Registry objectPath เก็บ TargetObject หรือ ObjectName ตาม source โดยไม่แยกชื่อ value จาก Sysmon path ที่อาจกำกวม ส่วน valueName ใช้ ObjectValueName ที่ระบุชัดเจน มี old/new value หรือ Details เฉพาะเมื่อ source ให้มา ข้อมูลนี้อาจมีรายละเอียดของ configuration
- Authentication แยก Subject ที่รายงาน/เรียกใช้งาน ออกจาก Target ที่เข้าสู่ระบบ 4624/4625/4648 ใช้ payload ProcessId/ProcessName เท่านั้น 4634 และ 4647 ใช้ XML TargetUser* แม้หน้าจอ Event Viewer จะแสดงหัวข้อ Subject
- Service 4697 ใช้ ServiceFileName/ServiceStartType/ServiceAccount; SCM 7045 ใช้ ImagePath/StartType/AccountName ไม่มี installer PID ใน payload จึงไม่สร้าง Actor
- Driver load 6 ใช้ ImageLoaded และ hashes/signature ตาม source ไม่ระบุว่า process ใดทำให้ driver ถูกโหลด เหตุการณ์ติดตั้ง driver service อาจปรากฏทั้ง services และ drivers โดยมี EventId เดียวกัน
- Task ใช้ TaskName และ client process/locality fields เฉพาะที่มีอยู่ DefinitionAvailable บอกเพียงว่ามี TaskContent หรือ TaskContentNew ใน payload เท่านั้น ไม่ยืนยันว่า definition ถูกต้อง XML task definition ไม่ถูกส่งผ่าน API
- Module แยก ImageLoaded ของ module ออกจาก Image ของ process
- Process access ใช้ SourceProcessId/Guid/Image และ TargetProcessId/Guid/Image คนละบทบาท รองรับการสะกด GUID ของ source ที่ต่างกัน แต่ถ้ามีสองรูปสะกดใน payload เดียวกันจะไม่เลือกเอง
- ฟิลด์ที่ไม่อยู่ใน DTO, raw event XML, arbitrary properties, TaskContent, file stream Contents และ security descriptors ไม่ถูกส่งออกใน projection นี้ ข้อมูลดิบยังเป็นหลักฐานเดิมใน storage

## ข้อจำกัดที่ต้องแสดงตามจริง

1. ค่าเริ่มต้นอ่าน System/Application จึงอาจไม่มีข้อมูลของหลายชนิดข้างต้น Security และ Sysmon ต้องมีอยู่และอ่านได้ตาม configuration เดิม การไม่มีรายการไม่แปลว่าไม่มีการกระทำนั้น
2. Security object auditing ต้องพึ่ง audit policy และ SACL เดิม 4656 หมายถึงร้องขอสิทธิ์ ไม่ยืนยันว่า operation ถูกทำ 4663 หมายถึงใช้สิทธิ์และไม่มี Failure form แต่ AccessMask ขึ้นกับ object type เช่น 0x1 ของ File อาจหมายถึง ReadData หรือ ListDirectory ส่วน DELETE right อาจพบเมื่อ rename
3. 4660 ยืนยัน deletion แต่ไม่มี object name/type เพียงพอที่จะจำแนก file/registry โดยลำพัง จึงไม่รวมใน typed family นี้โดยไม่ทำ handle correlation
4. Sysmon 11 ไม่ใช่ทุก write และไม่ใช่ file read Sysmon 9 เป็น raw drive read เท่านั้น 13 มีข้อจำกัดของ source ในการบันทึกค่าที่เขียน โดยเอกสารระบุ DWORD/QWORD 7 ถูกปิดโดยค่าเริ่มต้นและต้องอาศัย configuration ที่มีอยู่
5. Sysmon 10 ไม่ใช่หลักฐานว่ามี memory read/write เกิดขึ้น ส่วน StartModule/StartFunction ใน 8 เป็นค่าที่ Sysmon อนุมานและอาจว่างได้
6. 4648 เป็นการพยายามใช้ explicit credentials ไม่รับรองความสำเร็จ 4647 คือเริ่ม logoff ไม่ใช่ session จบ 4672 คือได้รับ privileges ไม่ใช่ใช้ privileges แล้ว 4776 เป็น NTLM บน authoritative host ไม่ให้ destination computer หรือ actor PID
7. PID อาจถูกใช้ซ้ำ LogonId ใช้เชื่อมโยงได้เฉพาะ host เดียวกันระหว่าง reboot ห้ามใช้เป็น global identity ไม่อนุมาน causality จากเวลาที่ใกล้กัน
8. SCM 7036/7040 และ audit 4660 ยังไม่มี typed projection ในชุดนี้ Raw event ยังเก็บได้ตามเดิม Service state/configuration snapshots เป็นแหล่งอิสระและไม่ควรแสร้งเป็น install history
9. fallback แบบ snapshot/diff บอกสถานะที่สังเกตและช่วงเวลาที่ตรวจพบเท่านั้น ไม่รู้ actor หรือเวลาการเปลี่ยนที่แน่นอน FileSystemWatcher/ReadDirectoryChangesW ไม่มี actor PID หรือ ordinary read events และอาจล้น buffer จึงต้องรายงาน gap และ rescan
10. USN เป็น change journal ที่สะสม reason flags ไม่ใช่ทุก file read/write ไม่มี actor PID; SecurityId ไม่ใช่ actor SID ต้องเก็บ volume/journal identity/cursor และตรวจ retention loss ห้ามสร้าง/ปรับ journal เพื่อแก้ coverage
11. ETW real-time consumption และ kernel sessions ขึ้นกับ privileges/session ACL ส่วน WFP subscription ต้องมี FWPM_ACTRL_SUBSCRIBE การเปิด provider หรือเปลี่ยน WFP engine options เป็นการเปลี่ยนสถานะระบบ ไม่ใช่การอ่านหลักฐานเดิม ชุดนี้ไม่ดำเนินการเหล่านั้น

## แหล่งอ้างอิง Microsoft

- [Sysmon event semantics และ schema discovery](https://learn.microsoft.com/en-us/sysinternals/downloads/sysmon)
- [Sysmon configuration/filter coverage](https://learn.microsoft.com/en-us/windows/security/operating-system-security/sysmon/sysmon-configuration-files)
- [Security 4656](https://learn.microsoft.com/en-us/windows/security/threat-protection/auditing/event-4656), [4663](https://learn.microsoft.com/en-us/windows/security/threat-protection/auditing/event-4663), [4660](https://learn.microsoft.com/en-us/windows/security/threat-protection/auditing/event-4660), [4657](https://learn.microsoft.com/en-us/windows/security/threat-protection/auditing/event-4657), [4670](https://learn.microsoft.com/en-us/windows/security/threat-protection/auditing/event-4670)
- [4624](https://learn.microsoft.com/en-us/windows/security/threat-protection/auditing/event-4624), [4625](https://learn.microsoft.com/en-us/windows/security/threat-protection/auditing/event-4625), [4634](https://learn.microsoft.com/en-us/windows/security/threat-protection/auditing/event-4634), [4647](https://learn.microsoft.com/en-us/windows/security/threat-protection/auditing/event-4647), [4648](https://learn.microsoft.com/en-us/windows/security/threat-protection/auditing/event-4648), [4672](https://learn.microsoft.com/en-us/windows/security/threat-protection/auditing/event-4672), [4776](https://learn.microsoft.com/en-us/windows/security/threat-protection/auditing/event-4776)
- [4697](https://learn.microsoft.com/en-us/windows/security/threat-protection/auditing/event-4697), [4698](https://learn.microsoft.com/en-us/windows/security/threat-protection/auditing/event-4698), [4702](https://learn.microsoft.com/en-us/windows/security/threat-protection/auditing/event-4702), [6416](https://learn.microsoft.com/en-us/windows/security/threat-protection/auditing/event-6416), [PnP family](https://learn.microsoft.com/en-us/windows/security/threat-protection/auditing/audit-pnp-activity)
- [SCM 7045](https://learn.microsoft.com/en-us/troubleshoot/windows-server/performance/troubleshoot-unexpected-reboots-system-event-logs)
- [ReadDirectoryChangesW](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-readdirectorychangesw), [USN_RECORD_V2](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ns-winioctl-usn_record_v2), [journal cursor/version](https://learn.microsoft.com/en-us/windows/win32/api/winioctl/ns-winioctl-read_usn_journal_data_v1)
- [ETW session access](https://learn.microsoft.com/en-us/windows/win32/api/evntcons/nf-evntcons-eventaccesscontrol), [WFP subscription access](https://learn.microsoft.com/en-us/windows/win32/api/fwpmu/nf-fwpmu-fwpmneteventsubscribe1)

## ตรวจสอบ

SystemActivitySmoke.SeedAsync(path) สร้าง fixture database โดยไม่ใช้ Windows source จริง ส่วน VerifyAsync(path) ตรวจ semantic fields, source collision, object-type filtering ก่อน paging, tied timestamps/cursors, duplicate/malformed payload, no provider-PID attribution, SQL parameter binding และการไม่ส่ง XML/unknown fields ออกผ่าน JSON การผ่าน fixture ไม่รับรองว่า Windows เครื่องใดมี channel/policy/retention ครบ ต้องแสดงสถานะ source จริงแยกต่างหาก
