(() => {
  "use strict";

  const $ = (id) => document.getElementById(id);

  const PROMPTS = [
    { tag: "COLLECTIONS", q: "Who should I call first today about overdue invoices?" },
    { tag: "CREDIT", q: "Can we accept a new order of $8,000 from Norm Thompson?" },
    { tag: "DELIVERY", q: "When can we ship 15 units of A00003?" },
    { tag: "PURCHASING", q: "What should I reorder this week, and how much?" },
    { tag: "QUOTATION", q: "Prepare a quotation for Parameter Technology: 2 x A00001 and 20 x A00005." },
    { tag: "PRICING", q: "What price does Parameter Technology pay for the Rainbow Color Printer 7.5?" }
  ];

  const state = { history: [], busy: false, featured: 0, health: null, tools: [] };

  // ------------------------------------------------------------ helpers
  const esc = (s) => String(s).replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));

  function inline(s) {
    return s
      .replace(/`([^`]+)`/g, "<code>$1</code>")
      .replace(/\*\*([^*]+)\*\*/g, "<strong>$1</strong>")
      .replace(/(^|[\s(])\*([^*\s][^*]*)\*(?=[\s.,;:!?)]|$)/g, "$1<i>$2</i>");
  }

  // Small, safe Markdown subset: paragraphs, lists, tables, bold, inline code. Input is escaped first.
  function markdown(text) {
    const lines = esc(text || "").split(/\r?\n/);
    const out = [];
    let i = 0;
    while (i < lines.length) {
      const line = lines[i];
      if (/^\s*\|.*\|\s*$/.test(line)) {
        const rows = [];
        while (i < lines.length && /^\s*\|.*\|\s*$/.test(lines[i])) rows.push(lines[i++]);
        const cells = (r) => r.trim().replace(/^\||\|$/g, "").split("|").map((c) => inline(c.trim()));
        const body = rows.filter((r) => !/^\s*\|[\s:|-]+\|\s*$/.test(r));
        const [head, ...rest] = body;
        out.push("<div class=\"table-wrap\"><table><thead><tr>" + cells(head).map((c) => `<th>${c}</th>`).join("") + "</tr></thead><tbody>" +
          rest.map((r) => "<tr>" + cells(r).map((c) => `<td>${c}</td>`).join("") + "</tr>").join("") + "</tbody></table></div>");
        continue;
      }
      if (/^\s*([-*]|\d+\.)\s+/.test(line)) {
        const ordered = /^\s*\d+\./.test(line);
        const items = [];
        while (i < lines.length && /^\s*([-*]|\d+\.)\s+/.test(lines[i])) items.push(lines[i++].replace(/^\s*([-*]|\d+\.)\s+/, ""));
        const tag = ordered ? "ol" : "ul";
        out.push(`<${tag}>` + items.map((t) => `<li>${inline(t)}</li>`).join("") + `</${tag}>`);
        continue;
      }
      if (line.trim() === "") { i++; continue; }
      const para = [];
      while (i < lines.length && lines[i].trim() !== "" && !/^\s*(\||[-*]\s|\d+\.\s)/.test(lines[i])) para.push(lines[i++].replace(/^#+\s*/, ""));
      out.push(`<p>${inline(para.join("<br>"))}</p>`);
    }
    return out.join("");
  }

  const formatArgs = (json) => {
    try {
      const obj = JSON.parse(json || "{}");
      const show = (v) => {
        if (typeof v === "string") return `"${v}"`;
        if (Array.isArray(v) && v.every((x) => x && typeof x === "object" && "itemCode" in x && "quantity" in x))
          return v.map((x) => `${x.quantity}×${x.itemCode}`).join(" + ");
        return typeof v === "object" && v !== null ? JSON.stringify(v) : v;
      };
      return Object.entries(obj).map(([k, v]) => `${k}: ${show(v)}`).join(", ");
    } catch { return json; }
  };

  // ------------------------------------------------------------ title reveal
  function revealTitle(el) {
    let delay = 0;
    const walk = (node) => {
      [...node.childNodes].forEach((child) => {
        if (child.nodeType === Node.TEXT_NODE) {
          // Letters animate one by one, but each word stays in a no-wrap box so lines only break between words.
          const frag = document.createDocumentFragment();
          child.textContent.split(/( )/).forEach((part) => {
            if (part === "") return;
            if (part === " ") { const sp = document.createElement("span"); sp.className = "sp"; frag.appendChild(sp); return; }
            const word = document.createElement("span");
            word.className = "word";
            for (const ch of part) {
              const span = document.createElement("span");
              span.className = "ch"; span.textContent = ch; span.style.animationDelay = `${delay}ms`; delay += 38;
              word.appendChild(span);
            }
            frag.appendChild(word);
          });
          child.replaceWith(frag);
        } else if (child.nodeType === Node.ELEMENT_NODE) {
          walk(child);
        }
      });
    };
    walk(el);
  }

  // ------------------------------------------------------------ counters
  function countUp(el) {
    const target = Number(el.dataset.count);
    if (target === 0) { el.textContent = "0"; return; }
    const start = performance.now();
    const step = (now) => {
      const t = Math.min(1, (now - start) / 1400);
      el.textContent = Math.round(target * (1 - Math.pow(1 - t, 3)));
      if (t < 1) requestAnimationFrame(step);
    };
    requestAnimationFrame(step);
  }

  const io = new IntersectionObserver((entries) => {
    entries.forEach((e) => { if (e.isIntersecting) { countUp(e.target); io.unobserve(e.target); } });
  }, { threshold: 0.6 });
  document.querySelectorAll("[data-count]").forEach((el) => io.observe(el));

  // ------------------------------------------------------------ scroll progress
  const progress = $("progress");
  const onScroll = () => {
    const max = document.documentElement.scrollHeight - innerHeight;
    progress.style.width = `${max > 0 ? (scrollY / max) * 100 : 0}%`;
  };
  addEventListener("scroll", onScroll, { passive: true });

  // ------------------------------------------------------------ gallery
  const strip = $("strip");
  PROMPTS.forEach((p, idx) => {
    const b = document.createElement("button");
    b.className = "thumb";
    b.type = "button";
    b.setAttribute("role", "listitem");
    b.innerHTML = `<p class="eyebrow gold">${p.tag}</p><p>${esc(p.q)}</p>`;
    b.addEventListener("click", () => { setFeatured(idx); ask(p.q); });
    b.addEventListener("mouseenter", () => setFeatured(idx));
    strip.appendChild(b);
  });

  function setFeatured(idx) {
    state.featured = (idx + PROMPTS.length) % PROMPTS.length;
    const p = PROMPTS[state.featured];
    const q = $("featuredQ");
    q.classList.add("out");
    setTimeout(() => {
      q.textContent = p.q;
      $("featuredTag").textContent = p.tag;
      q.classList.remove("out");
    }, 220);
    $("counter").textContent = `${String(state.featured + 1).padStart(2, "0")} / ${String(PROMPTS.length).padStart(2, "0")}`;
    [...strip.children].forEach((c, i) => c.classList.toggle("active", i === state.featured));
  }

  $("prev").addEventListener("click", () => setFeatured(state.featured - 1));
  $("next").addEventListener("click", () => setFeatured(state.featured + 1));
  $("featuredAsk").addEventListener("click", () => ask(PROMPTS[state.featured].q));

  let auto = setInterval(() => setFeatured(state.featured + 1), 6000);
  document.querySelector(".gallery").addEventListener("mouseenter", () => clearInterval(auto));
  document.querySelector(".gallery").addEventListener("mouseleave", () => { auto = setInterval(() => setFeatured(state.featured + 1), 6000); });

  // ------------------------------------------------------------ health + tools
  async function loadHealth() {
    const pill = $("statusPill");
    try {
      const h = await (await fetch("/api/health")).json();
      state.health = h;
      const source = h.sapMode === "Demo" ? "Demo data" : h.sapLabel;
      $("statusText").textContent = h.llmConfigured ? `${source} · ${h.model}` : `${source} · no LLM`;
      pill.classList.remove("ok", "warn", "customer");
      pill.classList.add(h.llmConfigured ? "ok" : "warn");
      if (h.sapMode === "Customer") pill.classList.add("customer");
      $("sapMode").textContent = source;
      $("llmState").textContent = h.llmConfigured ? h.model : "Not configured";
      $("modelName").textContent = h.llmConfigured ? h.model : "Not configured";
      $("traceSource").textContent = source;
      $("refPill").textContent = h.sapMode === "Demo" ? "REF · DEMO COMPANY" : `REF · ${h.sapLabel.split(" on ")[0]}`.toUpperCase();
      document.querySelectorAll("#modeChips .chip").forEach((c) =>
        c.classList.toggle("on", c.dataset.mode === (h.sapMode === "Demo" ? "Demo" : "ServiceLayer")));
      $("setup").hidden = h.llmConfigured;
    } catch {
      $("statusText").textContent = "API unreachable";
      pill.classList.add("warn");
    }
  }

  // ------------------------------------------------------------ connection drawer
  const drawer = $("drawer");
  const backdrop = $("drawerBackdrop");
  const form = $("connectForm");

  function openDrawer() {
    backdrop.hidden = false;
    drawer.classList.add("open");
    drawer.setAttribute("aria-hidden", "false");
    setTimeout(() => (form.hidden ? $("disconnect") : form.elements.baseUrl).focus(), 300);
  }
  function closeDrawer() {
    drawer.classList.remove("open");
    drawer.setAttribute("aria-hidden", "true");
    backdrop.hidden = true;
  }
  $("openConnect").addEventListener("click", openDrawer);
  $("closeConnect").addEventListener("click", closeDrawer);
  backdrop.addEventListener("click", closeDrawer);
  addEventListener("keydown", (e) => { if (e.key === "Escape" && drawer.classList.contains("open")) closeDrawer(); });

  function showConnection(c) {
    const customer = c.kind === "Customer";
    $("connectedBox").hidden = !customer;
    form.hidden = customer;
    $("openConnect").hidden = !c.connectionsEnabled && !customer;
    $("openConnect").textContent = customer ? "Change data source" : "Connect your Service Layer";
    $("sapWrites").textContent = c.writesAllowed ? (c.kind === "Demo" ? "Demo only" : "After you confirm") : "Read-only";
    if (customer) {
      $("connectedLabel").textContent = c.companyDB;
      $("connectedSub").textContent = `${c.host} · ${c.writesAllowed ? "quotations allowed after confirmation" : "read-only"}`;
    }
  }

  async function loadConnection() {
    try { showConnection(await (await fetch("/api/connection")).json()); } catch { /* keep defaults */ }
  }

  async function dataSourceChanged() {
    resetConversation();
    await Promise.all([loadHealth(), loadConnection(), loadBrief()]);
  }

  form.addEventListener("submit", async (e) => {
    e.preventDefault();
    const err = $("connectError");
    const submit = $("connectSubmit");
    err.hidden = true;
    submit.disabled = true;
    submit.textContent = "Testing the connection…";
    const f = form.elements;
    try {
      const res = await fetch("/api/connection", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          baseUrl: f.baseUrl.value, companyDB: f.companyDB.value, userName: f.userName.value, password: f.password.value,
          allowUntrustedCertificate: f.allowUntrustedCertificate.checked, allowWrites: f.allowWrites.checked
        })
      });
      const body = await res.json().catch(() => ({}));
      if (!res.ok) {
        err.textContent = res.status === 429 ? "Too many attempts. Wait a minute and try again." : (body.detail || "Could not connect.");
        err.hidden = false;
        return;
      }
      f.password.value = "";
      await dataSourceChanged();
    } catch {
      err.textContent = "Could not reach this server.";
      err.hidden = false;
    } finally {
      submit.disabled = false;
      submit.textContent = "Test and connect";
    }
  });

  $("disconnect").addEventListener("click", async () => {
    await fetch("/api/connection", { method: "DELETE" }).catch(() => {});
    await dataSourceChanged();
  });

  async function loadTools() {
    try {
      state.tools = await (await fetch("/api/tools")).json();
      $("toolList").innerHTML = state.tools.map((t) => `<li data-tool="${esc(t.name)}" title="${esc(t.description)}">${esc(t.name)}</li>`).join("");
    } catch { /* console stays empty */ }
  }

  function highlightTools(calls) {
    const used = new Set(calls.map((c) => c.name));
    document.querySelectorAll("#toolList li").forEach((li) => li.classList.toggle("used", used.has(li.dataset.tool)));
  }

  // ------------------------------------------------------------ trace
  function renderTrace(question, reply) {
    const calls = reply.toolCalls || [];
    const steps = [
      ["QUESTION", esc(question)],
      ["MODEL", calls.length ? `Decided it needed ${calls.length} tool call${calls.length > 1 ? "s" : ""}.` : "Answered without calling SAP."],
      ...calls.map((c) => ["TOOL CALL", `<code>${esc(c.name)}</code> ${esc(formatArgs(c.arguments))}`]),
      ["ANSWER", "Written from the tool results only."]
    ];
    $("timeline").innerHTML = steps.map(([l, b], i) =>
      `<li class="live" style="animation-delay:${i * 110}ms"><span class="t-label">${l}</span><span class="t-body">${b}</span></li>`).join("");

    const count = $("callCount");
    const target = calls.length;
    let n = 0;
    count.textContent = "0";
    const tick = setInterval(() => { if (n >= target) return clearInterval(tick); count.textContent = ++n; }, 120);
    $("callNote").textContent = target ? [...new Set(calls.map((c) => c.name))].join(" · ") : "No SAP data was needed for this one.";
  }


  // ------------------------------------------------------------ money
  const money = (v, currency = "USD") => {
    try { return new Intl.NumberFormat("en-US", { style: "currency", currency, maximumFractionDigits: 0 }).format(v); }
    catch { return `${currency} ${Math.round(v).toLocaleString("en-US")}`; }
  };
  const money2 = (v, currency = "USD") => {
    try { return new Intl.NumberFormat("en-US", { style: "currency", currency }).format(v); }
    catch { return `${currency} ${Number(v).toFixed(2)}`; }
  };
  const qty = (v) => Number(v).toLocaleString("en-US", { maximumFractionDigits: 2 });

  // ------------------------------------------------------------ daily brief
  const BUCKETS = [
    ["notYetDue", "Not yet due", "#d8cbb5"],
    ["days1To30", "1–30 days", "#c9a27a"],
    ["days31To60", "31–60 days", "#a07c56"],
    ["days61To90", "61–90 days", "#7c5e40"],
    ["over90", "Over 90", "#3a2a1a"]
  ];

  const listItems = (items, empty) => items.length ? items.join("") : `<li class="none">${empty}</li>`;

  async function loadBrief() {
    try {
      const b = await (await fetch("/api/brief")).json();
      const date = new Date(b.date + "T12:00:00");
      $("briefDate").textContent = date.toLocaleDateString("en-US", { weekday: "long", month: "long", day: "numeric" });
      $("agingOverdue").textContent = money(b.receivablesOverdue);
      $("agingSub").textContent = `${b.overdueInvoices} overdue invoice${b.overdueInvoices === 1 ? "" : "s"} · ${money(b.receivablesOpen)} open in total`;

      const total = b.receivablesOpen || 1;
      $("agingBar").innerHTML = BUCKETS.map(([k, , c]) => `<span data-w="${(b.receivablesByAge[k] / total) * 100}" style="background:${c}"></span>`).join("");
      requestAnimationFrame(() => setTimeout(() => $("agingBar").querySelectorAll("span").forEach((s) => (s.style.width = `${s.dataset.w}%`)), 60));
      $("agingLegend").innerHTML = BUCKETS.map(([k, label, c]) => `<li><i style="background:${c}"></i>${label}<b>${money(b.receivablesByAge[k])}</b></li>`).join("");

      $("lateCount").textContent = b.lateOrders;
      $("lateList").innerHTML = listItems(b.lateOrderList.map((o) =>
        `<li><span>#${o.docNum} · ${esc(o.cardName)}</span><span>${o.daysOverdue}d late</span></li>`), "Nothing is late.");

      $("reorderCount").textContent = b.itemsBelowMinimum;
      $("reorderList").innerHTML = listItems(b.topReorders.map((r) =>
        `<li><span>${esc(r.itemCode)} · whs ${esc(r.warehouseCode)}</span><span>buy ${qty(r.suggestedQuantity)}</span></li>`), "Stock is above minimum.");

      $("creditCount").textContent = b.customersOverLimit.length;
      $("creditList").innerHTML = listItems(b.customersOverLimit.map((c) =>
        `<li><span>${esc(c.cardName)}</span><span>+${money(c.overBy)}</span></li>`), "Everyone is within limit.");
    } catch {
      $("agingSub").textContent = "Could not load the brief.";
    }
  }

  document.querySelectorAll("[data-ask]").forEach((b) => b.addEventListener("click", () => ask(b.dataset.ask)));

  // ------------------------------------------------------------ quotation proposal
  function shipBadge(line) {
    if (line.canShipNow) return `<span class="ship now">ships now</span>`;
    if (line.fullQuantityDate) return `<span class="ship later">from ${line.fullQuantityDate}</span>`;
    return `<span class="ship short">short</span>`;
  }

  function renderProposal(p) {
    const el = document.createElement("div");
    el.className = "proposal";
    const expires = new Date(p.expiresAt);
    el.innerHTML = `
      <div class="proposal-head">
        <div>
          <p class="eyebrow gold">SALES QUOTATION · AWAITING YOUR CONFIRMATION</p>
          <h4>${esc(p.cardName)}</h4>
          <span class="sub">${esc(p.cardCode)} · valid until ${p.validUntil}</span>
        </div>
        <span class="verdict ${esc(p.creditVerdict)}">CREDIT · ${esc(p.creditVerdict).toUpperCase()}</span>
      </div>
      <div class="table-wrap"><table>
        <thead><tr><th>ITEM</th><th class="num">QTY</th><th class="num">UNIT PRICE</th><th class="num">TOTAL</th><th>STOCK</th></tr></thead>
        <tbody>${p.lines.map((l) => `<tr>
          <td>${esc(l.itemCode)} · ${esc(l.itemName)}</td>
          <td class="num">${qty(l.quantity)}</td>
          <td class="num">${money2(l.unitPrice, l.currency)}</td>
          <td class="num">${money2(l.lineTotal, l.currency)}</td>
          <td>${shipBadge(l)}</td></tr>`).join("")}</tbody>
      </table></div>
      <div class="proposal-total"><span>TOTAL BEFORE TAX</span><strong>${money2(p.total, p.currency)}</strong></div>
      ${p.creditReasons.length ? `<ul class="reasons">${p.creditReasons.map((r) => `<li>${esc(r)}</li>`).join("")}</ul>` : ""}
      <p class="proposal-target">${p.writesAllowed ? "Will be created in" : "Read-only connection to"} <b>${esc(p.target)}</b>${p.writesAllowed ? "" : ". Reconnect with “Allow creating sales quotations” to create it."}</p>
      <div class="proposal-actions">
        <button class="btn-green" type="button" data-act="confirm"${p.writesAllowed ? "" : " disabled"}>Create in SAP B1</button>
        <button class="btn-outline" type="button" data-act="cancel">Discard</button>
      </div>
      <p class="proposal-foot">Nothing has been created yet. Prices from price list ${p.lines[0].priceList}. This proposal expires at ${expires.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })}.</p>`;

    const buttons = el.querySelectorAll("[data-act]");
    const finish = (html, err) => {
      buttons.forEach((b) => b.remove());
      el.querySelector(".proposal-foot")?.remove();
      el.classList.add("closed");
      el.insertAdjacentHTML("beforeend", `<div class="proposal-done${err ? " err" : ""}">${html}</div>`);
    };

    buttons.forEach((btn) => btn.addEventListener("click", async () => {
      buttons.forEach((b) => (b.disabled = true));
      const action = btn.dataset.act;
      try {
        const res = await fetch(`/api/actions/${encodeURIComponent(p.actionId)}/${action}`, { method: "POST" });
        const body = await res.json().catch(() => ({}));
        if (res.status === 502) {
          // SAP rejected the document; the proposal is still pending, so the user can retry.
          el.querySelector(".proposal-done.err")?.remove();
          el.insertAdjacentHTML("beforeend", `<div class="proposal-done err">${esc(body.detail || "SAP rejected the quotation.")}</div>`);
          buttons.forEach((b) => (b.disabled = false));
          return;
        }
        if (!res.ok) { finish(esc(body.detail || "This proposal can no longer be used."), true); return; }
        if (action === "confirm") {
          finish(`Created in SAP B1 as sales quotation <strong>#${body.docNum}</strong>.`);
          state.history.push({ role: "assistant", content: `(The user confirmed the proposal. Sales quotation ${body.docNum} was created in SAP B1.)` });
        } else {
          finish("Discarded. Nothing was created in SAP.");
          state.history.push({ role: "assistant", content: "(The user discarded the quotation proposal.)" });
        }
      } catch {
        buttons.forEach((b) => (b.disabled = false));
      }
    }));
    return el;
  }

  // ------------------------------------------------------------ chat
  const thread = $("thread");
  const input = $("input");
  const send = $("send");

  function addMessage(kind, html) {
    $("empty")?.remove();
    const el = document.createElement("div");
    el.className = `msg ${kind}`;
    const who = kind === "user" ? "YOU" : kind === "agent" ? "B1 AGENT" : "NOTICE";
    el.innerHTML = `<div class="who">${who}</div><div class="body">${html}</div>`;
    thread.appendChild(el);
    return el;
  }

  async function ask(text) {
    if (state.busy || !text.trim()) return;
    state.busy = true;
    send.disabled = true;
    document.getElementById("assistant").scrollIntoView({ block: "start" });

    state.history.push({ role: "user", content: text });
    addMessage("user", esc(text));
    const pending = addMessage("agent", `<span class="thinking"><i></i><i></i><i></i></span>`);
    pending.scrollIntoView({ block: "nearest", behavior: "smooth" });

    try {
      const res = await fetch("/api/chat", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ messages: state.history })
      });
      const body = await res.json().catch(() => ({}));
      pending.remove();

      if (!res.ok) {
        state.history.pop();
        addMessage("error", esc(body.detail || body.title || `Request failed (${res.status}).`));
        return;
      }

      state.history.push({ role: "assistant", content: body.reply });
      const calls = body.toolCalls || [];
      const chips = calls.length
        ? `<div class="calls">${calls.map((c) => `<span class="call"><b>${esc(c.name)}</b> ${esc(formatArgs(c.arguments))}</span>`).join("")}</div>`
        : "";
      const el = addMessage("agent", markdown(body.reply) + chips);
      (body.pendingActions || []).forEach((p) => el.appendChild(renderProposal(p)));
      el.scrollIntoView({ block: "nearest", behavior: "smooth" });
      highlightTools(calls);
      renderTrace(text, body);
    } catch {
      pending.remove();
      state.history.pop();
      addMessage("error", "Could not reach the API.");
    } finally {
      state.busy = false;
      send.disabled = false;
      input.focus({ preventScroll: true });
    }
  }

  $("form").addEventListener("submit", (e) => {
    e.preventDefault();
    const text = input.value.trim();
    if (!text) return;
    input.value = "";
    input.style.height = "";
    ask(text);
  });

  input.addEventListener("keydown", (e) => {
    if (e.key === "Enter" && !e.shiftKey) { e.preventDefault(); $("form").requestSubmit(); }
  });
  input.addEventListener("input", () => { input.style.height = "auto"; input.style.height = `${input.scrollHeight}px`; });

  $("surprise").addEventListener("click", () => ask(PROMPTS[Math.floor(Math.random() * PROMPTS.length)].q));
  function resetConversation() {
    state.history = [];
    thread.innerHTML = `<div class="empty" id="empty"><p class="serif-note">Start with a question, or pick one from the gallery above.</p></div>`;
    highlightTools([]);
  }
  $("reset").addEventListener("click", resetConversation);

  // ------------------------------------------------------------ boot
  revealTitle($("title"));
  setFeatured(0);
  onScroll();
  loadHealth();
  loadConnection();
  loadTools();
  loadBrief();
})();
