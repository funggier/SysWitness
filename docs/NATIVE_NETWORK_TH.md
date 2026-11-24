# การสังเกต endpoint เครือข่ายผ่าน Windows IP Helper

SysWitness อ่านตาราง TCP และ UDP ทั้ง IPv4/IPv6 ด้วย `GetExtendedTcpTable` และ `GetExtendedUdpTable` จาก Windows ทุกประมาณ 3 วินาทีหลังรอบก่อนเสร็จ ไม่มีการติดตั้ง driver/provider เปลี่ยน firewall หรือนโยบาย audit และไม่มีคำสั่งควบคุม socket/process ผ่าน API ข้อมูล

ตั้งค่าใน environment ของโปรแกรมก่อนเริ่ม:

| ตัวแปร | ค่า | ผล |
|---|---|---|
| `SYSWITNESS_NETWORK_MODE` | `auto` (ค่าเริ่มต้น) | อ่านตาราง endpoint เมื่อทำงานบน Windows |
| `SYSWITNESS_NETWORK_MODE` | `off` | ไม่เริ่มตัวเก็บนี้ |

การตั้งค่าเปลี่ยนผ่าน startup ในเครื่องเท่านั้น ผู้เรียก HTTP เปลี่ยนโหมดไม่ได้ หาก API native อ่านไม่ได้ จะรายงาน degraded แยก protocol/family โดยไม่ขอยกระดับสิทธิ์ ไม่เปลี่ยนสมาชิกกลุ่ม และไม่ตีความข้อผิดพลาดว่า endpoint ทั้งหมดหายไป

## หลักฐานและความหมาย

แหล่งหลักฐานคือ channel `WindowsNetworkSnapshot`, provider `Windows.IPHelper`, category `windows.network.inventory` มี XML ของ SysWitness ใน namespace `urn:syswitness:network-observation:v1` ไม่ใช่ XML จาก Windows Event Log

| sourceEventId | change | ความหมาย |
|---:|---|---|
| 1 | `firstObserved` | พบ endpoint ในตารางสำเร็จครั้งแรก หรือพบอีกหลังเคยไม่พบ |
| 2 | `changed` | key endpoint เดิมมี field เปลี่ยน เช่น TCP state |
| 3 | `notObserved` | endpoint ที่เคยพบไม่อยู่ในการอ่านตารางครั้งถัดไปที่สำเร็จ |

หลักฐานชุดแรกถูกเก็บด้วย เมื่อ field ไม่เปลี่ยนจะไม่บันทึกซ้ำเวลาแต่ละรอบ `OccurredAtUtc` เป็นเวลาจบรอบการสังเกต ไม่ใช่เวลาเปิด/ปิด socket มี captureStartedAtUtc/captureCompletedAtUtc เพื่อแสดงช่วงการอ่านหลายตาราง การเริ่มโปรแกรมใหม่สร้าง baseline ใหม่

`GET /api/v1/network/inventory` ต้องมี scope `network.read` และคืนหน้าของ **ประวัติการสังเกตและ diff** ไม่ใช่รายการ socket ที่ยังเปิด ณ เวลาที่เรียก รองรับ limit/cursor/channel/provider/sourceEventId/fromUtc/toUtc ตามรูปแบบ query มาตรฐาน มีค่าเริ่มต้น limit 100 และสูงสุด 500 ไม่มี SQL หรือ executable filter จากผู้เรียก ไม่มี raw XML ใน DTO สาธารณะ

## ขอบเขตความเชื่อมั่น

- ข้อมูลระบุ PID เท่านั้น: `processAttribution=pidOnly` และ `quality=derived` ไม่สร้าง ProcessGuid/process-instance identity จาก PID เพราะ Windows นำ PID กลับมาใช้ได้ เมื่อ PID เป็น 0 ระบุ attribution ว่า unavailable
- TCP มี local/remote endpoint และ state ตามที่ Windows รายงาน เมื่อ LISTEN ไม่คืนค่า remote ที่ไม่มีความหมาย Unknown state เก็บเลขเดิม ไม่มีการเดาทิศทางหรือการอนุญาต/บล็อก
- UDP เป็น endpoint ที่ bind ในเครื่อง อาจเป็น socket ที่ใช้ส่งอย่างเดียว ไม่มี remote endpoint, TCP state, ปริมาณ byte หรือคำรับรองว่ากำลังรับ packet
- socket อายุสั้นระหว่างรอบอาจไม่ถูกเห็น ตารางสี่ชุดและสถานะ process ไม่ใช่ snapshot เดียวกัน ไม่มีคำรับรอง completeness/lossless
- endpoint key ไม่ใช่ socket handle หลาย socket ที่มี field เหมือนกันไม่สามารถแยก instance จากข้อมูลตารางนี้
- ภายหลัง storage pause รอบ polling จะรอ retry หลักฐานเดิม ไม่เร่งสร้างคิวใหม่ Coverage แสดง paused และสาเหตุทั่วไปโดยไม่เขียน endpoint ลง log
- ต่อ partition จำกัด buffer 16 MiB, ไม่เกิน 16,384 rows และ retry หลัง probe สูงสุด 5 ครั้ง หากเกินขอบเขตถือว่าการอ่าน partition ล้มเหลว ไม่ใช้ข้อมูลบางส่วนเพื่อลบ inventory เดิม
- IPv6 scope ID แปลงตาม network byte order ที่เอกสาร Microsoft ระบุ มี synthetic fixture สำหรับ nonzero scope แต่ยังควรตรวจ link-local บน Windows เป้าหมายจริง
- DNS ETW ไม่ได้รวมในตัวเก็บ native นี้ Sysmon DNS ที่มีอยู่ยังเป็นแหล่งแยก ไม่ตีความ snapshot ว่าให้ประวัติ DNS

## การตรวจสอบ

`NativeNetworkSmoke.VerifyAsync(path)` ตรวจ fixture ของ native layouts ทั้งสี่, network-order ports/scopes, PID 0, state ไม่รู้จัก, count/buffer ที่ไม่สมบูรณ์, allocation/row budget, retry exhaustion, ความล้มเหลวแยก partition, diff, XML ที่ห้าม DTD และ paging SQLite

`NativeNetworkSmoke.VerifyLive()` สร้าง TCP listener/connection และ UDP ที่ loopback ของ process ทดสอบเองทั้งสอง family แล้วอ่านผ่าน native API ปิด resource ทุกตัวเมื่อจบ ไม่พิมพ์ endpoint ของเครื่อง

หลัง build API แบบ Release เรียก `tests/Test-NativeNetwork.ps1 -Mode auto` เพื่อตรวจ owned UDP observation/disappearance ผ่าน HTTP และ `-Mode off` เพื่อตรวจปิดตัวเก็บ ใช้ token/database ชั่วคราวและลบเมื่อจบ ผลทดสอบแสดงเพียง PASS/ข้อผิดพลาดทั่วไป ไม่รวม host inventory

## เอกสารอ้างอิง Microsoft

- [GetExtendedTcpTable](https://learn.microsoft.com/en-us/windows/win32/api/iphlpapi/nf-iphlpapi-getextendedtcptable)
- [GetExtendedUdpTable](https://learn.microsoft.com/en-us/windows/win32/api/iphlpapi/nf-iphlpapi-getextendedudptable)
- [MIB_TCPROW_OWNER_PID](https://learn.microsoft.com/en-us/windows/win32/api/tcpmib/ns-tcpmib-mib_tcprow_owner_pid)
- [MIB_TCP6ROW_OWNER_PID](https://learn.microsoft.com/en-us/windows/win32/api/tcpmib/ns-tcpmib-mib_tcp6row_owner_pid)
- [MIB_UDPROW_OWNER_PID](https://learn.microsoft.com/en-us/windows/win32/api/udpmib/ns-udpmib-mib_udprow_owner_pid)
- [MIB_UDP6ROW_OWNER_PID](https://learn.microsoft.com/en-us/windows/win32/api/udpmib/ns-udpmib-mib_udp6row_owner_pid)
- [Process handles and identifiers](https://learn.microsoft.com/en-us/windows/win32/procthread/process-handles-and-identifiers)
