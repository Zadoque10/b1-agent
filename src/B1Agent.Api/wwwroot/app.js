(() => {
  "use strict";

  const $ = (id) => document.getElementById(id);

  const PROMPTS = [
    { tag: "FINANCE", q: "Which customers have overdue invoices?" },
    { tag: "CREDIT", q: "Is Microchips over its credit limit?" },
    { tag: "STOCK", q: "Can we ship 50 units of A00001 today?" },
    { tag: "SALES", q: "Show the open orders for Parameter Technology." },
    { tag: "STOCK", q: "Which printers do we have available in warehouse 02?" },
    { tag: "CUSTOMER", q: "Give me a quick account summary for Norm Thompson." }
  ];

  const state = { history: [], busy: false, featured: 0, health: null, tools: [] };

  // ------------------------------------------------------------ helpers
  const esc = (s) => String(s).replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));

  function inline(s) {
    return s
      .replace(/`([^`]+)`/g, "<code>$1</code>")
      .replace(/\*\*([^*]+)\*\*/g, "<strong>$1</strong>");
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
        out.push("<table><thead><tr>" + cells(head).map((c) => `<th>${c}</th>`).join("") + "</tr></thead><tbody>" +
          rest.map((r) => "<tr>" + cells(r).map((c) => `<td>${c}</td>`).join("") + "</tr>").join("") + "</tbody></table>");
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
      return Object.entries(obj).map(([k, v]) => `${k}: ${typeof v === "string" ? `"${v}"` : v}`).join(", ");
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
      const source = h.sapMode === "Demo" ? "Demo data" : "Service Layer";
      $("statusText").textContent = h.llmConfigured ? `${source} · ${h.model}` : `${source} · no LLM`;
      pill.classList.add(h.llmConfigured ? "ok" : "warn");
      $("sapMode").textContent = source;
      $("llmState").textContent = h.llmConfigured ? h.model : "Not configured";
      $("modelName").textContent = h.llmConfigured ? h.model : "Not configured";
      $("traceSource").textContent = source;
      $("refPill").textContent = h.sapMode === "Demo" ? "REF · DEMO COMPANY" : "REF · SERVICE LAYER";
      document.querySelectorAll("#modeChips .chip").forEach((c) => c.classList.toggle("on", c.dataset.mode === h.sapMode));
      $("setup").hidden = h.llmConfigured;
    } catch {
      $("statusText").textContent = "API unreachable";
      pill.classList.add("warn");
    }
  }

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
  $("reset").addEventListener("click", () => {
    state.history = [];
    thread.innerHTML = `<div class="empty" id="empty"><p class="serif-note">Start with a question, or pick one from the gallery above.</p></div>`;
    highlightTools([]);
  });

  // ------------------------------------------------------------ boot
  revealTitle($("title"));
  setFeatured(0);
  onScroll();
  loadHealth();
  loadTools();
})();
