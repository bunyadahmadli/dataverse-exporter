"use strict";

const $ = (id) => document.getElementById(id);

const state = {
  tables: [],          // {name, rowCount, columnCount}
  table: null,         // selected table name
  columns: [],         // {name, storeType, isPrimaryKey}
  visible: new Set(),  // visible column names
  filters: [],         // {column, op, value}
  sort: null,
  dir: "asc",
  page: 1,
  pageSize: 50,
  total: 0,
};

const OPS = {
  contains: "contains", startswith: "starts with", eq: "=", ne: "≠",
  gt: ">", gte: "≥", lt: "<", lte: "≤", null: "empty", notnull: "not empty",
};

const fmtInt = (n) => Number(n).toLocaleString();

/* ---------- Table list ---------- */

async function loadTables() {
  const res = await fetch("/api/tables");
  state.tables = await res.json();
  $("sidebarMeta").textContent =
    `${fmtInt(state.tables.length)} tables · ${fmtInt(state.tables.reduce((a, t) => a + t.rowCount, 0))} rows`;
  renderTableList();
}

function renderTableList() {
  const q = $("tableSearch").value.trim().toLowerCase();
  const list = $("tableList");
  list.innerHTML = "";
  for (const t of state.tables) {
    if (q && !t.name.toLowerCase().includes(q)) continue;
    const btn = document.createElement("button");
    btn.className = "table-item" + (t.name === state.table ? " active" : "");
    btn.innerHTML = `<span class="name"></span><span class="count"></span>`;
    btn.querySelector(".name").textContent = t.name;
    btn.querySelector(".count").textContent = fmtInt(t.rowCount);
    btn.onclick = () => selectTable(t.name);
    list.appendChild(btn);
  }
}

/* ---------- Table selection ---------- */

async function selectTable(name) {
  state.table = name;
  state.filters = [];
  state.sort = null;
  state.dir = "asc";
  state.page = 1;
  renderTableList();

  const res = await fetch(`/api/tables/${encodeURIComponent(name)}/columns`);
  state.columns = await res.json();

  // Default: PK + first 12 columns visible (avoid overwhelming wide tables).
  state.visible = new Set(state.columns.slice(0, 12).map((c) => c.name));
  const pk = state.columns.find((c) => c.isPrimaryKey);
  if (pk) state.visible.add(pk.name);

  $("empty").hidden = true;
  $("content").hidden = false;
  $("tableTitle").textContent = name;
  $("columnPanel").hidden = true;

  renderFilterColumns();
  renderColumnPanel();
  renderChips();
  await loadData();
}

/* ---------- Column panel ---------- */

function renderFilterColumns() {
  const sel = $("filterColumn");
  sel.innerHTML = "";
  for (const c of state.columns) {
    const opt = document.createElement("option");
    opt.value = c.name;
    opt.textContent = `${c.name} (${c.storeType})`;
    sel.appendChild(opt);
  }
}

function renderColumnPanel() {
  const q = $("columnSearch").value.trim().toLowerCase();
  const wrap = $("columnChecks");
  wrap.innerHTML = "";
  for (const c of state.columns) {
    if (q && !c.name.toLowerCase().includes(q)) continue;
    const label = document.createElement("label");
    const cb = document.createElement("input");
    cb.type = "checkbox";
    cb.checked = state.visible.has(c.name);
    cb.onchange = () => {
      cb.checked ? state.visible.add(c.name) : state.visible.delete(c.name);
      loadData();
    };
    const nameSpan = document.createElement("span");
    nameSpan.textContent = c.name;
    const typeSpan = document.createElement("span");
    typeSpan.className = "type";
    typeSpan.textContent = c.storeType;
    label.append(cb, nameSpan, typeSpan);
    wrap.appendChild(label);
  }
}

/* ---------- Filters ---------- */

function addFilter() {
  const column = $("filterColumn").value;
  const op = $("filterOp").value;
  const value = $("filterValue").value.trim();
  if (!column) return;
  if (!["null", "notnull"].includes(op) && !value) {
    $("filterValue").focus();
    return;
  }
  state.filters.push({ column, op, value });
  $("filterValue").value = "";
  state.page = 1;
  renderChips();
  loadData();
}

function renderChips() {
  const wrap = $("filterChips");
  wrap.innerHTML = "";
  state.filters.forEach((f, i) => {
    const chip = document.createElement("span");
    chip.className = "chip";
    const text = document.createElement("span");
    text.textContent = ["null", "notnull"].includes(f.op)
      ? `${f.column} ${OPS[f.op]}`
      : `${f.column} ${OPS[f.op]} "${f.value}"`;
    const x = document.createElement("button");
    x.textContent = "×";
    x.onclick = () => {
      state.filters.splice(i, 1);
      state.page = 1;
      renderChips();
      loadData();
    };
    chip.append(text, x);
    wrap.appendChild(chip);
  });
}

/* ---------- Data ---------- */

function buildQuery(forExport = false) {
  const p = new URLSearchParams();
  if (!forExport) {
    p.set("page", state.page);
    p.set("pageSize", state.pageSize);
  }
  if (state.sort) {
    p.set("sort", state.sort);
    p.set("dir", state.dir);
  }
  const cols = state.columns.filter((c) => state.visible.has(c.name)).map((c) => c.name);
  if (cols.length && cols.length < state.columns.length) p.set("columns", cols.join(","));
  for (const f of state.filters)
    p.append("f", ["null", "notnull"].includes(f.op) ? `${f.column}|${f.op}` : `${f.column}|${f.op}|${f.value}`);
  return p;
}

async function loadData() {
  if (!state.table) return;
  $("loading").hidden = false;
  $("gridError").hidden = true;
  try {
    const res = await fetch(`/api/tables/${encodeURIComponent(state.table)}/data?` + buildQuery());
    const body = await res.json();
    if (!res.ok) throw new Error(body.error || res.statusText);
    state.total = body.total;
    renderGrid(body.rows);
    renderPager();
    const t = state.tables.find((x) => x.name === state.table);
    $("tableInfo").textContent =
      `${fmtInt(state.total)} rows` +
      (state.filters.length ? ` (filtered, ${fmtInt(t?.rowCount ?? 0)} total)` : "") +
      ` · ${state.columns.length} columns`;
  } catch (err) {
    $("gridError").textContent = "Error: " + err.message;
    $("gridError").hidden = false;
    $("grid").querySelector("tbody").innerHTML = "";
  } finally {
    $("loading").hidden = true;
  }
}

function renderGrid(rows) {
  const visibleCols = state.columns.filter((c) => state.visible.has(c.name));
  const cols = visibleCols.length ? visibleCols : state.columns;

  const thead = $("grid").querySelector("thead");
  thead.innerHTML = "";
  const tr = document.createElement("tr");
  for (const c of cols) {
    const th = document.createElement("th");
    th.textContent = c.name;
    if (state.sort === c.name) {
      const dir = document.createElement("span");
      dir.className = "dir";
      dir.textContent = state.dir === "asc" ? "▲" : "▼";
      th.appendChild(dir);
    }
    th.title = c.storeType;
    th.onclick = () => {
      if (state.sort === c.name) state.dir = state.dir === "asc" ? "desc" : "asc";
      else { state.sort = c.name; state.dir = "asc"; }
      state.page = 1;
      loadData();
    };
    tr.appendChild(th);
  }
  thead.appendChild(tr);

  const tbody = $("grid").querySelector("tbody");
  tbody.innerHTML = "";
  for (const row of rows) {
    const tr = document.createElement("tr");
    for (const c of cols) {
      const td = document.createElement("td");
      renderCell(td, row[c.name], c);
      tr.appendChild(td);
    }
    tbody.appendChild(tr);
  }
}

const GUID_RE = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const DATE_RE = /^\d{4}-\d{2}-\d{2}T\d{2}:/;

function renderCell(td, value, column) {
  if (value === null || value === undefined) {
    td.innerHTML = `<span class="null">null</span>`;
    return;
  }
  if (typeof value === "boolean") {
    td.textContent = value ? "✓" : "✗";
    return;
  }
  if (typeof value === "string") {
    if (GUID_RE.test(value)) {
      td.textContent = value;
      td.className = "mono";
      td.title = value;
      return;
    }
    if (DATE_RE.test(value)) {
      td.textContent = new Date(value).toLocaleString();
      return;
    }
    td.textContent = value.length > 200 ? value.slice(0, 200) + "…" : value;
    td.title = value.length > 200 ? value.slice(0, 2000) : value;
    return;
  }
  if (typeof value === "number") {
    td.textContent = Number.isInteger(value) ? fmtInt(value) : value.toLocaleString();
    return;
  }
  td.textContent = String(value);
}

/* ---------- Paging ---------- */

function renderPager() {
  const pages = Math.max(1, Math.ceil(state.total / state.pageSize));
  $("pageInfo").textContent = `Page ${fmtInt(state.page)} / ${fmtInt(pages)}`;
  $("prevPage").disabled = state.page <= 1;
  $("nextPage").disabled = state.page >= pages;
}

/* ---------- Events ---------- */

$("tableSearch").addEventListener("input", renderTableList);
$("columnSearch").addEventListener("input", renderColumnPanel);
$("addFilter").addEventListener("click", addFilter);
$("filterValue").addEventListener("keydown", (e) => { if (e.key === "Enter") addFilter(); });
$("columnsBtn").addEventListener("click", () => {
  $("columnPanel").hidden = !$("columnPanel").hidden;
});
$("colsAll").addEventListener("click", () => {
  state.columns.forEach((c) => state.visible.add(c.name));
  renderColumnPanel();
  loadData();
});
$("colsNone").addEventListener("click", () => {
  state.visible.clear();
  const pk = state.columns.find((c) => c.isPrimaryKey);
  if (pk) state.visible.add(pk.name);
  renderColumnPanel();
  loadData();
});
$("prevPage").addEventListener("click", () => { state.page--; loadData(); });
$("nextPage").addEventListener("click", () => { state.page++; loadData(); });
$("pageSize").addEventListener("change", (e) => {
  state.pageSize = parseInt(e.target.value, 10);
  state.page = 1;
  loadData();
});
$("exportBtn").addEventListener("click", () => {
  window.location.href =
    `/api/tables/${encodeURIComponent(state.table)}/export?` + buildQuery(true);
});

loadTables();
