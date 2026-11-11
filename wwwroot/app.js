(() => {
  const $ = id => document.getElementById(id);
  const CALCULATED = new Set(["Age", "Is Sample Twin", "Left Hand Ratio (2D:4D)", "Right Hand Ratio (2D:4D)"]);
  const LOCKED = new Set(["Id"]);       // columns that are always exported (cannot be unselected)
  const OPS = {
    text:   [["contains", "Contains"], ["notContains", "Not Contains"], ["equals", "Equals"], ["notEquals", "Not Equals"]],
    number: [["gt", "Greater than"], ["gte", "Greater than or equal"], ["lt", "Less than"], ["lte", "Less than or equal"], ["eq", "Equal"], ["notEquals", "Not Equals"]],
    date:   [["eq", "On"], ["notEquals", "Not on"], ["gt", "After"], ["gte", "On or after"], ["lt", "Before"], ["lte", "On or before"]]
  };

  let sessionId = null;
  let sessionVersion = 0;    // bumped by the server whenever clean-up rules are applied on the clean-up page
  let cleanupRules = [];     // clean-up rules currently applied to this upload
  let info = [];             // column metadata from the server: {name,type,distinctCount,values,min,max}
  let byName = {};
  let columns = [];          // final columns, in order
  const selected = new Set();
  let renames = {};          // original column name -> new header (export only)
  let order = [];            // every column in the user's export order (only the selected ones are exported)
  let sorts = [];            // sort levels, first = primary: {column, dir: "asc"|"desc"}
  // filter tree: a group is {mode: "and"|"or", items: [...]}, a filter is {column, op, value}; groups nest freely
  let root = { mode: "and", items: [] };
  let dlSeq = 0;
  let matchCount = null, totalRows = 0, filterError = "";
  let savedFilters = [];     // saved filters (SQLite, server side)
  let outputSheets = [];
  let activeSheetIndex = 0;
  let restoringSheet = false;
  const PREVIEW_ROWS = 10;
  const OP_TEXT = { contains: "contains", notContains: "does not contain", equals: "=", notEquals: "≠", eq: "=", gt: ">", gte: "≥", lt: "<", lte: "≤" };

  const el = (tag, cls, text) => { const e = document.createElement(tag); if (cls) e.className = cls; if (text != null) e.textContent = text; return e; };
  const setStatus = (node, msg, cls) => { node.textContent = msg; node.className = "status" + (cls ? " " + cls : ""); };
  // the tree without empty filters / empty groups (what is sent to the server)
  const activeItems = items => {
    const out = [];
    for (const n of items) {
      if (n.items) { const sub = activeItems(n.items); if (sub.length) out.push({ mode: n.mode, items: sub }); }
      else if (n.value !== "" && n.value != null) out.push({ column: n.column, op: n.op, value: n.value });
    }
    return out;
  };
  const activeFilters = () => activeItems(root.items);
  const ensureLocked = () => LOCKED.forEach(c => { if (columns.includes(c)) selected.add(c); });
  // a saved order may miss new columns or contain gone ones: keep the known ones; a missing column goes back right after
  // the column that precedes it in the file (e.g. "Other Caste" after "Caste" when a clean-up rule is undone)
  const normOrder = o => {
    const have = new Set(columns), seen = new Set();
    const out = (o || []).filter(c => have.has(c) && !seen.has(c) && seen.add(c));
    columns.forEach((c, i) => {
      if (seen.has(c)) return;
      let at = 0;
      for (let j = i - 1; j >= 0; j--) { const k = out.indexOf(columns[j]); if (k >= 0) { at = k + 1; break; } }
      out.splice(at, 0, c); seen.add(c);
    });
    return out;
  };
  const normSorts = list => {
    const seen = new Set();
    return (Array.isArray(list) ? list : []).filter(x => x && columns.includes(x.column) && !seen.has(x.column) && seen.add(x.column))
      .map(x => ({ column: x.column, dir: x.dir === "desc" ? "desc" : "asc" }));
  };
  // The full order remembers excluded columns too; columns newly added to the source are included by default.
  const restoreSelection = (saved, savedOrder) => {
    selected.clear();
    (Array.isArray(saved) ? saved : columns).forEach(c => { if (columns.includes(c)) selected.add(c); });
    if (Array.isArray(savedOrder)) {
      const known = new Set([...savedOrder, ...(Array.isArray(saved) ? saved : [])]);
      columns.forEach(c => { if (!known.has(c)) selected.add(c); });
    }
    ensureLocked();
  };
  const chosenCols = () => order.filter(c => selected.has(c) || LOCKED.has(c));
  const fmt = n => Number(n).toLocaleString();
  let srcFile = "", srcSheet = "";
  // header status pill: Ready / Processing / Completed / Completed with warnings / Failed
  const PILL = { ready: ["", "Ready"], processing: ["processing", "Processing…"], done: ["done", "Completed"], warn: ["warn", "Completed with warnings"], failed: ["failed", "Failed"] };
  function setPill(kind, text) { const [cls, label] = PILL[kind]; const p = $("hdrStatus"); p.className = "pill" + (cls ? " " + cls : ""); p.textContent = text || label; }
  function updateSummary() {
    const box = $("summary");
    if (!sessionId) { box.classList.add("hidden"); return; }
    const items = [["File", srcFile], ["Source sheet", srcSheet], ["Rows", fmt(totalRows)], ["Columns", fmt(columns.length)],
      ["Clean-up", cleanupRules.length ? `${cleanupRules.length} rule(s) applied` : "None (values as uploaded)"],
      ["Output sheets", fmt(outputSheets.length)], ["Editing", outputSheets[activeSheetIndex]?.name || ""],
      ["Rows matching", matchCount == null ? "—" : fmt(matchCount)], ["Columns selected", fmt(chosenCols().length)]];
    box.replaceChildren(...items.filter(([, v]) => v !== "" && v != null).map(([k, v]) => {
      const d = el("div"), dl = el("dl"); const dd = el("dd", null, v); dd.title = v;
      dl.append(el("dt", null, k), dd); d.append(dl); return d;
    }));
    box.classList.remove("hidden");
  }
  const MAX_UPLOAD = 20 * 1024 * 1024;                       // same limit as the server
  const readJson = async res => { try { return await res.json(); } catch { return {}; } };   // error pages (e.g. 413) are not JSON
  const safeName = v => { if (!v) return "transformed.xlsx"; try { return decodeURIComponent(v); } catch { return v; } };   // "50%.xlsx" must not throw

  // ------------------------------------------------------------------ upload
  $("uploadBtn").onclick = async () => {
    const f = $("file").files[0];
    if (!f) return setStatus($("status"), "Choose an .xlsx file first.", "err");
    if (!/\.xlsx$/i.test(f.name)) return setStatus($("status"), "Only .xlsx files are supported.", "err");
    if (f.size > MAX_UPLOAD) return setStatus($("status"), `That file is ${(f.size / 1048576).toFixed(1)} MB; the limit is 20 MB.`, "err");
    if (sessionId && (outputSheets.length > 1 || activeFilters().length > 0) &&
        !confirm("Uploading a new file resets your current sheets and filters. Continue?")) return;

    closePreviewPanel(false);
    $("savedContent").hidden = true; $("savedToggle").setAttribute("aria-expanded", "false");
    $("uploadBtn").disabled = true;
    $("previewCard").classList.add("hidden");
    $("notes").classList.add("hidden");
    setStatus($("status"), "Uploading and transforming…"); setPill("processing", "Uploading…");

    try {
      const fd = new FormData();
      fd.append("file", f);
      let res;
      try { res = await fetch("/api/upload", { method: "POST", body: fd }); }
      catch { throw new Error("Could not reach the server. Check the connection and try again."); }
      const data = await readJson(res);
      if (!res.ok) throw new Error(data.error || (res.status === 413 ? "The file is too large (limit 20 MB)." : `Upload failed (HTTP ${res.status}).`));

      sessionId = data.id; sessionVersion = data.version || 1; cleanupRules = data.cleanup || [];
      srcFile = f.name; srcSheet = data.sheet || "";
      $("hdrFile").textContent = f.name; $("hdrFile").title = f.name; $("hdrCtx").classList.remove("hidden");
      info = data.columnInfo;
      byName = Object.fromEntries(info.map(c => [c.name, c]));
      columns = data.columns;
      order = [...columns]; sorts = [];
      selected.clear();
      renames = {};
      $("renameToggle").checked = false;
      $("saveFilters").checked = false; $("saveName").value = ""; setStatus($("saveFiltersMsg"), "");
      columns.forEach(c => selected.add(c));          // everything selected by default
      root = { mode: "and", items: [] };
      outputSheets = [{ name: "Transformed Data", state: null }];
      activeSheetIndex = 0;
      $("fileName").value = f.name.replace(/\.xlsx$/i, "") + "_Transformed";
      setupNameCustom = false; syncSetupName();
      setStatus($("workflowStatus"), "");
      renderSheetTabs();
      matchCount = totalRows = data.rowCount;
      filterError = "";

      setStatus($("status"),
        `Done: ${fmt(data.rowCount)} rows, ${columns.length} columns (sheet “${data.sheet}”).`, "ok");
      setPill("ready", "Workbook loaded");

      showNotes(data);

      $("search").value = "";
      setStatus($("savedMsg"), "");
      renderFilters();
      renderColumns();
      loadSaved();
      $("previewCard").classList.remove("hidden");
      pvKey = ""; pvPendingKey = ""; pvResult = null; ++pvNo; ++reqNo; countPending = false; schedulePreview(); updateStepper();
    } catch (e) {
      setStatus($("status"), e.message + " Check the file and try again.", "err"); setPill("failed", "Upload failed");
    } finally {
      $("uploadBtn").disabled = false;
    }
  };

  function captureSheet() {
    if (!outputSheets.length || activeSheetIndex < 0 || restoringSheet) return;
    outputSheets[activeSheetIndex].state = {
      root: JSON.parse(JSON.stringify(root)), selected: [...selected], renames: { ...renames },
      order: [...order], sorts: sorts.map(x => ({ ...x })),
      renameEnabled: $("renameToggle").checked,
      saveFilters: $("saveFilters").checked, saveName: $("saveName").value
    };
  }
  function restoreSheet(index) {
    captureSheet();
    activeSheetIndex = index;
    const sh = outputSheets[index];
    restoringSheet = true;
    const st = sh.state;
    root = st ? cleanRoot(st.root, []) : { mode: "and", items: [] };   // never restore filters on columns this file does not have
    restoreSelection(st?.selected, st?.order);
    renames = st ? { ...st.renames } : {};
    order = normOrder(st?.order); sorts = normSorts(st?.sorts);
    $("renameToggle").checked = st?.renameEnabled || false;
    $("saveFilters").checked = st?.saveFilters || false;
    $("saveName").value = st?.saveName || ""; setStatus($("saveFiltersMsg"), "");
    $("search").value = "";
    restoringSheet = false;
    renderSheetTabs(); renderFilters(); renderColumns(); scheduleCount();
  }
  // saved filters may refer to columns that are missing from the current file: drop those instead of crashing the page
  function cleanRoot(r, skipped) {
    const src = r || { mode: "and", items: [] };
    return { mode: src.mode === "or" ? "or" : "and", items: importItems(src.items || [], skipped) };
  }
  function updateBadges() {
    const name = outputSheets[activeSheetIndex]?.name;
    document.querySelectorAll(".editing-badge").forEach(b => { b.textContent = name ? `Editing sheet: ${name}` : ""; b.classList.toggle("hidden", !name); });
  }
  function renderSheetTabs() {
    updateBadges();
    for (const id of ["sheetTabs", "pvTabs"]) {
      const box = $(id); box.replaceChildren();
      outputSheets.forEach((sh, i) => {
        const t = el("button", "tab" + (i === activeSheetIndex ? " active" : ""), sh.name);
        t.type = "button";
        t.setAttribute("role", "tab"); t.setAttribute("aria-selected", i === activeSheetIndex);
        t.tabIndex = i === activeSheetIndex ? 0 : -1;
        t.title = "Double-click to rename";
        t.onclick = () => { if (i !== activeSheetIndex) restoreSheet(i); };
        t.ondblclick = renameActiveSheet;
        t.onkeydown = e => {          // ← → between tabs, F2 renames (keyboard path for double-click)
          if (e.key === "F2") { e.preventDefault(); return renameActiveSheet(); }
          const d = e.key === "ArrowRight" ? 1 : e.key === "ArrowLeft" ? -1 : 0; if (!d) return;
          e.preventDefault(); const j = (i + d + outputSheets.length) % outputSheets.length;
          if (j !== activeSheetIndex) restoreSheet(j);
          $(id).querySelectorAll(".tab")[j]?.focus();
        };
        box.append(t);
      });
      const add = el("button", "tab add", "+ Add sheet"); add.type = "button"; add.onclick = addSheet; box.append(add);
    }
    $("removeSheetBtn").disabled = outputSheets.length < 2;
  }
  function renameActiveSheet() {
    const i = activeSheetIndex, sh = outputSheets[i]; if (!sh) return;
    const n = prompt("Output sheet name (max 31 characters):", sh.name);
    if (n == null) return;
    const cleaned = n.trim();
    if (!cleaned || cleaned.length > 31 || /[\\/?*\[\]:]/.test(cleaned)) return alert("Enter a valid Excel sheet name (1–31 characters; no \\ / ? * [ ] : characters).");
    if (outputSheets.some((other, j) => j !== i && other.name.toLowerCase() === cleaned.toLowerCase())) return alert("Sheet names must be unique.");
    sh.name = cleaned; renderSheetTabs(); schedulePreview();
  }
  $("renameSheetBtn").onclick = renameActiveSheet;
  $("removeSheetBtn").onclick = () => {
    if (outputSheets.length < 2) return;
    const i = activeSheetIndex;
    if (!confirm(`Remove sheet “${outputSheets[i].name}”?`)) return;
    outputSheets.splice(i, 1);
    activeSheetIndex = -1; restoreSheet(Math.min(i, outputSheets.length - 1));
  };
  function addSheet() {
    captureSheet();
    let n = `Sheet ${outputSheets.length + 1}`, suffix = 2;
    while (outputSheets.some(sh => sh.name.toLowerCase() === n.toLowerCase())) n = `Sheet ${outputSheets.length + 1} (${suffix++})`;
    outputSheets.push({ name: n, state: null });
    restoreSheet(outputSheets.length - 1);
  }
  // Saved setups live on the server (SQLite), so every device sees them. Older versions kept them in this browser:
  // those are moved to the server once (WORKFLOW_KEY), then removed from the browser.
  const WORKFLOW_KEY = "etl-multi-sheet-workflows-v1";
  let workflows = [];        // [{id, name, savedAt, workflow: {fileName, sheets}}]
  let setupNameCustom = false;
  function syncSetupName() {
    if (setupNameCustom) return;
    $("setupName").value = $("fileName").value.trim().replace(/\.xlsx$/i, "").trim();
  }
  $("fileName").addEventListener("input", syncSetupName);
  $("setupName").addEventListener("input", () => { setupNameCustom = true; });
  async function loadWorkflows() {
    try {
      const res = await fetch("/api/workflows");
      if (!res.ok) throw new Error();
      workflows = await res.json();
    } catch { setStatus($("workflowStatus"), "Saved setups could not be loaded from the server.", "err"); refreshWorkflowList(); return false; }
    refreshWorkflowList(); return true;
  }
  function refreshWorkflowList() {
    const sel = $("workflowList"), prior = sel.value;
    sel.replaceChildren(new Option(workflows.length ? "Select a saved setup…" : "No saved setups yet", ""));
    workflows.forEach(w => sel.append(new Option(w.name, w.name)));
    if (workflows.some(w => w.name === prior)) sel.value = prior;
  }
  async function postWorkflow(name, workflow) {
    let res;
    try { res = await fetch("/api/workflows", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ name, workflow }) }); }
    catch { throw new Error("could not reach the server"); }
    if (!res.ok) { const d = await readJson(res); throw new Error(d.error || `HTTP ${res.status}`); }
  }
  async function saveCurrentWorkflow() {
    captureSheet();
    const fileBase = $("fileName").value.trim().replace(/\.xlsx$/i, "").trim();
    const name = ($("setupName").value.trim() || fileBase || "Transformed Data").slice(0, 100);
    const workflow = { fileName: $("fileName").value, sheets: outputSheets.map(x => ({ name: x.name, state: x.state })) };
    try { await postWorkflow(name, workflow); }
    catch (e) { setStatus($("workflowStatus"), `Could not save the setup: ${e.message}. Your settings are still here; retry Save current setup.`, "err"); return null; }
    await loadWorkflows();
    $("workflowList").value = name; $("setupName").value = name;
    return name;
  }
  async function migrateLocalWorkflows() {
    let local = [];
    try { local = JSON.parse(localStorage.getItem(WORKFLOW_KEY) || "[]"); } catch { return; }
    if (!Array.isArray(local) || !local.length) return;
    let allOk = true;
    for (const w of local) {
      if (!w || !w.name || workflows.some(x => x.name.toLowerCase() === String(w.name).toLowerCase())) continue;
      try { await postWorkflow(String(w.name).slice(0, 100), { fileName: w.fileName || "", sheets: w.sheets || [] }); } catch { allOk = false; }
    }
    if (allOk) { try { localStorage.removeItem(WORKFLOW_KEY); } catch { } }
    await loadWorkflows();
  }
  $("loadWorkflowBtn").onclick = () => {
    const name = $("workflowList").value;
    if (!name) return setStatus($("workflowStatus"), "Choose a saved setup from the list first.", "err");
    if (!sessionId) return setStatus($("workflowStatus"), "Upload a workbook first, then load a setup.", "err");
    const workflow = workflows.find(x => x.name === name)?.workflow; if (!workflow) return;
    if (workflow.fileName) $("fileName").value = workflow.fileName;
    setupNameCustom = false; syncSetupName();
    const skipped = [];
    outputSheets = (workflow.sheets || []).map(x => ({ name: x.name, state: x.state ? { ...x.state, root: cleanRoot(x.state.root, skipped) } : null }));
    if (!outputSheets.length) outputSheets = [{ name: "Transformed Data", state: null }];
    activeSheetIndex = -1; restoreSheet(0);
    const gone = [...new Set(skipped)];
    setStatus($("workflowStatus"), gone.length
      ? `Loaded “${name}”, but skipped filters on columns that are missing or no longer fit in this file: ${gone.join(", ")}.`
      : `Loaded saved setup “${name}”. Filters and columns have been restored.`, gone.length ? "err" : "ok");
  };
  $("saveWorkflowBtn").onclick = async () => {
    const name = await saveCurrentWorkflow();
    if (!name) return;
    setStatus($("workflowStatus"), `Saved the current sheets as “${name}”.`, "ok");
  };
  $("deleteWorkflowBtn").onclick = async () => {
    const name = $("workflowList").value;
    if (!name) return setStatus($("workflowStatus"), "Choose a saved setup to delete.", "err");
    const w = workflows.find(x => x.name === name); if (!w) return;
    if (!confirm(`Delete the saved setup "${name}"?`)) return;
    try {
      const r = await fetch("/api/workflows/" + w.id, { method: "DELETE" });
      if (!r.ok && r.status !== 404) throw new Error();
    } catch { return setStatus($("workflowStatus"), "Could not delete the setup. Please try again.", "err"); }
    await loadWorkflows();
    setStatus($("workflowStatus"), `Deleted “${name}”.`, "ok");
  };
  loadWorkflows().then(ok => { if (ok) migrateLocalWorkflows(); });

  // "Save setup" on export is a per-browser preference (off by default, so exports do not pile up setups)
  try { $("saveSetup").checked = localStorage.getItem("etl-save-setup") === "1"; } catch { }
  $("saveSetup").onchange = () => { try { localStorage.setItem("etl-save-setup", $("saveSetup").checked ? "1" : "0"); } catch { } };

  // ------------------------------------------------------------------ filters
  // new conditions start on the first column that is useful to filter (not the Id, which is unique per row)
  const newFilter = () => {
    const first = info.find(c => !LOCKED.has(c.name) && c.values) || info.find(c => !LOCKED.has(c.name)) || info[0];
    return { column: first.name, op: OPS[first.type][0][0], value: "" };
  };
  $("addFilter").onclick = () => { root.items.push(newFilter()); renderFilters(); };
  $("addGroup").onclick = () => { root.items.push({ mode: "and", items: [newFilter()] }); renderFilters(); };
  $("clearFilters").onclick = () => { root.items = []; renderFilters(); scheduleCount(); };

  function makeSelect(options, current) {
    const s = document.createElement("select");
    options.forEach(([value, label]) => {
      const o = document.createElement("option");
      o.value = value; o.textContent = label;
      if (value === current) o.selected = true;
      s.append(o);
    });
    return s;
  }

  // Layout: [Group 1]  -- AND/OR --  [Group 2]  -- AND/OR --  ...   (root.mode joins the groups)
  // and inside every group its own AND/OR joins that group's filters.
  function renderFilters() {
    const box = $("filters");
    box.replaceChildren();
    const n = root.items.length;
    $("noFilters").classList.toggle("hidden", n > 0);
    $("addGroup").classList.toggle("hidden", n === 0);      // groups only once there is something to group
    if (n > 1) {
      const head = el("div", "ghead");
      head.append(el("span", "opt", "Keep rows that"));
      const sel = makeSelect([["and", "Match ALL conditions (AND)"], ["or", "Match ANY condition (OR)"]], root.mode);
      sel.setAttribute("aria-label", "How the top-level conditions combine");
      sel.onchange = () => { root.mode = sel.value; renderFilters(); scheduleCount(); };
      head.append(sel);
      box.append(head);
    }
    if (root.items.some(it => !it.items)) {
      const fh = el("div", "fhead"); ["Column", "Operator", "Value", ""].forEach(t => fh.append(el("span", null, t))); box.append(fh);
    }
    let g = 0;
    root.items.forEach((it, i) => {
      if (i > 0) box.append(el("div", "joiner", root.mode === "or" ? "OR" : "AND"));
      box.append(it.items ? renderGroup(it, root, "Group " + (++g)) : renderFilter(it, root));
    });
    refresh();
  }

  // one group: its own AND/OR for the filters inside it
  function renderGroup(g, parent, title) {
    const box = el("div", "gbox");
    const head = el("div", "ghead");
    head.append(el("b", null, title));
    const sel = makeSelect([["and", "Match ALL conditions (AND)"], ["or", "Match ANY condition (OR)"]], g.mode);
    sel.setAttribute("aria-label", title + ": how its conditions combine");
    sel.onchange = () => { g.mode = sel.value; scheduleCount(); refresh(); };
    const addF = el("button", "mini", "+ Add condition");
    addF.onclick = () => { g.items.push(newFilter()); renderFilters(); };
    const rm = el("button", "x", "✕");
    rm.title = "Remove group"; rm.setAttribute("aria-label", "Remove " + title.toLowerCase());
    rm.onclick = () => { parent.items.splice(parent.items.indexOf(g), 1); renderFilters(); scheduleCount(); };
    const addG = el("button", "mini", "+ Add sub-group");
    addG.onclick = () => { g.items.push({ mode: "and", items: [newFilter()] }); renderFilters(); };
    head.append(sel, addF, addG, rm);
    box.append(head);
    if (g.items.length === 0) box.append(el("p", "gempty", "Empty group: add a condition."));
    g.items.forEach((n, i) => box.append(n.items ? renderGroup(n, g, "Sub-group") : renderFilter(n, g)));
    return box;
  }

  function renderFilter(f, parent) {
    const ci = byName[f.column];
    const row = el("div", "frow");

    // column
    const colSel = makeSelect(info.map(c => [c.name, c.type === "text" ? c.name : `${c.name}  (${c.type})`]), f.column);
    colSel.onchange = () => {
      f.column = colSel.value;
      f.op = OPS[byName[f.column].type][0][0];
      f.value = "";
      renderFilters(); scheduleCount();
    };

    // operator
    const opSel = makeSelect(OPS[ci.type], f.op);
    opSel.onchange = () => { f.op = opSel.value; f.value = ""; renderFilters(); scheduleCount(); };

    // value
    let valueCtl;
    if (ci.type === "text" && (f.op === "equals" || f.op === "notEquals") && ci.values) {
      valueCtl = makeSelect([["", "— choose a value —"], ...ci.values.map(v => [v.value, `${v.value}  (${fmt(v.count)})`])], f.value);
      valueCtl.onchange = () => { f.value = valueCtl.value; scheduleCount(); };
    } else {
      valueCtl = document.createElement("input");
      valueCtl.type = ci.type === "number" ? "number" : ci.type === "date" ? "date" : "text";
      if (ci.type === "number") valueCtl.step = "any";
      if (ci.type === "date") { if (ci.minText) valueCtl.min = ci.minText; if (ci.maxText) valueCtl.max = ci.maxText; }
      valueCtl.placeholder = ci.type === "number" ? "number" : ci.type === "date" ? "YYYY-MM-DD" : "text";
      valueCtl.value = f.value;
      if (ci.values && ci.type !== "date") {                                  // suggestions from the distinct values
        const dl = document.createElement("datalist");
        dl.id = "dl-" + (dlSeq++);
        ci.values.forEach(v => { const o = document.createElement("option"); o.value = v.value; dl.append(o); });
        valueCtl.setAttribute("list", dl.id);
        row.append(dl);
      }
      valueCtl.oninput = valueCtl.onchange = () => { if (f.value === valueCtl.value.trim()) return; f.value = valueCtl.value.trim(); scheduleCount(); };
    }

    // remove
    const rm = el("button", "x", "✕");
    rm.title = "Remove condition"; rm.setAttribute("aria-label", "Remove condition on " + f.column);
    colSel.setAttribute("aria-label", "Column"); opSel.setAttribute("aria-label", "Operator"); valueCtl.setAttribute("aria-label", "Value for " + f.column);
    rm.onclick = () => { parent.items.splice(parent.items.indexOf(f), 1); renderFilters(); scheduleCount(); };

    // hint under the row: distinct values / range
    let hint;
    if (ci.type === "number")
      hint = `${fmt(ci.distinctCount)} distinct values, range ${ci.min} to ${ci.max}`;
    else if (ci.type === "date")
      hint = `${fmt(ci.distinctCount)} distinct dates, from ${ci.minText} to ${ci.maxText}`;
    else
      hint = ci.values ? `${fmt(ci.distinctCount)} distinct values` : `${fmt(ci.distinctCount)} distinct values (too many to list)`;

    row.append(colSel, opSel, valueCtl, rm, el("div", "fhint", hint));
    return row;
  }

  // ------------------------------------------------------------------ saved filters
  async function loadSaved() {
    try {
      const res = await fetch("/api/saved-filters");
      if (!res.ok) throw new Error();
      savedFilters = await res.json();
    } catch { savedFilters = []; setStatus($("savedMsg"), "Saved filters could not be loaded. Try again in a moment.", "err"); }
    renderSaved();
  }

  // readable one-liner for a saved filter tree, e.g.  Age ≥ 18 AND (Gender = Male OR Gender = Other)
  const summarize = (items, mode) => items
    .map(n => n.items ? "(" + summarize(n.items, n.mode) + ")" : `${n.column} ${OP_TEXT[n.op] || n.op} ${n.value}`)
    .join(mode === "or" ? "  OR  " : "  AND  ");

  // rebuild a saved tree for the current file, dropping filters whose column is missing / no longer fits
  function importItems(items, skipped) {
    const out = [];
    for (const n of items) {
      if (n.items) {
        const sub = importItems(n.items, skipped);
        if (sub.length) out.push({ mode: n.mode === "or" ? "or" : "and", items: sub });
      } else {
        const ci = byName[n.column];
        if (ci && OPS[ci.type].some(o => o[0] === n.op)) out.push({ column: n.column, op: n.op, value: n.value });
        else skipped.push(n.column);
      }
    }
    return out;
  }

  // groups at the top, joined by `mode`; loose filters (older saved entries) are wrapped into one group
  function toRoot(items, mode) {
    mode = mode === "or" ? "or" : "and";
    const loose = items.filter(n => !n.items), groups = items.filter(n => n.items);
    return { mode, items: [...(loose.length ? [{ mode, items: loose }] : []), ...groups] };
  }

  function renderSaved() {
    $("savedCount").textContent = savedFilters.length;
    $("savedBox").classList.remove("hidden");
    $("savedSearch").classList.toggle("hidden", savedFilters.length < 6);
    const sq = $("savedSearch").value.trim().toLowerCase();
    const shownSaved = sq ? savedFilters.filter(sf => [sf.name, sf.fileName, summarize(sf.filters, sf.mode)].some(x => String(x || "").toLowerCase().includes(sq))) : savedFilters;
    $("savedList").replaceChildren(...shownSaved.map(sf => {
      const row = el("div", "saved");
      const meta = el("div", "meta");
      meta.append(
        el("b", null, sf.name),
        el("small", null, "Used for: " + sf.fileName),
        ...(sf.columns ? [el("small", null,
          `Columns: ${sf.columns.length} selected` + (Object.keys(sf.renames || {}).length ? `, ${Object.keys(sf.renames).length} renamed` : ""))] : []),
        el("small", null, summarize(sf.filters, sf.mode)),
        el("small", null, "Saved " + new Date(sf.savedAt).toLocaleString()));
      const use = el("button", null, "Apply");
      use.onclick = () => applySaved(sf);
      const del = el("button", "x danger", "✕");
      del.title = "Delete saved filters"; del.setAttribute("aria-label", `Delete saved filter ${sf.name}`);
      del.onclick = async () => {
        if (!confirm(`Delete the saved filter "${sf.name}"?`)) return;
        try {
          const r = await fetch("/api/saved-filters/" + sf.id, { method: "DELETE" });
          if (!r.ok && r.status !== 404) throw new Error();
        } catch { return setStatus($("savedMsg"), "Could not delete the saved filter. Please try again.", "err"); }
        loadSaved();
      };
      row.append(meta, use, del);
      return row;
    }));
    if (!shownSaved.length) $("savedList").append(el("p", "empty", savedFilters.length ? "No saved filters match your search." : "No saved filters yet."));
  }

  function applySaved(sf) {
    const skipped = [];
    const items = importItems(sf.filters, skipped);
    if (items.length === 0) return setStatus($("savedMsg"), "None of these filters fit the columns of this file.", "err");

    root = toRoot(items, sf.mode);
    // saved entries remember a sheet / file label, not the workbook name: only fill the name in when it is empty
    if (!$("fileName").value.trim()) $("fileName").value = sf.fileName.replace(/\.xlsx$/i, "");
    syncSetupName();
    $("saveFilters").checked = true;
    $("saveName").value = sf.name;
    setStatus($("saveFiltersMsg"), "");

    // selected + renamed columns (entries saved before this was stored have none: leave the current choice alone)
    const colsMissing = [];
    if (sf.columns && sf.columns.length) {
      const have = new Set(columns);
      const keep = sf.columns.filter(c => have.has(c));
      colsMissing.push(...sf.columns.filter(c => !have.has(c)));
      if (keep.length) {
        restoreSelection(keep, sf.columnOrder);
        order = normOrder(sf.columnOrder || keep);              // saved columns come in their saved order
        renames = {};
        Object.entries(sf.renames || {}).forEach(([c, n]) => { if (selected.has(c) && n) renames[c] = n; });
        $("renameToggle").checked = Object.keys(renames).length > 0;
        renderColumns();
      } else colsMissing.push("(none of the saved columns exist in this file, kept the current selection)");
    }

    sorts = normSorts(sf.sorts); // legacy entries have no sorts: use normal unsorted behavior
    renderSorts(); renderFilters(); scheduleCount();
    const warn = [];
    if (skipped.length) warn.push(`skipped filters on missing/incompatible columns: ${[...new Set(skipped)].join(", ")}`);
    if (colsMissing.length) warn.push(`columns not found: ${colsMissing.join(", ")}`);
    setStatus($("savedMsg"),
      warn.length ? `Applied, but ${warn.join("; ")}` : `Applied the saved filter "${sf.name}".`,
      warn.length ? "err" : "ok");
  }

  // live "N of M rows match" (debounced; ignores out-of-date answers)
  let timer = null, reqNo = 0, countPending = false;
  function scheduleCount() { ++reqNo; countPending = true; clearTimeout(timer); refresh(); timer = setTimeout(runCount, 250); }

  async function runCount() {
    const mine = ++reqNo;
    try {
      const res = await fetch("/api/count", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ id: sessionId, filters: activeFilters(), mode: root.mode })
      });
      const data = await readJson(res);
      if (mine !== reqNo) return;
      if (!res.ok) { filterError = data.error || `Could not count the rows (HTTP ${res.status}).`; matchCount = null; }
      else { filterError = ""; matchCount = data.count; totalRows = data.total; }
    } catch (e) {
      if (mine !== reqNo) return;
      filterError = "Could not reach the server."; matchCount = null;
    }
    countPending = false; refresh();
  }

  // ------------------------------------------------------------------ column picker
  function columnRow(c) {
    const on = selected.has(c) || LOCKED.has(c);
    const label = el("label", "col" + (on ? "" : " off"));
    const cb = document.createElement("input");
    cb.type = "checkbox";
    cb.checked = on;
    cb.setAttribute("aria-label", "Include " + c);
    if (LOCKED.has(c)) cb.disabled = true;
    cb.onchange = () => { cb.checked ? selected.add(c) : selected.delete(c); renderColumns(); };
    const src = el("span", "src", c);
    if (LOCKED.has(c)) src.append(el("em", "tag", "required"));
    label.append(cb, src);
    if ($("renameToggle").checked) {
      const rn = document.createElement("input");
      rn.type = "text"; rn.className = "rn"; rn.placeholder = "Rename to…";
      rn.setAttribute("aria-label", "Output name for " + c);
      rn.value = renames[c] || "";
      rn.onclick = e => { e.stopPropagation(); e.preventDefault(); rn.focus(); };
      rn.oninput = () => { const v = rn.value.trim(); if (v) renames[c] = v; else delete renames[c]; captureSheet(); renderOrder(); schedulePreview(); };
      label.append(rn);
    } else label.append(el("span", "outname", c));
    const t = byName[c]?.type;
    label.append(el("span", "ctype", CALCULATED.has(c) ? "calculated" : t || ""));
    return label;
  }
  function renderColumns() {
    const q = $("search").value.trim().toLowerCase();
    const box = $("cols");
    box.replaceChildren();
    const groups = [
      ["Calculated columns", columns.filter(c => CALCULATED.has(c))],
      ["Original columns", columns.filter(c => !CALCULATED.has(c))]
    ];
    for (const [title, list] of groups) {
      const shown = list.filter(c => c.toLowerCase().includes(q));
      if (!shown.length) continue;
      const g = el("div", "cgroup"), head = el("div", "chead");
      const n = list.filter(c => selected.has(c) || LOCKED.has(c)).length;
      head.append(el("b", null, title), el("span", "opt", `${n} of ${list.length} selected`));
      const all = el("button", "mini", "All"), none = el("button", "mini", "None");
      all.type = none.type = "button"; all.setAttribute("aria-label", "Select all " + title.toLowerCase()); none.setAttribute("aria-label", "Select none of the " + title.toLowerCase());
      all.onclick = () => { list.forEach(c => selected.add(c)); renderColumns(); };
      none.onclick = () => { list.forEach(c => { if (!LOCKED.has(c)) selected.delete(c); }); renderColumns(); };
      head.append(all, none);
      const ch = el("div", "colhead"); ["", "Source column", "Output name", "Type"].forEach(x => ch.append(el("span", null, x)));
      g.append(head, ch, ...shown.map(columnRow));
      box.append(g);
    }
    if (!box.children.length) box.append(el("p", "empty", "  No columns match the search."));
    renderOrder(); renderSorts();
    refresh();
  }

  // ------------------------------------------------------------------ column order (drag & drop or arrows)
  let dragCol = null;
  function moveCol(c, delta) {             // move among the selected columns only
    const vis = chosenCols(), i = vis.indexOf(c), j = i + delta;
    if (i < 0 || j < 0 || j >= vis.length) return;
    const a = order.indexOf(c), b = order.indexOf(vis[j]);
    [order[a], order[b]] = [order[b], order[a]];
    renderOrder(); refresh();
  }
  function dropCol(c, target, after) {
    if (!c || c === target) return;
    order.splice(order.indexOf(c), 1);
    order.splice(order.indexOf(target) + (after ? 1 : 0), 0, c);
    renderOrder(); refresh();
  }
  function renderOrder() {
    const list = $("orderList"), vis = chosenCols();
    list.replaceChildren();
    if (!vis.length) { list.append(el("li", "empty", "Select some columns above.")); return; }
    const shownName = c => ($("renameToggle").checked && renames[c]) ? `${renames[c]}  (${c})` : c;
    vis.forEach((c, i) => {
      const li = el("li"); li.draggable = true;
      const up = el("button", "mini", "↑"), down = el("button", "mini", "↓");
      up.title = "Move up"; down.title = "Move down";
      up.setAttribute("aria-label", "Move " + c + " up"); down.setAttribute("aria-label", "Move " + c + " down");
      up.disabled = i === 0; down.disabled = i === vis.length - 1;
      up.onclick = () => moveCol(c, -1); down.onclick = () => moveCol(c, 1);
      li.append(el("span", "grip", "⋮⋮"), el("span", "nm", shownName(c)), up, down);
      li.ondragstart = e => { dragCol = c; li.classList.add("dragging"); e.dataTransfer.effectAllowed = "move"; e.dataTransfer.setData("text/plain", c); };
      li.ondragend = () => { dragCol = null; li.classList.remove("dragging"); };
      const after = e => e.offsetY > li.clientHeight / 2;
      li.ondragover = e => { if (!dragCol) return; e.preventDefault(); li.classList.toggle("over-bottom", after(e)); li.classList.toggle("over-top", !after(e)); };
      li.ondragleave = () => li.classList.remove("over-top", "over-bottom");
      li.ondrop = e => { e.preventDefault(); li.classList.remove("over-top", "over-bottom"); dropCol(dragCol, c, after(e)); };
      list.append(li);
    });
  }
  $("resetOrder").onclick = () => { order = [...columns]; renderOrder(); refresh(); };

  // ------------------------------------------------------------------ sort levels
  function renderSorts() {
    const box = $("sortList");
    box.replaceChildren();
    if (!sorts.length) box.append(el("p", "empty", "Not sorted: rows keep the order of the uploaded file."));
    sorts.forEach((sv, i) => {
      const row = el("div", "srow");
      const used = new Set(sorts.filter((_, k) => k !== i).map(x => x.column));
      const colSel = makeSelect(columns.filter(c => !used.has(c)).map(c => [c, c]), sv.column);
      const type = byName[sv.column]?.type;
      const dirSel = makeSelect(type === "number" ? [["asc", "Smallest → largest"], ["desc", "Largest → smallest"]]
        : type === "date" ? [["asc", "Oldest → newest"], ["desc", "Newest → oldest"]]
        : [["asc", "A → Z"], ["desc", "Z → A"]], sv.dir);
      colSel.onchange = () => { sv.column = colSel.value; renderSorts(); refresh(); };
      dirSel.onchange = () => { sv.dir = dirSel.value; refresh(); };
      const up = el("button", "mini", "↑"), down = el("button", "mini", "↓"), rm = el("button", "mini danger", "✕");
      up.title = "Higher priority"; down.title = "Lower priority"; rm.title = "Remove this sort";
      up.setAttribute("aria-label", "Higher priority"); down.setAttribute("aria-label", "Lower priority"); rm.setAttribute("aria-label", "Remove this sort");
      colSel.setAttribute("aria-label", (i === 0 ? "Sort by" : "Then by") + " column"); dirSel.setAttribute("aria-label", "Sort direction");
      up.disabled = i === 0; down.disabled = i === sorts.length - 1;
      up.onclick = () => { [sorts[i - 1], sorts[i]] = [sorts[i], sorts[i - 1]]; renderSorts(); refresh(); };
      down.onclick = () => { [sorts[i + 1], sorts[i]] = [sorts[i], sorts[i + 1]]; renderSorts(); refresh(); };
      rm.onclick = () => { sorts.splice(i, 1); renderSorts(); refresh(); };
      row.append(el("span", "lbl", i === 0 ? "Sort by" : "then by"), colSel, dirSel, up, down, rm);
      box.append(row);
    });
    $("addSort").disabled = sorts.length >= columns.length;
    $("clearSort").disabled = !sorts.length;
  }
  $("addSort").onclick = () => {
    const used = new Set(sorts.map(x => x.column));
    const next = chosenCols().find(c => !used.has(c)) || columns.find(c => !used.has(c));
    if (!next) return;
    sorts.push({ column: next, dir: "asc" }); renderSorts(); refresh();
  };
  $("clearSort").onclick = () => { sorts = []; renderSorts(); refresh(); };

  // ------------------------------------------------------------------ preview (first rows of the active sheet)
  let pvTimer = null, pvKey = "", pvPendingKey = "", pvNo = 0, pvResult = null;
  const previewBody = () => ({ id: sessionId, columns: chosenCols(), filters: activeFilters(), mode: root.mode, sorts,
    renames: $("renameToggle").checked ? renames : {}, limit: PREVIEW_ROWS, sheet: outputSheets[activeSheetIndex]?.name || "" });
  function schedulePreview() {
    if (!sessionId) return;
    const key = JSON.stringify(previewBody());
    updatePreviewWorkspace(key !== pvKey);
    if (key === pvKey || key === pvPendingKey) return;
    ++pvNo; pvPendingKey = ""; clearTimeout(pvTimer); pvTimer = setTimeout(runPreview, 400);
  }
  async function runPreview() {
    if (!sessionId) return;
    const key = JSON.stringify(previewBody());
    if (key === pvKey || key === pvPendingKey) return;
    pvPendingKey = key;
    const mine = ++pvNo;
    setStatus($("pvStatus"), "Loading preview…");
    try {
      const res = await fetch("/api/preview", { method: "POST", headers: { "Content-Type": "application/json" }, body: key });
      const d = await readJson(res);
      if (mine !== pvNo) return;
      if (!res.ok) throw new Error(d.error || `Preview failed (HTTP ${res.status}).`);
      pvKey = key; pvPendingKey = ""; pvResult = d;
      renderPreview(d); updatePreviewWorkspace(false);
    } catch (e) {
      if (mine !== pvNo) return;
      pvKey = ""; pvPendingKey = "";
      setStatus($("pvStatus"), "Preview could not be generated: " + e.message + (pvResult ? " The grid retains the last successful preview." : " Review the settings and try again."), "err");
      updatePreviewWorkspace(true);
    }
  }
  function renderPreview(d) {
    const box = $("pvTable");
    if (!d.rows.length) {
      box.replaceChildren();
      const empty = el("div", "pvempty", d.total === 0 && totalRows === 0 ? "This worksheet has no previewable rows." : "No rows match these filters.");
      const edit = el("button", "mini", "Edit filters"); edit.onclick = () => openPreviewPanel("filter"); empty.append(document.createTextNode(" "), edit); box.append(empty);
      return setStatus($("pvStatus"), "Showing 0 of " + fmt(d.total) + " matching rows.");
    }
    const t = el("table", "pv"), thead = el("thead"), hr = el("tr"), tb = el("tbody");
    // headers come back in the same order as the columns sent (chosenCols); map them back to know sort / rename / type
    const cols = chosenCols(), mapped = cols.length === d.headers.length;
    const corner = el("th", "rn", "#"); corner.setAttribute("aria-label", "Row"); hr.append(corner);
    const numeric = d.headers.map((h, i) => mapped && byName[cols[i]]?.type === "number");
    d.headers.forEach((h, i) => {
      const th = el("th"), src = mapped ? cols[i] : null;
      if (src) {
        const sort = el("button", "header-sort", h); sort.type = "button"; sort.title = "Toggle sort for " + src; sort.setAttribute("aria-label", "Toggle sort for " + src);
        sort.onclick = () => { const level = sorts.findIndex(x => x.column === src); if (level < 0) sorts.push({ column: src, dir: "asc" }); else if (sorts[level].dir === "asc") sorts[level].dir = "desc"; else sorts.splice(level, 1); renderSorts(); refresh(); };
        const rename = el("button", "header-rename", "✎"); rename.type = "button"; rename.title = "Rename output column " + src; rename.setAttribute("aria-label", "Rename output column " + src);
        rename.onclick = () => { openPreviewPanel("rename"); const input = [...$("cols").querySelectorAll("input.rn")].find(n => n.getAttribute("aria-label") === "Output name for " + src); input?.focus(); input?.select(); };
        th.append(sort, rename);
      } else th.textContent = h;
      th.scope = "col";
      if (src && src !== h) { th.append(el("small", null, "from " + src)); th.title = `Output name “${h}”, original column “${src}”`; }
      const si = src ? sorts.findIndex(x => x.column === src) : -1;
      if (si >= 0) {
        const s = el("span", "srt", (sorts[si].dir === "desc" ? "▼" : "▲") + (sorts.length > 1 ? si + 1 : ""));
        s.title = `Sorted ${sorts[si].dir === "desc" ? "descending" : "ascending"}` + (sorts.length > 1 ? ` (level ${si + 1})` : "");
        th.setAttribute("aria-sort", sorts[si].dir === "desc" ? "descending" : "ascending");
        th.append(s);
      }
      hr.append(th);
    });
    thead.append(hr);
    d.rows.slice(0, PREVIEW_ROWS).forEach((r, ri) => {
      const tr = el("tr"); tr.append(el("td", "rn", ri + 1));
      r.forEach((v, i) => { const td = el("td", numeric[i] ? "num" : null, v); td.title = v; tr.append(td); });
      tb.append(tr);
    });
    t.append(thead, tb);
    box.replaceChildren(t);
    const sortTxt = sorts.length ? ` Sorted by ${sorts.map(x => `${x.column} (${x.dir === "desc" ? "descending" : "ascending"})`).join(", then ")}.` : "";
    setStatus($("pvStatus"), `Showing the first ${Math.min(d.rows.length, PREVIEW_ROWS)} of ${fmt(d.total)} rows for sheet “${outputSheets[activeSheetIndex]?.name || ""}”.${sortTxt}`);
  }

  // One live set of controls: park in the hidden Preview store when closed.
  const panelSources = { filter: ["filterControls"], sort: ["sortControls"], columns: ["columnControls", "orderControls"], rename: ["columnControls", "orderControls"], sheets: ["sheetControls"], more: ["notes"] };
  const panelTitles = { filter: "Filter data", sort: "Sort rows", columns: "Columns & order", rename: "Rename output columns", sheets: "Sheets & saved setups", more: "Review & workbook actions" };
  let previewPanel = null, movedControls = [], panelTrigger = null;
  function closePreviewPanel(focus = true) {
    movedControls.forEach(({ node, home }) => {
      if (home) home.replaceWith(node); else $("pvControlStore").append(node);
    }); movedControls = [];
    $("pvPanelBody").replaceChildren(); $("pvPanel").hidden = true; previewPanel = null;
    document.querySelectorAll("[data-pv-panel]").forEach(b => b.setAttribute("aria-expanded", "false"));
    if (focus) panelTrigger?.focus();
  }
  function openPreviewPanel(name) {
    closePreviewPanel(false); previewPanel = name;
    panelTrigger = document.querySelector(`[data-pv-panel="${name}"]`);
    panelTrigger?.setAttribute("aria-expanded", "true");
    $("pvPanelTitle").textContent = panelTitles[name]; $("pvPanel").hidden = false;
    if (name === "rename") { $("renameToggle").checked = true; $("search").value = ""; renderColumns(); }
    for (const id of panelSources[name] || []) {
      const node = $(id);
      // Transformation notes keep their Upload home; configuration stays in Preview only.
      const home = id === "notes" ? document.createComment("Transformation notes home") : null;
      if (home) node.before(home);
      $("pvPanelBody").append(node); movedControls.push({ node, home });
    }
    if (name === "more") {
      const upload = el("button", null, "Upload another workbook"); upload.onclick = () => { closePreviewPanel(false); $("uploadCard").scrollIntoView({ block: "start" }); $("file").focus(); };
      $("pvPanelBody").append(upload);
      if ($("notes").classList.contains("hidden")) $("pvPanelBody").append(el("p", "empty", "No transformation changes reported for this upload."));
    }
  }
  document.querySelectorAll("[data-pv-panel]").forEach(b => b.onclick = () => previewPanel === b.dataset.pvPanel ? closePreviewPanel() : openPreviewPanel(b.dataset.pvPanel));
  $("pvClosePanel").onclick = () => closePreviewPanel();
  $("pvPanel").addEventListener("keydown", e => { if (e.key === "Escape") { e.preventDefault(); closePreviewPanel(); } });
  $("savedToggle").onclick = () => { const expanded = $("savedToggle").getAttribute("aria-expanded") !== "true"; $("savedToggle").setAttribute("aria-expanded", String(expanded)); $("savedContent").hidden = !expanded; };
  $("pvEditFilters").onclick = () => openPreviewPanel("filter");
  $("pvClearFilters").onclick = () => $("clearFilters").click();
  function updatePreviewWorkspace(pending) {
    const af = activeFilters(), summary = af.length ? summarize(af, root.mode) : "No filters applied";
    $("pvFilterSummary").textContent = summary; $("pvFilterSummary").title = summary;
    $("pvEditFilters").textContent = af.length ? "Edit filters" : "Add filter"; $("pvClearFilters").disabled = !root.items.length;
    const matching = countPending ? "Recalculating…" : filterError ? "Unavailable" : matchCount == null ? "—" : fmt(matchCount);
    const shown = pending ? "Updating…" : pvResult ? `${Math.min(pvResult.rows.length, PREVIEW_ROWS)} of ${fmt(pvResult.total)}` : "Loading…";
    $("pvCounts").replaceChildren(el("span", null, `Total rows: ${fmt(totalRows)}`), el("span", null, `Matching rows: ${matching}`), el("span", null, `Showing: ${shown}`));
  }

  // ------------------------------------------------------------------ step bar
  const STEP_LIS = [...document.querySelectorAll("#stepper li")];
  STEP_LIS.forEach(li => {
    li.onclick = () => {
      const s = $(li.dataset.target);
      if (li.dataset.target !== "previewCard") closePreviewPanel(false);
      if (!s.classList.contains("hidden")) s.scrollIntoView({ behavior: matchMedia("(prefers-reduced-motion: reduce)").matches ? "auto" : "smooth", block: "start" });
    };
    li.onkeydown = e => { if (e.key === "Enter" || e.key === " ") { e.preventDefault(); li.click(); } };
  });
  function updateStepper() {
    const secs = STEP_LIS.map(li => $(li.dataset.target));
    let cur = 0;
    secs.forEach((s, i) => { if (!s.classList.contains("hidden") && s.getBoundingClientRect().top < window.innerHeight * 0.4) cur = i; });
    if (!sessionId) cur = 0;
    STEP_LIS.forEach((li, i) => {
      const locked = secs[i].classList.contains("hidden");
      li.classList.toggle("locked", locked);
      li.tabIndex = locked ? -1 : 0; li.setAttribute("role", "link");
      if (locked) li.setAttribute("aria-disabled", "true"); else li.removeAttribute("aria-disabled");
      li.classList.toggle("current", i === cur);
      li.classList.toggle("done", !!sessionId && i < cur);
      if (i === cur) li.setAttribute("aria-current", "step"); else li.removeAttribute("aria-current");
    });
  }
  // the pinned step bar + summary change height (summary appears after upload): keep "jump to section" clear of them
  const syncStickyHeight = () => document.documentElement.style.setProperty("--sticky-h", ($("topbar").offsetHeight + 12) + "px");
  try { new ResizeObserver(syncStickyHeight).observe($("topbar")); } catch { }
  syncStickyHeight();
  window.addEventListener("scroll", updateStepper, { passive: true });
  window.addEventListener("resize", updateStepper);
  updateStepper();

  function refresh() {
    if (!restoringSheet && activeFilters().length === 0 && $("saveFilters").checked) $("saveFilters").checked = false;   // nothing to remember
    if (!restoringSheet && activeFilters().length === 0) setStatus($("saveFiltersMsg"), "");
    if (outputSheets.length && !restoringSheet) captureSheet();
    $("count").textContent = `${selected.size} of ${columns.length} selected`;
    $("filterErr").textContent = filterError;
    $("matchCount").textContent = countPending ? "Recalculating…" : matchCount == null ? "" : `${fmt(matchCount)} of ${fmt(totalRows)} rows match`;

    const hasFilters = activeFilters().length > 0;
    $("saveFilters").disabled = !hasFilters;
    $("saveFiltersBtn").disabled = !hasFilters;
    $("saveFiltersBtn").title = hasFilters ? "Save these filters (with this sheet's columns, renames and sorts) now" : "Add a filter first";
    $("saveFiltersLbl").title = hasFilters ? "Save the filters again under this name every time the workbook is exported" : "Add a filter first";
    $("dl").classList.toggle("hidden", selected.size === 0);   // export bar appears once something is selected
    const af = activeFilters();
    $("filterSummary").replaceChildren(...(af.length ? [el("b", null, "Keeping rows where: "), document.createTextNode(summarize(af, root.mode))] : []));
    updateSummary();
    $("downloadBtn").disabled = countPending || matchCount == null || matchCount === 0;
    setStatus($("dlStatus"),
      matchCount === 0 ? "Export unavailable: no rows match the filters on this sheet."
      : matchCount == null ? (filterError ? "Export unavailable: fix the filter error first." : "")
      : `${outputSheets.length > 1 ? `${outputSheets.length} sheets · this sheet: ` : ""}${fmt(matchCount)} rows × ${selected.size} columns`, matchCount === 0 || (matchCount == null && filterError) ? "err" : "");
    schedulePreview();
  }

  $("saveFilters").onchange = refresh;
  $("saveName").addEventListener("input", () => captureSheet());

  // save the active sheet's filters right away (no export needed)
  $("saveFiltersBtn").onclick = async () => {
    const msg = $("saveFiltersMsg"), af = activeFilters(), name = $("saveName").value.trim();
    if (!af.length) return setStatus(msg, "Add a filter condition first.", "err");
    if (!name) { setStatus(msg, "Enter a name for these filters.", "err"); $("saveName").focus(); return; }
    const existing = savedFilters.find(sf => sf.name.toLowerCase() === name.toLowerCase());
    if (existing && !confirm(`A saved filter named "${existing.name}" already exists. Replace it?`)) return;
    captureSheet();
    const sheet = { sheetName: outputSheets[activeSheetIndex]?.name || "", columns: chosenCols(), columnOrder: [...order], sorts: normSorts(sorts),
      filters: af, mode: root.mode, renames: $("renameToggle").checked ? renames : {} };
    $("saveFiltersBtn").disabled = true;
    try {
      let res;
      try { res = await fetch("/api/saved-filters", { method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify({ id: sessionId, name, sheet }) }); }
      catch { throw new Error("Could not reach the server. Check the connection and try again."); }
      if (!res.ok) { const d = await readJson(res); throw new Error(d.error || `Could not save the filters (HTTP ${res.status}).`); }
      setStatus(msg, `Saved filters “${name}”.`, "ok");
      loadSaved();
    } catch (e) { setStatus(msg, e.message, "err"); }
    finally { $("saveFiltersBtn").disabled = activeFilters().length === 0; }
  };
  $("search").oninput = renderColumns;
  $("renameToggle").onchange = renderColumns;
  $("allBtn").onclick  = () => { columns.forEach(c => selected.add(c)); renderColumns(); };
  $("noneBtn").onclick = () => { selected.clear(); ensureLocked(); renderColumns(); };

  // ------------------------------------------------------------------ download
  $("downloadBtn").onclick = async () => {
    $("downloadBtn").disabled = true;
    captureSheet();
    const wantSetup = $("saveSetup").checked;
    const workflowName = wantSetup ? await saveCurrentWorkflow() : null;
    setStatus($("dlStatus"), "Preparing workbook…"); setPill("processing", "Exporting…");
    try {
      const cleanItems = items => {
        const out = [];
        for (const n of items || []) {
          if (n.items) { const sub = cleanItems(n.items); if (sub.length) out.push({ mode: n.mode, items: sub }); }
          else if (n.value !== "" && n.value != null) out.push({ column: n.column, op: n.op, value: n.value });
        }
        return out;
      };
      const sheets = outputSheets.map(sh => {
        const st = sh.state || { root: { mode: "and", items: [] }, selected: columns, renames: {} };
        const rootState = st.root || { mode: "and", items: [] };
        const sel = new Set(st.selected || columns);
        const cols = normOrder(st.order).filter(c => sel.has(c) || LOCKED.has(c));
        return { sheetName: sh.name, columns: cols, columnOrder: normOrder(st.order), sorts: normSorts(st.sorts), filters: cleanItems(rootState.items), mode: rootState.mode || "and", renames: st.renameEnabled === false ? {} : (st.renames || {}), saveFilters: !!st.saveFilters && cleanItems(rootState.items).length > 0, filterName: (st.saveName || "").trim() };
      });
      const missingFilterName = sheets.find(sh => sh.saveFilters && !sh.filterName);
      if (missingFilterName) {
        restoreSheet(outputSheets.findIndex(sh => sh.name === missingFilterName.sheetName));
        setStatus($("dlStatus"), `Enter a filter name for sheet “${missingFilterName.sheetName}” or uncheck Also update on export.`, "err");
        openPreviewPanel("filter"); $("saveName").focus(); setPill("failed", "Export needs a filter name");
        return;
      }
      const res = await fetch("/api/export-multiple", {
        method: "POST", headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ id: sessionId, fileName: $("fileName").value.trim(), sheets })
      });
      if (!res.ok) {
        const d = await readJson(res);
        throw new Error(res.status === 404
          ? "Your uploaded file has expired (uploads are kept in memory, 1 hour after last use). Upload it again, then pick your setup under “Sheets & setups”."
          : (d.error || d.detail || `Export failed (HTTP ${res.status}).`));
      }
      const blob = await res.blob(), url = URL.createObjectURL(blob), a = el("a");
      const cd = res.headers.get("Content-Disposition") || "";
      const m = /filename\*?=(?:UTF-8'')?"?([^";]+)"?/i.exec(cd);
      a.href = url; a.download = safeName(m ? m[1] : ""); document.body.append(a); a.click(); a.remove(); setTimeout(() => URL.revokeObjectURL(url), 10000);
      refresh(); loadSaved(); refreshWorkflowList();
      const failed = parseInt(res.headers.get("X-Filters-Save-Failed") || "0", 10);
      const warnings = [];
      if (failed > 0) warnings.push(`${failed} filter(s) could not be remembered on the server`);
      if (wantSetup && !workflowName) warnings.push("the setup could not be saved (retry Save current setup)");
      setStatus($("dlStatus"), warnings.length ? `Downloaded, but ${warnings.join("; ")}.`
        : workflowName ? `Downloaded. Setup “${workflowName}” saved with each sheet’s filters and columns.` : "Downloaded.",
        warnings.length ? "err" : "ok");
      setPill(warnings.length ? "warn" : "done", warnings.length ? "Exported with warnings" : "Exported");
    } catch (e) {
      setStatus($("dlStatus"), e.message, "err"); setPill("failed", "Export failed");
    } finally {
      $("downloadBtn").disabled = countPending || matchCount == null || matchCount === 0;
    }
  };

  // ------------------------------------------------------------------ optional clean-up rules (cleanup.html, opened in a new tab)
  function showNotes(data) {
    const notes = data.notes || [];
    $("notesList").replaceChildren(...notes.map(n => el("li", null, n)));
    $("notes").classList.toggle("hidden", notes.length === 0);
    const q = "?id=" + encodeURIComponent(sessionId) + "&file=" + encodeURIComponent(srcFile);
    $("changesLink").href = "changes.html" + q;
    $("changesLink").parentElement.classList.toggle("hidden", !data.changeCount);
    $("cleanupLink").href = "cleanup.html" + q;
    $("cleanupState").textContent = cleanupRules.length
      ? `${cleanupRules.length} clean-up rule(s) applied.`
      : "Values are exactly as uploaded (no clean-up rules applied).";
    $("cleanupHint").classList.remove("hidden");
  }
  $("openCleanup").onclick = () => { if (sessionId) window.open($("cleanupLink").href, "_blank", "noopener"); };

  // the clean-up page changed the table: reload the column info, keep everything the user set up where it still fits
  function applySessionUpdate(data) {
    captureSheet();
    sessionVersion = data.version; cleanupRules = data.cleanup || [];
    info = data.columnInfo; byName = Object.fromEntries(info.map(c => [c.name, c]));
    columns = data.columns; totalRows = data.rowCount;
    const skipped = [];
    outputSheets.forEach(sh => { if (sh.state) sh.state = { ...sh.state, root: cleanRoot(sh.state.root, skipped) }; });
    const i = Math.max(0, activeSheetIndex); activeSheetIndex = -1; restoreSheet(i);
    showNotes(data);
    pvKey = ""; pvPendingKey = ""; ++pvNo; schedulePreview(); updateSummary();
    const gone = [...new Set(skipped)];
    setStatus($("status"), (cleanupRules.length ? `Clean-up updated: ${cleanupRules.length} rule(s) applied.` : "Clean-up removed: values are as uploaded.")
      + (gone.length ? ` Filters on removed columns were dropped: ${gone.join(", ")}.` : ""), gone.length ? "err" : "ok");
  }
  async function checkSessionVersion() {
    if (!sessionId) return;
    try {
      const res = await fetch("/api/session/" + encodeURIComponent(sessionId));
      if (!res.ok) return;
      const data = await res.json();
      if (data.id === sessionId && data.version !== sessionVersion) applySessionUpdate(data);
    } catch { }
  }
  try { new BroadcastChannel("etl-session").onmessage = e => { if (e.data?.id === sessionId) checkSessionVersion(); }; } catch { }
  document.addEventListener("visibilitychange", () => { if (document.visibilityState === "visible") checkSessionVersion(); });

  // ------------------------------------------------------------------ file picking: size info + drag & drop
  const showFileInfo = () => {
    const f = $("file").files[0];
    $("fileInfo").textContent = f ? `${f.name} · ${(f.size / 1048576).toFixed(1)} MB` : "";
  };
  $("file").onchange = showFileInfo;
  $("savedSearch").oninput = renderSaved;
  // keep the setupName box synced on programmatic fileName changes too (load setup / saved filters already call syncSetupName)

  // ------------------------------------------------------------------ light / dark theme (only the preference is stored)
  let memTheme = null;
  function applyTheme(t) {
    document.documentElement.setAttribute("data-theme", t);
    const next = t === "dark" ? "light" : "dark";
    $("themeBtn").setAttribute("aria-label", `Switch to ${next} theme`); $("themeBtn").title = `Switch to ${next} theme`;
  }
  applyTheme(document.documentElement.getAttribute("data-theme") === "dark" ? "dark" : "light");
  $("themeBtn").onclick = () => {
    const t = document.documentElement.getAttribute("data-theme") === "dark" ? "light" : "dark";
    memTheme = t; try { localStorage.setItem("etl-theme", t); } catch { /* storage blocked: keep it for this page only */ }
    applyTheme(t);
  };
  const dropCard = $("uploadCard");
  const hasFiles = e => !!e.dataTransfer && [...(e.dataTransfer.types || [])].includes("Files");
  ["dragenter", "dragover"].forEach(t => window.addEventListener(t, e => { if (!hasFiles(e)) return; e.preventDefault(); dropCard.classList.add("dropping"); }));
  window.addEventListener("dragleave", e => { if (!e.relatedTarget) dropCard.classList.remove("dropping"); });
  window.addEventListener("drop", e => {
    if (!hasFiles(e)) return;
    e.preventDefault(); dropCard.classList.remove("dropping");
    const f = e.dataTransfer.files[0]; if (!f) return;
    const dt = new DataTransfer(); dt.items.add(f); $("file").files = dt.files;
    showFileInfo();
    if (!$("uploadBtn").disabled) $("uploadBtn").click();
  });
})();
