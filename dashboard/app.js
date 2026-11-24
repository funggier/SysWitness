/* SysWitness local dashboard. All API operations are same-origin GET reads. */
(function () {
  "use strict";
  const M = globalThis.SysWitnessModel;
  const $ = id => document.getElementById(id);
  const controllers = new Set();
  const clients = [];
  let token = "";
  let session = 0;
  let activePanel = "overview";
  let pollTimer = null;
  let pollDelay = 15000;
  let overviewBusy = false;
  let overviewPollBlocked = false;
  let storageBusy = false;
  let gapsBusy = false;
  let detailVersion = 0;

  function node(tag, value, className) {
    const element = document.createElement(tag);
    if (value !== undefined) M.writeText(element, value);
    if (className) element.className = className;
    return element;
  }
  function append(parent, ...children) { parent.append(...children); return parent; }
  function clear(element) { element.replaceChildren(); }
  function message(element, text, kind) {
    M.writeText(element, text);
    element.className = "state-line" + (kind ? " " + kind : "");
  }
  function empty(element, value) { clear(element); element.append(node("p", value, "empty")); }
  function stamp() { return new Date().toISOString().replace("T", " ").replace(/\.\d{3}Z$/, " UTC"); }
  function utc(value) { return value ? String(value).replace("T", " ").replace(/Z$/, " UTC") : "—"; }
  function bytes(value) {
    if (value === null || value === undefined || !Number.isFinite(Number(value))) return "—";
    const number = Number(value);
    return number >= 1073741824 ? (number / 1073741824).toFixed(2) + " GiB" :
      number >= 1048576 ? (number / 1048576).toFixed(1) + " MiB" :
      number >= 1024 ? (number / 1024).toFixed(1) + " KiB" : number + " B";
  }
  function statusBadge(value) {
    const kinds = { collecting: "good", ok: "good", degraded: "warning", starting: "warning",
      disabled: "neutral", observed: "neutral", derived: "warning", unavailable: "warning" };
    return node("span", value, "badge " + (kinds[value] || "neutral"));
  }
  function sourceText(source) {
    if (!source) return "—";
    return [source.channel, source.provider, "Event " + M.text(source.sourceEventId),
      "Record " + M.text(source.sourceRecordId), source.computer].filter(x => x !== null && x !== undefined && x !== "").join("\n");
  }
  function processText(process) {
    if (!process) return "—";
    return [process.imagePath || process.processName, process.processId === null || process.processId === undefined ?
      null : "PID " + process.processId, process.processGuid, process.attribution || process.identityQuality].filter(Boolean).join("\n") || "—";
  }
  function endpointText(endpoint) {
    if (!endpoint) return "—";
    return [endpoint.address, endpoint.port === null || endpoint.port === undefined ? null : "port " + endpoint.port,
      endpoint.hostName, endpoint.role].filter(x => x !== null && x !== undefined && x !== "").join("\n") || "—";
  }
  function table(container, rows, columns, caption) {
    clear(container);
    if (!Array.isArray(rows) || !rows.length) { empty(container, "ไม่พบข้อมูลที่ตรงกันในหลักฐานที่ยังเก็บไว้"); return; }
    const wrap = node("div", undefined, "table-wrap");
    const grid = node("table");
    const cap = node("caption", caption || "ผลลัพธ์จาก API", "sr-only");
    const head = node("thead");
    const heading = node("tr");
    for (const column of columns) {
      const th = node("th", column.label); th.scope = "col"; heading.append(th);
    }
    head.append(heading);
    const body = node("tbody");
    for (const row of rows) {
      const tr = node("tr");
      for (const column of columns) {
        const td = node("td");
        const value = column.value(row);
        if (value instanceof Node) td.append(value);
        else M.writeText(td, value);
        if (column.mono) td.className = "mono";
        tr.append(td);
      }
      body.append(tr);
    }
    append(grid, cap, head, body); wrap.append(grid); container.append(wrap);
  }
  function facts(container, entries) {
    table(container, entries.map(([key, value]) => ({ key, value })),
      [{ label: "รายการ", value: row => row.key }, { label: "ค่าจากเซิร์ฟเวอร์", value: row => row.value }], "รายละเอียดจากเซิร์ฟเวอร์");
    const grid = container.querySelector("table");
    if (grid) grid.classList.add("detail-table");
  }
  function metric(container, label, value, note) {
    const card = node("div", undefined, "metric");
    append(card, node("div", label, "metric-label"), node("div", value, "metric-value"), node("div", note, "metric-note"));
    container.append(card);
  }
  function limitations(container, values) {
    if (!Array.isArray(values) || !values.length) return;
    const list = node("ul", undefined, "limitations");
    for (const value of values.slice(0, 40)) list.append(node("li", value));
    container.append(list);
  }
  function evidence(row) {
    const details = node("details");
    details.append(node("summary", "รายละเอียดหลักฐาน"));
    let loaded = false;
    details.addEventListener("toggle", () => {
      if (details.open && !loaded) {
        loaded = true;
        const pre = node("pre", JSON.stringify(row, null, 2), "code-line");
        details.append(pre);
      }
    });
    return details;
  }
  function failure(error) {
    if (error.status) return M.requestError(error.status) + (error.code ? " [" + error.code + "]" : "");
    return error.message || "อ่านข้อมูลไม่สำเร็จ";
  }
  function syncButtons() {
    const connected = Boolean(token);
    $("disconnect-button").disabled = !connected;
    $("connect-button").disabled = connected;
    $("token").disabled = connected;
    for (const button of document.querySelectorAll("[data-needs-connection]")) button.disabled = !connected;
    for (const client of clients) client.sync();
  }
  function stopPoll() { if (pollTimer !== null) clearTimeout(pollTimer); pollTimer = null; }
  function schedulePoll() {
    stopPoll();
    if (!token || overviewPollBlocked || activePanel !== "overview" || document.hidden || !$("auto-refresh").checked) return;
    pollTimer = setTimeout(() => { pollTimer = null; loadOverview(); }, pollDelay);
  }
  function disconnect(reason) {
    token = ""; session++; stopPoll();
    for (const controller of controllers) controller.abort();
    controllers.clear();
    overviewBusy = false; overviewPollBlocked = false; storageBusy = false; gapsBusy = false; detailVersion++;
    $("token").value = "";
    for (const id of ["overview-metrics", "storage-metrics", "gaps-result", "detail-result"]) clear($(id));
    for (const id of ["coverage-result", "collector-result", "storage-result", "retention-result"]) empty($(id), "ยังไม่เชื่อมต่อ");
    for (const id of ["overview-state", "gaps-state", "storage-state", "retention-state"]) message($(id), "รอการเชื่อมต่อ");
    $("event-detail").hidden = true;
    for (const client of clients) client.reset();
    message($("connection-state"), reason || "ตัดการเชื่อมต่อแล้ว • ล้าง Token และข้อมูลที่แสดง", reason ? "error" : "");
    syncButtons();
  }
  async function api(path, query) {
    if (!token) throw new Error("ยังไม่เชื่อมต่อ");
    if (!path.startsWith("/api/v1/") || path.includes("?")) throw new Error("เส้นทาง API ไม่ถูกต้อง");
    const expectedSession = session;
    const controller = new AbortController();
    controllers.add(controller);
    const timeout = setTimeout(() => controller.abort(), 20000);
    const url = new URL(path, location.origin);
    const params = new URLSearchParams(query || {});
    if (params.size) url.search = params.toString();
    try {
      const response = await fetch(url, { method: "GET", mode: "same-origin", cache: "no-store",
        credentials: "omit", redirect: "error", referrerPolicy: "no-referrer",
        headers: { "X-SysWitness-Token": token, Accept: "application/json" }, signal: controller.signal });
      if (expectedSession !== session) throw Object.assign(new Error("ยกเลิกคำขอแล้ว"), { stale: true });
      if (!response.ok) {
        if (response.status === 401) disconnect(M.requestError(401));
        if (response.status === 429) {
          const retry = response.headers.get("Retry-After");
          const seconds = Number(retry);
          const wait = retry && Number.isFinite(seconds) ? seconds * 1000 : retry ? Date.parse(retry) - Date.now() : 30000;
          pollDelay = Math.max(30000, Math.min(300000, Number.isFinite(wait) ? wait : 30000));
        }
        let code;
        try { const data = await response.json(); if (typeof data.error === "string") code = data.error; } catch (_) {}
        throw Object.assign(new Error(M.requestError(response.status)), { status: response.status, code });
      }
      const result = await response.json();
      if (expectedSession !== session) throw Object.assign(new Error("ยกเลิกคำขอแล้ว"), { stale: true });
      return result;
    } catch (error) {
      if (expectedSession !== session) error.stale = true;
      if (error.name === "AbortError" && !error.stale) error.message = "คำขอใช้เวลาเกิน 20 วินาทีหรือถูกยกเลิก กรุณาจำกัดช่วงเวลาแล้วลองใหม่";
      throw error;
    } finally { clearTimeout(timeout); controllers.delete(controller); }
  }

  async function loadOverview() {
    if (!token || overviewBusy) return;
    overviewBusy = true; stopPoll();
    const expected = session;
    $("overview-refresh").disabled = true;
    message($("overview-state"), "กำลังอ่านสถานะล่าสุด…");
    const results = await Promise.allSettled([api("/api/v1/health"), api("/api/v1/monitoring/coverage")]);
    if (expected !== session) return;
    const [health, coverage] = results;
    overviewPollBlocked = coverage.status === "rejected" && [403, 404].includes(coverage.reason.status);
    if (health.status === "fulfilled" && coverage.status === "fulfilled") pollDelay = 15000;
    clear($("overview-metrics"));
    metric($("overview-metrics"), "API", health.status === "fulfilled" ? health.value.status : "อ่านไม่ได้", "สถานะบริการจาก /health");
    if (coverage.status === "fulfilled") {
      const value = coverage.value;
      const channels = Array.isArray(value.channels) ? value.channels : [];
      metric($("overview-metrics"), "การเก็บ Event Log", value.status, "ค่ารายงานโดยเซิร์ฟเวอร์");
      metric($("overview-metrics"), "แหล่งข้อมูลกำลังเก็บ", Array.isArray(value.enabledCollectors) ? value.enabledCollectors.length : "—",
        "จากที่ตั้งค่า " + (Array.isArray(value.configuredCollectors) ? value.configuredCollectors.length : "—") + " แหล่ง");
      metric($("overview-metrics"), "Local Process Collector", value.processCollector ? value.processCollector.status : "—",
        value.processCollector ? value.processCollector.activeSource || "ยังไม่มี Active Source" : "ไม่มีข้อมูลจาก API");
      table($("coverage-result"), channels, [
        { label: "Channel", value: r => r.channel },
        { label: "สถานะ", value: r => statusBadge(r.status) },
        { label: "อ่านครั้งล่าสุด UTC", value: r => utc(r.lastPollAtUtc), mono: true },
        { label: "อ่าน / Record ล่าสุด", value: r => M.text(r.lastReadCount) + " / " + M.text(r.lastRecordId) },
        { label: "Gap ที่บันทึก", value: r => r.recordedGaps },
        { label: "ข้อผิดพลาด", value: r => r.lastError }
      ], "สถานะแต่ละ Channel");
      const note = node("p", value.completeness, "form-help"); $("coverage-result").append(note);
      if (value.processCollector) facts($("collector-result"), Object.entries(value.processCollector).map(([k, v]) => [k, v]));
      else empty($("collector-result"), "API ไม่ได้รายงานข้อมูล Local Process Collector");
      const degraded = value.status === "degraded" || value.processCollector && value.processCollector.status === "degraded";
      message($("overview-state"), (degraded ? "degraded • มีแหล่งข้อมูลทำงานไม่สมบูรณ์ • " : "อ่านสถานะแล้ว • ") + stamp(), degraded ? "warning" : "good");
    } else {
      empty($("coverage-result"), failure(coverage.reason)); empty($("collector-result"), "อ่าน Coverage ไม่สำเร็จ");
      message($("overview-state"), failure(coverage.reason) + (overviewPollBlocked ? " • หยุดอ่านอัตโนมัติสำหรับสถานะนี้" : pollDelay > 15000 ? " • รอบอัตโนมัติถัดไปอย่างน้อย " + Math.ceil(pollDelay / 1000) + " วินาที" : ""), "error");
    }
    if (health.status === "rejected" && coverage.status === "fulfilled") {
      message($("overview-state"), "อ่านได้บางส่วน • Health: " + failure(health.reason), "warning");
    }
    overviewBusy = false; syncButtons(); schedulePoll();
  }
  async function loadGaps(event) {
    event.preventDefault();
    if (!token || gapsBusy) return;
    const expected = session; gapsBusy = true;
    const button = $("gaps-form").querySelector("button"); button.disabled = true;
    message($("gaps-state"), "กำลังอ่านประวัติ Gap…"); empty($("gaps-result"), "กำลังโหลด…");
    try {
      const values = Object.fromEntries(new FormData($("gaps-form")));
      const data = await api("/api/v1/monitoring/gaps", M.snapshotQuery(values, ["channel", "limit"]));
      if (expected !== session) return;
      const rows = Array.isArray(data) ? data : data.items;
      table($("gaps-result"), rows, [
        { label: "หลักฐาน Gap", value: r => evidence(r) }
      ]);
      message($("gaps-state"), "อ่านแล้ว • สูงสุด " + values.limit + " รายการ • " + stamp(), "good");
    } catch (error) { if (expected === session) { empty($("gaps-result"), "อ่านข้อมูลไม่สำเร็จ"); message($("gaps-state"), failure(error), "error"); } }
    finally { if (expected === session) { gapsBusy = false; syncButtons(); } }
  }
  async function loadStorage() {
    if (!token || storageBusy) return;
    const expected = session; storageBusy = true;
    $("storage-refresh").disabled = true;
    message($("storage-state"), "กำลังอ่านข้อมูลพื้นที่จัดเก็บ…");
    message($("retention-state"), "กำลังอ่านประวัติ…");
    const results = await Promise.allSettled([api("/api/v1/storage/status"), api("/api/v1/storage/retention", { limit: 50 })]);
    if (expected !== session) return;
    const [status, retention] = results;
    clear($("storage-metrics"));
    if (status.status === "fulfilled") {
      const data = status.value;
      metric($("storage-metrics"), "หลักฐานที่เก็บ", data.storedEventCount, "จำนวน Event ที่ยังคงอยู่");
      metric($("storage-metrics"), "ขนาดไฟล์รวม", bytes(data.totalPhysicalBytes), "Database + WAL + SHM");
      metric($("storage-metrics"), "Soft limit", bytes(data.softLimitBytes), "ไม่ใช่โควตาดิสก์ตายตัว");
      metric($("storage-metrics"), "พื้นที่ว่าง", bytes(data.availableFreeDiskBytes), "สำรองขั้นต่ำ " + bytes(data.minFreeDiskBytes));
      facts($("storage-result"), [
        ["รับหลักฐานใหม่ได้ (snapshot)", data.canAcceptNewEvidence], ["เหตุผลหยุดรับ", data.ingestPauseReason],
        ["ถึง Soft limit", data.softLimitReached], ["ถึงพื้นที่สำรอง", data.diskReserveReached],
        ["Database", bytes(data.databaseBytes)], ["WAL", bytes(data.walBytes)], ["SHM", bytes(data.shmBytes)],
        ["Retention (วัน)", data.retentionDays === null ? "ปิดใช้งาน" : data.retentionDays],
        ["จำนวนที่ Retention ลบสะสม", data.recordedRetentionDeletedEvents], ["ขอบเขตของค่า", data.completeness]
      ]);
      message($("storage-state"), (data.canAcceptNewEvidence === false ? "หยุดรับหลักฐานตามสถานะพื้นที่ • " : "อ่านแล้ว • ") + stamp(),
        data.canAcceptNewEvidence === false ? "warning" : "good");
    } else { empty($("storage-result"), failure(status.reason)); message($("storage-state"), failure(status.reason), "error"); }
    if (retention.status === "fulfilled") {
      table($("retention-result"), retention.value, [
        { label: "ลบเมื่อ UTC", value: r => utc(r.deletedAtUtc), mono: true },
        { label: "Channel", value: r => r.channel }, { label: "จำนวน", value: r => r.eventCount },
        { label: "ช่วง Observation UTC", value: r => utc(r.oldestObservedAtUtc) + "\n" + utc(r.newestObservedAtUtc), mono: true },
        { label: "Cutoff UTC", value: r => utc(r.cutoffUtc), mono: true }
      ], "ประวัติการลบสำเนาหลักฐานตาม Retention");
      message($("retention-state"), "50 รายการล่าสุดตามขีดจำกัดคำขอ • " + stamp(), "good");
    } else { empty($("retention-result"), "อ่านประวัติไม่สำเร็จ"); message($("retention-state"), failure(retention.reason), "error"); }
    storageBusy = false; syncButtons();
  }

  const commonFields = [
    { name: "channel", label: "Channel", placeholder: "เช่น System" },
    { name: "provider", label: "Provider", placeholder: "ชื่อ Provider ตรงตามข้อมูล" },
    { name: "sourceEventId", label: "Source Event ID", type: "number", min: 0, max: 65535 },
    { name: "limit", label: "จำนวนต่อหน้า (1–500)", type: "number", min: 1, max: 500, value: 50, required: true },
    { name: "fromUtc", label: "ตั้งแต่ UTC (รวม)", placeholder: "2026-11-24T00:00:00Z", wide: true },
    { name: "toUtc", label: "ก่อน UTC (ไม่รวม)", placeholder: "2026-11-25T00:00:00Z", wide: true }
  ];
  function field(form, spec) {
    const label = node("label", spec.label);
    if (spec.wide) label.className = "wide";
    let input;
    if (spec.options) {
      input = node("select");
      for (const [value, text] of spec.options) { const option = node("option", text); option.value = value; input.append(option); }
    } else {
      input = node("input"); input.type = spec.type || "text";
      if (spec.placeholder) input.placeholder = spec.placeholder;
      if (spec.min !== undefined) input.min = spec.min;
      if (spec.max !== undefined) input.max = spec.max;
      if (spec.required) input.required = true;
      if (spec.value !== undefined) input.value = spec.value;
      input.autocomplete = "off"; input.spellcheck = false;
    }
    input.name = spec.name;
    label.append(input); form.append(label);
    return input;
  }
  const processFields = [
    { name: "mode", label: "มุมมองโปรเซส", options: [["feed", "Start / Exit ทุกแหล่ง"], ["timeline", "Sysmon Lifecycle ตาม GUID"], ["activity", "Sysmon Activity ตาม GUID + Computer"], ["tree", "Sysmon Tree ตาม GUID + Computer"]] },
    { name: "processGuid", label: "Process GUID", placeholder: "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx", wide: true },
    { name: "computer", label: "Computer ตรงตาม Source", placeholder: "จำเป็นสำหรับ Activity / Tree" },
    { name: "depth", label: "Tree Depth (0–8)", type: "number", min: 0, max: 8, value: 3 },
    { name: "treeLimit", label: "Tree Nodes (1–256)", type: "number", min: 1, max: 256, value: 100 }
  ];
  const activityKinds = [["files", "ไฟล์"], ["registry", "Registry"], ["authentication", "ยืนยันตัวตน / Logon"],
    ["services", "Services"], ["drivers", "Drivers"], ["tasks", "Scheduled Tasks"], ["devices", "Devices"],
    ["modules", "Modules / Image Load"], ["process-access", "Process Access"]];
  const genericAllowed = commonFields.map(f => f.name);
  const timeColumn = { label: "เกิดเหตุ UTC", value: r => utc(r.occurredAtUtc), mono: true };
  const sourceColumn = { label: "Source / Provenance", value: r => sourceText(r.source) };
  const payloadColumn = { label: "Payload / คุณภาพ", value: r => M.text(r.payloadStatus) + "\n" + M.text(r.observationMethod) + " / " + M.text(r.quality) };
  const detailColumn = { label: "หลักฐาน", value: r => evidence(r) };
  function processColumns() {
    return [timeColumn, { label: "เหตุการณ์", value: r => r.kind }, { label: "Process", value: r => processText(r) },
      { label: "Parent / Exit", value: r => processText(r.parent) + "\nexit " + M.text(r.exitStatus) },
      payloadColumn, sourceColumn, detailColumn];
  }
  function networkColumns(mode) {
    const common = [timeColumn, { label: "Process / Attribution", value: r => processText(r.process) }];
    if (mode === "dns") common.push(
      { label: "DNS Name", value: r => r.queryName }, { label: "Status / ผลที่รายงาน", value: r => M.text(r.queryStatus) + "\n" + M.text(r.queryResults) });
    else common.push(
      { label: "Source Endpoint", value: r => endpointText(r.sourceEndpoint) },
      { label: "Destination Endpoint", value: r => endpointText(r.destinationEndpoint) },
      { label: "Protocol / Direction / Decision", value: r => [M.text(r.protocol), M.text(r.direction), M.text(r.decision)].join("\n") });
    common.push(sourceColumn, { label: "Payload", value: r => r.payloadStatus }, detailColumn);
    return common;
  }
  function activityPayload(row) {
    return row.file || row.registry || row.authentication || row.service || row.driver || row.task ||
      row.device || row.module || row.processAccess || null;
  }
  function activityTarget(row) {
    if (row.file) return row.file.path || row.file.devicePath;
    if (row.registry) return [row.registry.objectPath, row.registry.valueName, row.registry.newName].filter(Boolean).join("\n");
    if (row.authentication) {
      const data = row.authentication;
      return [data.target?.domain, data.target?.name, data.sourceAddress,
        data.logonType === null || data.logonType === undefined ? null : "Logon type " + data.logonType,
        data.status].filter(Boolean).join("\n");
    }
    if (row.service) return [row.service.name, row.service.imagePath].filter(Boolean).join("\n");
    if (row.driver) return [row.driver.name, row.driver.imagePath].filter(Boolean).join("\n");
    if (row.task) return row.task.name;
    if (row.device) return [row.device.instanceId, row.device.description].filter(Boolean).join("\n");
    if (row.module) return row.module.imagePath;
    if (row.processAccess) return processText(row.processAccess.target) + "\nGranted access " + M.text(row.processAccess.grantedAccess);
    return null;
  }
  function activityColumns() {
    return [timeColumn, { label: "ประเภท / Operation", value: r => M.text(r.kind) + "\n" + M.text(activityPayload(r)?.operation) },
      { label: "Actor", value: r => processText(r.actor) },
      { label: "เป้าหมาย / ข้อมูลที่รายงาน", value: activityTarget },
      { label: "Subject", value: r => r.subject ? [r.subject.domain, r.subject.name, r.subject.sid, r.subject.logonId].filter(Boolean).join("\n") : "—" },
      payloadColumn, sourceColumn, detailColumn];
  }
  function renderTree(container, data) {
    clear(container);
    const view = M.boundedTree(data);
    const partial = data.depthLimitReached || data.nodeLimitReached || data.evidenceLimitReached || view.clientClipped;
    if (partial) container.append(node("p", "partial • View ถูกจำกัดด้วย Depth / Nodes / Source evidence หรือขอบเขตแสดงผล โปรดอ่าน Flags ด้านล่าง", "partial-note"));
    if (data.hasCycle) container.append(node("p", "พบวงจร / Self-reference ใน View นี้ ความสัมพันธ์แสดงแยกเป็นตารางโดยไม่วนขยาย", "partial-note"));
    const meta = node("div"); container.append(meta);
    facts(meta, [["Root GUID", data.rootProcessGuid], ["Computer", data.computer], ["Method / Quality", M.text(data.method) + " / " + M.text(data.quality)],
      ["Source events used / limit", M.text(data.sourceEventsUsed) + " / " + M.text(data.sourceEventLimit)],
      ["Depth / Node limits", M.text(data.maxDepth) + " / " + M.text(data.nodeLimit)],
      ["depthLimitReached", data.depthLimitReached], ["nodeLimitReached", data.nodeLimitReached], ["evidenceLimitReached", data.evidenceLimitReached],
      ["hasCycle", data.hasCycle], ["Client display clipped", view.clientClipped]]);
    container.append(node("h3", "Nodes • ระดับมาจาก Server, Reference มีระดับว่าง", "subheading"));
    const nodes = node("div"); container.append(nodes);
    table(nodes, view.nodes, [
      { label: "Depth", value: r => r.depth },
      { label: "GUID / State", value: r => M.text(r.processGuid) + "\n" + M.text(r.evidenceState), mono: true },
      { label: "Image จาก Creation แรกใน View", value: r => r.creations?.[0]?.imagePath },
      { label: "Creations / Parent conflict", value: r => M.text(r.creations?.length) + " / " + M.text(r.hasConflictingParents) }, detailColumn
    ], "โหนดโปรเซสใน View ที่มีขอบเขต");
    container.append(node("h3", "Relationships • Parent GUID → Child GUID", "subheading"));
    const edges = node("div"); container.append(edges);
    table(edges, view.relationships, [
      { label: "Parent GUID", value: r => r.parentProcessGuid, mono: true }, { label: "Child GUID", value: r => r.childProcessGuid, mono: true },
      { label: "Basis", value: r => r.basis }, { label: "Child creation evidence", value: r => M.text(r.evidence?.eventId) + "\n" + sourceText(r.evidence?.source) },
      detailColumn
    ], "ความสัมพันธ์ที่ต้นทางรายงาน พร้อมหลักฐานของ Child creation");
    container.append(node("h3", "Unresolved • หลักฐานความสัมพันธ์ที่ระบุไม่ได้", "subheading"));
    const unresolved = node("div"); container.append(unresolved);
    table(unresolved, view.unresolved, [{ label: "เหตุผล", value: r => r.reason }, detailColumn]);
    limitations(container, data.limitations);
    container.append(node("p", "Flags เป็น false ไม่ได้ยืนยันความครบถ้วน ไม่มีการเติมความสัมพันธ์จาก PID หรืออ้างว่า Reference-only คือโปรเซสที่พบจริง", "form-help"));
  }
  async function showEvent(eventId) {
    if (!token) return;
    const expected = session, version = ++detailVersion;
    $("event-detail").hidden = false; empty($("detail-result"), "กำลังอ่านสรุปเหตุการณ์…");
    try {
      const data = await api("/api/v1/events/" + encodeURIComponent(eventId));
      if (expected !== session || version !== detailVersion) return;
      facts($("detail-result"), Object.entries(data));
    } catch (error) { if (expected === session && version === detailVersion) empty($("detail-result"), failure(error)); }
  }
  function makeClient(config) {
    const host = $(config.id + "-client");
    const card = node("article", undefined, "card");
    const form = node("form");
    const grid = node("div", undefined, "form-grid");
    const controls = {};
    for (const spec of config.fields) controls[spec.name] = field(grid, spec);
    const actions = node("div", undefined, "form-actions");
    const submit = node("button", "ค้นหา / อ่านใหม่", "primary"); submit.type = "submit";
    const note = node("p", "เปลี่ยนตัวกรองแล้วกดค้นหา • อ่านใหม่เริ่มจากหน้าแรก", "form-help");
    append(actions, submit, note);
    append(form, grid, node("p", "เวลาใช้ ISO 8601 พร้อมเขตเวลา เช่น 2026-11-24T00:00:00Z • From รวม, To ไม่รวม • ไม่ใช่การค้นหาข้อความอิสระ", "form-help"), actions);
    const stateLabel = node("p", "รอการเชื่อมต่อ", "state-line"); stateLabel.setAttribute("role", "status");
    const result = node("div");
    const pager = node("div", undefined, "pager");
    const pageInfo = node("span", "สูงสุด 500 รายการต่อหน้า • Cursor ไม่ใช่ Snapshot");
    const pagerButtons = node("div", undefined, "pager-buttons");
    const previous = node("button", "← หน้าก่อน"); previous.type = "button";
    const next = node("button", "หน้าถัดไป →"); next.type = "button";
    append(pagerButtons, previous, next); append(pager, pageInfo, pagerButtons);
    append(card, form, stateLabel, result, pager); host.append(card);
    let busy = false, version = 0, page = null, nextCursor = null, mode = null;
    let path = null, lastMeta = null;
    function sync() {
      submit.disabled = !token || busy;
      previous.disabled = !token || busy || !page || !page.previous.length;
      next.disabled = !token || busy || !nextCursor;
    }
    function reset() {
      version++; busy = false; page = null; nextCursor = null; mode = null; path = null; lastMeta = null;
      empty(result, "ยังไม่เชื่อมต่อ"); message(stateLabel, "รอการเชื่อมต่อ");
      M.writeText(pageInfo, "สูงสุด 500 รายการต่อหน้า • Cursor ไม่ใช่ Snapshot"); sync();
    }
    async function load() {
      if (!token || busy || !page || !path) return;
      const expected = session, requestVersion = ++version; busy = true; sync();
      const oldInfo = pageInfo.textContent;
      message(stateLabel, "กำลังโหลด…"); empty(result, "กำลังอ่านข้อมูลตามตัวกรอง…");
      const query = { ...page.query }; if (page.cursor) query.cursor = page.cursor;
      try {
        const data = await api(path, query);
        if (expected !== session || requestVersion !== version) return;
        if (mode === "tree") {
          renderTree(result, data); nextCursor = null;
          M.writeText(pageInfo, "Tree ไม่มี Cursor • Node limit " + page.query.limit + " • Source budget " + M.text(data.sourceEventLimit));
          message(stateLabel, (data.depthLimitReached || data.nodeLimitReached || data.evidenceLimitReached ? "partial • " : "") + "อ่าน View แล้ว • " + stamp(),
            data.depthLimitReached || data.nodeLimitReached || data.evidenceLimitReached ? "warning" : "good");
        } else {
          if (!Array.isArray(data.items)) throw new Error("รูปแบบคำตอบจาก API ไม่ตรงกับ Page contract");
          nextCursor = typeof data.nextCursor === "string" && data.nextCursor ? data.nextCursor : null;
          const rows = data.items.slice(0, 500);
          table(result, rows, config.columns(mode), "ผลการค้นหา " + config.id);
          limitations(result, data.limitations);
          if (lastMeta) result.append(node("p", lastMeta, "form-help"));
          if (data.correlationMethod) result.append(node("p", M.text(data.correlationMethod) + " / " + M.text(data.quality) +
            " • ไม่พิสูจน์ DNS-to-connection causality", "form-help"));
          const clipped = data.items.length > 500;
          M.writeText(pageInfo, "หน้า " + page.page + " • ได้ " + M.text(data.returned) + " / Limit " + M.text(data.limit) +
            " • " + (nextCursor ? "อาจมีข้อมูลต่อ" : "ไม่มี Cursor ถัดไป") + " • Cursor ไม่ใช่ Snapshot");
          message(stateLabel, (clipped ? "partial • แสดงสูงสุด 500 แถว • " : rows.length ? "อ่านแล้ว • " : "ไม่พบหลักฐานที่ตรงกัน • ") + stamp(), clipped ? "warning" : "good");
        }
      } catch (error) {
        if (expected !== session || requestVersion !== version) return;
        nextCursor = null; empty(result, "ไม่สามารถอ่านข้อมูลส่วนนี้ได้");
        message(stateLabel, failure(error), "error"); M.writeText(pageInfo, oldInfo);
      } finally { if (expected === session && requestVersion === version) { busy = false; sync(); } }
    }
    form.addEventListener("submit", event => {
      event.preventDefault();
      if (!token || busy) return;
      try {
        const values = Object.fromEntries(new FormData(form));
        const built = config.build(values);
        mode = built.mode; path = built.path; lastMeta = built.meta || null; nextCursor = null;
        page = { query: built.query, cursor: null, previous: [], page: 1 };
        load();
      } catch (error) { message(stateLabel, error.message, "error"); }
    });
    next.addEventListener("click", () => { if (nextCursor && !busy) { page = M.pageStep(page, "next", nextCursor); load(); } });
    previous.addEventListener("click", () => { if (page?.previous.length && !busy) { page = M.pageStep(page, "previous"); load(); } });
    const client = { sync, reset };
    clients.push(client); sync();
  }
  makeClient({
    id: "events", fields: [...commonFields, { name: "category", label: "Category", placeholder: "ค่าตรงตามข้อมูล" }],
    build: values => ({ mode: "events", path: "/api/v1/events", query: M.snapshotQuery(values, [...genericAllowed, "category"]) }),
    columns: () => [timeColumn, { label: "Category", value: r => r.category }, { label: "Channel / Provider", value: r => r.channel + "\n" + r.provider },
      { label: "Source ID / Record", value: r => M.text(r.sourceEventId) + " / " + M.text(r.sourceRecordId) },
      { label: "Event ID", value: r => r.eventId, mono: true }, { label: "รายละเอียด", value: r => {
        const button = node("button", "เปิดสรุป"); button.type = "button"; button.addEventListener("click", () => showEvent(r.eventId)); return button;
      } }]
  });
  makeClient({
    id: "processes", fields: [...processFields, ...commonFields],
    build: values => {
      const mode = values.mode;
      if (mode !== "feed" && !M.isGuid(values.processGuid.trim())) throw new Error("กรุณาระบุ Process GUID แบบมีขีดและไม่เป็นศูนย์");
      if ((mode === "tree" || mode === "activity") && (!values.computer.trim() || values.computer.length > 256 || /[\u0000-\u001f\u007f]/.test(values.computer)))
        throw new Error("Activity / Tree ต้องระบุ Computer ตรงตาม Source ไม่เกิน 256 ตัวอักษรและไม่มี Control Character");
      if (mode === "tree") return {
        mode, path: "/api/v1/processes/" + encodeURIComponent(values.processGuid.trim()) + "/tree",
        query: Object.freeze({ computer: values.computer, depth: values.depth, limit: values.treeLimit }),
        meta: "Tree ใช้เฉพาะ Computer, Depth และ Node limit ตัวกรอง Feed ด้านล่างไม่ใช้กับ Tree"
      };
      const path = mode === "feed" ? "/api/v1/processes/events" : "/api/v1/processes/" + encodeURIComponent(values.processGuid.trim()) + "/" + mode;
      const query = { ...M.snapshotQuery(values, genericAllowed) };
      if (mode === "activity") query.computer = values.computer;
      return { mode, path, query: Object.freeze(query), meta: mode === "activity" ? "Activity ต้องมี processes.read และ network.read ใน Token เดียวกัน" :
        mode === "timeline" ? "Timeline ตรงกับ Subject GUID ของ Sysmon; ไม่มี Computer filter" : "รวม Source ที่ API รองรับ แถวว่างไม่ได้ยืนยันว่าไม่มีโปรเซส" };
    },
    columns: mode => mode === "activity" ? [timeColumn, { label: "ชนิด", value: r => r.kind },
      { label: "ข้อมูลสังเกต", value: r => r.lifecycle ? processText(r.lifecycle) : r.connection ?
        endpointText(r.connection.sourceEndpoint) + " → " + endpointText(r.connection.destinationEndpoint) :
        r.dnsQuery ? M.text(r.dnsQuery.queryName) + "\nstatus " + M.text(r.dnsQuery.queryStatus) : "—" },
      { label: "Source / Provenance", value: r => sourceText((r.lifecycle || r.connection || r.dnsQuery)?.source) }, detailColumn] : processColumns()
  });
  makeClient({
    id: "network", fields: [{ name: "mode", label: "ประเภทหลักฐาน", options: [["connections", "Connections / WFP decisions"], ["dns", "DNS queries"]] },
      { name: "processGuid", label: "Subject Process GUID (ถ้ามี)", wide: true }, ...commonFields],
    build: values => {
      if (values.processGuid.trim() && !M.isGuid(values.processGuid.trim())) throw new Error("Process GUID ต้องมีขีดและไม่เป็นศูนย์");
      return { mode: values.mode, path: "/api/v1/network/" + values.mode,
        query: M.snapshotQuery(values, [...genericAllowed, "processGuid"]) };
    }, columns: networkColumns
  });
  makeClient({
    id: "activity", fields: [{ name: "kind", label: "ประเภทกิจกรรม", options: activityKinds }, ...commonFields],
    build: values => ({ mode: values.kind, path: "/api/v1/activity/" + values.kind,
      query: M.snapshotQuery(values, genericAllowed), meta: "ข้อมูลที่หายไปแสดงเป็น — • เปิดรายละเอียดเพื่ออ่าน Payload ที่มีจริงและ Source provenance" }),
    columns: activityColumns
  });

  document.querySelectorAll("nav [data-panel]").forEach(button => {
    button.addEventListener("click", () => {
      activePanel = button.dataset.panel;
      for (const panel of document.querySelectorAll(".panel")) panel.hidden = panel.id !== activePanel;
      for (const item of document.querySelectorAll("nav [data-panel]")) item.setAttribute("aria-pressed", String(item === button));
      stopPoll();
      if (activePanel === "overview") { if (token) loadOverview(); }
      else if (activePanel === "storage" && token && !$("storage-metrics").children.length) loadStorage();
    });
  });
  $("connect-form").addEventListener("submit", event => {
    event.preventDefault();
    if (token) return;
    const entered = $("token").value;
    if (!entered.trim()) return;
    token = entered; $("token").value = ""; session++; pollDelay = 15000; overviewPollBlocked = false;
    message($("connection-state"), "พร้อมส่งคำขอด้วย Token ในหน่วยความจำ • สิทธิ์ตรวจแยกในแต่ละ API", "good");
    syncButtons();
    if (activePanel === "overview") loadOverview();
    if (activePanel === "storage") loadStorage();
  });
  $("disconnect-button").addEventListener("click", () => disconnect());
  $("overview-refresh").addEventListener("click", loadOverview);
  $("storage-refresh").addEventListener("click", loadStorage);
  $("gaps-form").addEventListener("submit", loadGaps);
  $("auto-refresh").addEventListener("change", schedulePoll);
  $("detail-close").addEventListener("click", () => { detailVersion++; $("event-detail").hidden = true; clear($("detail-result")); });
  document.addEventListener("visibilitychange", () => { if (document.hidden) stopPoll(); else schedulePoll(); });
  window.addEventListener("pagehide", () => disconnect());
  syncButtons();
})();
