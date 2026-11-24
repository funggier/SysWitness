/* SysWitness dashboard — no dependencies, no persistent credential storage. */
(function (root) {
  "use strict";
  function text(value) { return value === null || value === undefined || value === "" ? "—" : String(value); }
  function writeText(element, value) { element.textContent = text(value); return element; }
  function snapshotQuery(values, allowed) {
    const query = {};
    for (const key of allowed) {
      const value = values[key] === undefined ? "" : String(values[key]).trim();
      if (value) query[key] = value;
    }
    return Object.freeze(query);
  }
  function pageStep(state, direction, nextCursor) {
    if (direction === "next" && nextCursor) return {
      query: state.query, cursor: nextCursor,
      previous: [...state.previous, state.cursor], page: state.page + 1
    };
    if (direction === "previous" && state.previous.length) return {
      query: state.query, cursor: state.previous.at(-1),
      previous: state.previous.slice(0, -1), page: state.page - 1
    };
    return state;
  }
  function boundedTree(value) {
    const nodes = Array.isArray(value.nodes) ? value.nodes : [];
    const relationships = Array.isArray(value.relationships) ? value.relationships : [];
    const unresolved = Array.isArray(value.unresolved) ? value.unresolved : [];
    return {
      nodes: nodes.slice(0, 256).sort((a, b) => (a.depth ?? 99) - (b.depth ?? 99)),
      relationships: relationships.slice(0, 1000),
      unresolved: unresolved.slice(0, 1000),
      clientClipped: nodes.length > 256 || relationships.length > 1000 || unresolved.length > 1000
    };
  }
  function isGuid(value) {
    return typeof value === "string" &&
      /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value) &&
      value !== "00000000-0000-0000-0000-000000000000";
  }
  function requestError(status) {
    const messages = {
      400: "400 • ตัวกรองหรือตัวตนไม่ถูกต้อง กรุณาตรวจรูปแบบและช่วงค่า",
      401: "401 • Token ไม่ถูกต้องหรือหมดสิทธิ์ กรุณาเชื่อมต่อใหม่",
      403: "403 • Token นี้ไม่มีสิทธิ์อ่านข้อมูลส่วนนี้",
      404: "404 • ไม่พบข้อมูลหรือ API นี้ยังไม่พร้อมใช้งาน",
      429: "429 • คำขอถี่เกินไป กรุณารอตามเวลาที่เซิร์ฟเวอร์กำหนด"
    };
    return messages[status] || (status + " • เซิร์ฟเวอร์ไม่สามารถตอบข้อมูลได้");
  }
  const api = { text, writeText, snapshotQuery, pageStep, boundedTree, isGuid, requestError };
  if (typeof module !== "undefined" && module.exports) module.exports = api;
  else root.SysWitnessModel = Object.freeze(api);
})(typeof globalThis !== "undefined" ? globalThis : this);
