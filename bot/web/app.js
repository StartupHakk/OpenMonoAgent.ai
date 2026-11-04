// SPDX-License-Identifier: AGPL-3.0-or-later
// The roster web view. Plain ES module, no framework. Every value from the
// server is put on the page with textContent, never as HTML.

let session = null;

async function api(path, options = {}) {
  const init = { credentials: "same-origin", headers: {}, ...options };
  if (init.body !== undefined) {
    init.method = init.method || "POST";
    init.headers["Content-Type"] = "application/json";
    init.headers["X-CSRF-Token"] = session ? session.csrf : "";
    init.body = JSON.stringify(init.body);
  }
  const res = await fetch("/api" + path, init);
  const data = await res.json().catch(() => ({}));
  if (!res.ok) throw new Error(data.error || "The roster answered " + res.status);
  return data;
}

function el(tag, props = {}, ...children) {
  const node = document.createElement(tag);
  for (const [key, value] of Object.entries(props)) {
    if (key === "text") node.textContent = value;
    else if (key === "class") node.className = value;
    else if (key.startsWith("on")) node.addEventListener(key.slice(2), value);
    else node.setAttribute(key, value);
  }
  for (const child of children) if (child) node.append(child);
  return node;
}

function table(headers, rows) {
  return el("table", {}, el("thead", {}, el("tr", {}, ...headers.map((h) => el("th", { text: h })))),
    el("tbody", {}, ...rows.map((r) => el("tr", {}, ...r.map((c) => (c instanceof Node ? el("td", {}, c) : el("td", { text: c ?? "" })))))));
}

function show(...nodes) {
  const page = document.getElementById("page");
  page.replaceChildren(...nodes);
}

function fail(error) {
  show(el("p", { class: "error", text: error.message }));
}

async function notices() {
  const box = document.getElementById("notices");
  const list = await api("/notices");
  box.replaceChildren(...list.slice(-8).map((n) => el("div", { class: "notice " + n.level },
    el("span", { text: n.text + " " }),
    session.role === "viewer" ? null : el("button", { class: "quiet", text: "Seen", onclick: async () => { await api(`/notices/${n.id}/seen`, { body: {} }); notices(); } }))));
}

const pages = {
  async bots() {
    const bots = await api("/bots");
    show(el("h2", { text: "Bots" }), table(["Name", "Title", "Role", "State", "Port", "Last active"],
      bots.map((b) => [b.name, b.title, b.role, b.enabled ? b.state : "disabled: " + (b.disabled_reason || ""), "127.0.0.1:" + b.port, b.last_active])));
  },

  async approvals() {
    const cards = await api("/approvals");
    const nodes = [el("h2", { text: "Approvals" })];
    if (cards.length === 0) nodes.push(el("p", { class: "muted", text: "Nothing is waiting for an answer." }));
    for (const c of cards) {
      const reason = el("input", { type: "text", placeholder: "Reason or answer (optional)" });
      const answer = async (decision) => {
        try {
          await api(`/approvals/${c.id}`, { body: { decision, hash: c.hash, reason: reason.value, text: reason.value } });
          pages.approvals();
        } catch (e) { alert(e.message); }
      };
      nodes.push(el("div", { class: "card" },
        el("div", {}, el("b", { text: c.bot + " " }), el("span", { class: "muted", text: `${c.kind}, expires ${new Date(c.expires_at).toLocaleTimeString()}` })),
        el("p", { text: c.summary }),
        c.action ? el("pre", { text: c.action }) : null,
        el("p", { class: "muted", text: (c.person_only ? "Only a person may answer. " : "") + (c.bound_to ? "Bound to " + c.bound_to + ". " : "") + "Requested by " + c.requester + ", origin " + c.origin }),
        session.role === "viewer" ? null : el("div", { class: "row" }, reason,
          el("button", { class: "allow", text: "Allow", onclick: () => answer("allow") }),
          el("button", { class: "deny", text: "Deny", onclick: () => answer("deny") }))));
    }
    show(...nodes);
  },

  async routines() {
    const list = await api("/routines");
    show(el("h2", { text: "Routines" }), table(["Bot", "Routine", "When", "Next", "Last", "State"],
      list.map((r) => [r.bot, r.name, r.rule, r.next ? new Date(r.next).toLocaleString() : "", r.last_status, !r.enabled ? "off" : r.paused ? "paused" : "on"])));
  },

  async rooms() {
    const rooms = await api("/rooms");
    if (rooms.length === 0) return show(el("h2", { text: "Rooms" }), el("p", { class: "muted", text: "No rooms in the team file." }));
    const pick = el("select", {}, ...rooms.map((r) => el("option", { value: r.name, text: `${r.name} (${r.responder})` })));
    const thread = el("div");
    const text = el("textarea", { rows: "3", placeholder: "Message the room. Name bots with @Name." });
    const load = async () => {
      const data = await api(`/rooms/${encodeURIComponent(pick.value)}`);
      thread.replaceChildren(...data.messages.map((m) => el("div", { class: "message" }, el("b", { text: m.author }), el("span", { class: "muted", text: m.kind + " " }), el("div", { text: m.text }))),
        ...data.turns.map((t) => el("p", { class: "muted", text: `${t.bot} is ${t.status} (hop ${t.hop})` })));
    };
    pick.addEventListener("change", load);
    const post = el("button", { text: "Post", onclick: async () => {
      try { await api(`/rooms/${encodeURIComponent(pick.value)}`, { body: { text: text.value } }); text.value = ""; load(); } catch (e) { alert(e.message); }
    } });
    show(el("h2", { text: "Rooms" }), pick, thread, session.role === "viewer" ? null : el("div", {}, text, el("div", { class: "row" }, post)));
    load();
  },

  async memory() {
    const bots = await api("/bots");
    const pick = el("select", {}, ...bots.map((b) => el("option", { value: b.slug, text: b.name })));
    const out = el("div");
    const load = async () => {
      const m = await api(`/memory/${encodeURIComponent(pick.value)}`);
      if (!m.enabled) return out.replaceChildren(el("p", { class: "muted", text: "Memory is off for this bot." }));
      out.replaceChildren(el("pre", { class: "block", text: m.block || "(empty)" }), table(["When", "Who", "File", "Why", ""], m.journal.map((j) => [j.at, j.actor, j.file, j.reason,
        j.undoable && session.role !== "viewer" ? el("button", { class: "quiet", text: "Undo", onclick: async () => {
          try { await api(`/memory/${encodeURIComponent(pick.value)}/undo`, { body: { id: j.id } }); load(); } catch (e) { alert(e.message); }
        } }) : ""])));
    };
    pick.addEventListener("change", load);
    show(el("h2", { text: "Memory" }), pick, out);
    if (bots.length) load();
  },

  async audit() {
    const rows = await api("/audit?n=200");
    show(el("h2", { text: "Audit" }), table(["When", "Who", "What", "Subject", "Detail"], rows.map((r) => [r.at, r.actor, r.action, r.subject, r.detail])));
  },
};

async function go(name) {
  for (const b of document.querySelectorAll("#nav button")) b.toggleAttribute("aria-current", b.dataset.page === name);
  if (document.querySelector(`#nav button[data-page="${name}"]`)) document.querySelector(`#nav button[data-page="${name}"]`).setAttribute("aria-current", "page");
  try { await pages[name](); await notices(); } catch (e) { fail(e); }
}

function showLogin() {
  const form = document.getElementById("login");
  form.hidden = false;
  document.querySelector("main").hidden = true;
  form.addEventListener("submit", async (event) => {
    event.preventDefault();
    const data = new FormData(form);
    const res = await fetch("/login", {
      method: "POST",
      credentials: "same-origin",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ name: data.get("name"), password: data.get("password"), code: data.get("code") }),
    });
    const body = await res.json().catch(() => ({}));
    if (res.ok) return location.reload();
    form.elements.password.value = "";
    form.elements.code.value = "";
    document.getElementById("login-error").textContent = body.error || "Sign-in failed.";
  });
}

async function start() {
  const res = await fetch("/api/session", { credentials: "same-origin" });
  const data = await res.json().catch(() => ({}));
  if (!res.ok) {
    if (data.mode && data.mode !== "local") return showLogin();
    document.getElementById("signin").hidden = false;
    document.querySelector("main").hidden = true;
    return;
  }
  session = data;
  document.getElementById("who").textContent = `${session.name} (${session.role})`;
  for (const b of document.querySelectorAll("#nav button")) b.addEventListener("click", () => go(b.dataset.page));
  document.getElementById("logout").addEventListener("click", async () => { await api("/logout", { body: {} }); location.reload(); });
  if (session.mode !== "local") {
    const all = document.getElementById("logout-all");
    all.hidden = false;
    all.addEventListener("click", async () => { await api("/logout-all", { body: {} }); location.reload(); });
  }
  go("approvals");
  setInterval(() => { const current = document.querySelector("#nav button[aria-current]"); if (current && document.visibilityState === "visible") go(current.dataset.page); }, 15000);
}

start();
