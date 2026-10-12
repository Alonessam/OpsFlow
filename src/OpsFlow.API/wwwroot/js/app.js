/* OpsFlow front-end. Vanilla JS, no build step.
 * All server data is rendered with textContent / createElement - never innerHTML - so a malicious
 * ticket title cannot inject markup (the CSP also forbids inline script as a second line of defence). */
(() => {
  'use strict';

  const STATUSES = ['New', 'Assigned', 'InProgress', 'PendingClient', 'Resolved', 'Closed'];
  const PRIORITIES = ['Critical', 'High', 'Medium', 'Low'];
  const LABEL = { InProgress: 'In progress', PendingClient: 'Pending client', TenantAdmin: 'Tenant admin' };
  const label = (v) => LABEL[v] || v;
  const FIELD = {
    AssignedToUserId: 'assignee', IsSlaBreached: 'SLA breached', ResolutionSummary: 'resolution summary',
    Title: 'title', Description: 'description', Priority: 'priority', Status: 'status'
  };
  const ACTION_LABEL = {
    Assigned: 'Assign', InProgress: 'Start / resume work', PendingClient: 'Waiting for client',
    Resolved: 'Resolve', Closed: 'Close', New: 'Unassign'
  };
  const DIALOG_IDS = ['ticket-dialog', 'new-dialog', 'action-dialog', 'admin-dialog'];

  const $ = (id) => document.getElementById(id);
  const state = {
    token: null, user: null, tenant: null,
    page: 1, pageSize: 15, total: 0, tickets: [], openId: null, ticket: null,
    connection: null, sound: false, staff: [], loggingIn: false,
  };
  // Monotonic counters: only the response of the most recent request may update the screen.
  const seq = { list: 0, detail: 0, hub: 0 };
  const timers = { search: null, refresh: null, reconnect: null };

  // ------------------------------------------------------------------ helpers
  function el(tag, props, ...kids) {
    const n = document.createElement(tag);
    for (const [k, v] of Object.entries(props || {})) {
      if (k === 'class') n.className = v;
      else if (k === 'text') n.textContent = v;
      else if (k.startsWith('on')) n.addEventListener(k.slice(2), v);
      else if (v !== false && v != null) n.setAttribute(k, v === true ? '' : v);
    }
    for (const kid of kids.flat()) if (kid != null) n.append(kid);
    return n;
  }
  const isStaff = () => state.user && state.user.role !== 'Employee';
  const isAdmin = () => state.user && state.user.role === 'TenantAdmin';

  function show(node, on) { node.hidden = !on; }
  function showError(node, text) { node.textContent = text || ''; show(node, !!text); }

  /** Turns an RFC 7807 / ValidationProblemDetails body into one readable sentence. */
  function problemText(data, status) {
    if (data && data.errors && typeof data.errors === 'object') {
      const msgs = Object.entries(data.errors).flatMap(([field, list]) => (Array.isArray(list) ? list : [list]).map((m) => (field ? field + ': ' + m : m)));
      if (msgs.length) return msgs.join(' ');
    }
    return (data && (data.detail || data.title)) || ('Request failed (' + status + ')');
  }

  async function api(method, path, body) {
    const headers = { Accept: 'application/json' };
    if (body !== undefined) headers['Content-Type'] = 'application/json';
    if (state.token) headers.Authorization = 'Bearer ' + state.token;
    const res = await fetch(path, { method, headers, body: body === undefined ? undefined : JSON.stringify(body) });
    if (res.status === 401 && state.token && path !== '/api/auth/login') {
      logout(true);
      const gone = new Error('Session expired.');
      gone.silent = true; // already explained on the login screen; no extra toast
      throw gone;
    }
    let data = null;
    const text = await res.text();
    if (text) { try { data = JSON.parse(text); } catch { /* non-JSON error body */ } }
    if (!res.ok) {
      const err = new Error(problemText(data, res.status));
      err.status = res.status; err.code = data && data.code;
      throw err;
    }
    return data;
  }

  function fmtDuration(ms) {
    const abs = Math.abs(ms);
    const m = Math.floor(abs / 60000), h = Math.floor(m / 60), d = Math.floor(h / 24);
    return d > 0 ? d + 'd ' + (h % 24) + 'h'
      : h > 0 ? h + 'h ' + String(m % 60).padStart(2, '0') + 'm'
        : m + 'm ' + String(Math.floor(abs / 1000) % 60).padStart(2, '0') + 's';
  }
  const fmtDate = (iso) => new Date(iso).toLocaleString([], { dateStyle: 'medium', timeStyle: 'short' });

  // ------------------------------------------------------------------ SLA chip (live countdown)
  function slaChip(t) {
    const chip = el('span', { class: 'badge sla' });
    chip.dataset.deadline = t.resolutionDeadline;
    chip.dataset.minutes = String(t.resolutionMinutes);
    chip.dataset.state = t.slaState;
    chip.dataset.open = String(t.status !== 'Resolved' && t.status !== 'Closed');
    chip.dataset.responded = String(!!t.firstRespondedAt);
    paintChip(chip);
    return chip;
  }

  function paintChip(chip) {
    const st = chip.dataset.state;
    let cls = st, text;
    if (st === 'Paused') text = 'Paused';
    else if (chip.dataset.open === 'false') text = st === 'Met' ? 'SLA met' : 'SLA breached';
    else {
      // Same rule as the server (Ticket.Evaluate): warn inside the last 20 % of the resolution window.
      const windowMs = Number(chip.dataset.minutes) * 60000;
      const remaining = Date.parse(chip.dataset.deadline) - Date.now();
      if (remaining < 0) { cls = 'Breached'; text = 'Over by ' + fmtDuration(remaining); }
      else if (st === 'Breached') { cls = 'Breached'; text = chip.dataset.responded === 'true' ? 'SLA breached' : 'Response overdue'; }
      else if (remaining <= windowMs * 0.2) { cls = 'Warning'; text = fmtDuration(remaining) + ' left'; }
      else { cls = 'Healthy'; text = fmtDuration(remaining) + ' left'; }
    }
    chip.className = 'badge sla sla-' + cls;
    chip.textContent = text;
  }
  setInterval(() => document.querySelectorAll('.badge.sla').forEach(paintChip), 1000);

  // ------------------------------------------------------------------ auth
  function saveSession() { try { sessionStorage.setItem('opsflow', JSON.stringify({ t: state.token, u: state.user, n: state.tenant })); } catch { /* private mode */ } }
  function loadSession() {
    try { const s = JSON.parse(sessionStorage.getItem('opsflow') || 'null'); if (s && s.t) { state.token = s.t; state.user = s.u; state.tenant = s.n; return true; } } catch { /* ignore */ }
    return false;
  }

  /** Leaves nothing of the previous session on screen: the next person may be from a different tenant. */
  function logout(expired) {
    try { sessionStorage.removeItem('opsflow'); } catch { /* ignore */ }
    seq.list++; seq.detail++; seq.hub++;
    for (const k of Object.keys(timers)) { clearTimeout(timers[k]); timers[k] = null; }
    if (state.connection) { state.connection.stop(); state.connection = null; }
    for (const id of DIALOG_IDS) { const d = $(id); if (d.open) d.close(); }
    state.token = state.user = state.tenant = null;
    Object.assign(state, { page: 1, total: 0, tickets: [], openId: null, ticket: null, staff: [] });
    $('tickets-body').replaceChildren(); $('kpis').replaceChildren(); delete $('kpis').dataset.breached;
    $('d-comments').replaceChildren(); $('d-history').replaceChildren();
    $('f-search').value = ''; $('f-status').value = ''; $('f-priority').value = '';
    $('f-breached').checked = false; $('f-mine').checked = false;
    setLive('off', 'offline');
    show($('app-view'), false); show($('login-view'), true);
    showError($('login-error'), expired ? 'Your session expired. Please sign in again.' : '');
  }

  async function login(slug, email, password) {
    if (state.loggingIn) return; // a double click must not start two sessions (and two live connections)
    state.loggingIn = true;
    try {
      const res = await api('POST', '/api/auth/login', { tenantSlug: slug, email, password });
      state.token = res.accessToken; state.user = res.user; state.tenant = res.tenant;
      saveSession();
      await enterApp();
    } finally { state.loggingIn = false; }
  }

  $('login-form').addEventListener('submit', async (e) => {
    e.preventDefault();
    showError($('login-error'), '');
    try { await login($('login-tenant').value, $('login-email').value, $('login-password').value); }
    catch (err) { if (!err.silent) showError($('login-error'), err.status === 429 ? 'Too many attempts. Wait a minute.' : err.message); }
  });
  $('logout-btn').addEventListener('click', () => logout(false));

  // ------------------------------------------------------------------ app shell
  async function enterApp() {
    try {
      $('login-password').value = '';
      $('tenant-name').textContent = state.tenant.name;
      $('who').textContent = state.user.fullName + ' - ' + label(state.user.role);
      show($('admin-btn'), isAdmin());
      show($('kpis'), isStaff());
      $('f-mine-wrap').lastChild.textContent = isStaff() ? ' Assigned to me' : ' Created by me';
      $('list-title').textContent = isStaff() ? 'Tickets' : 'My tickets';
      if (isStaff()) { try { state.staff = await api('GET', '/api/users?staffOnly=true'); } catch (err) { if (err.silent) throw err; state.staff = []; } }
      await refresh();
      // Only reveal the app once its data is loaded, so no empty (or stale) shell flashes by.
      show($('login-view'), false); show($('app-view'), true);
      connectLive();
    } catch (err) {
      if (state.token) logout(false);
      showError($('login-error'), err.silent ? $('login-error').textContent : 'Could not load your workspace: ' + err.message);
      throw Object.assign(err, { silent: true });
    }
  }

  async function refresh() {
    await Promise.all([loadTickets(), isStaff() ? loadKpis() : null]);
    if (state.openId && $('ticket-dialog').open) await loadDetail(state.openId, true);
  }

  async function loadKpis() {
    const s = await api('GET', '/api/dashboard/summary');
    const defs = [
      ['Open', s.open, ''], ['Unassigned', s.unassigned, s.unassigned ? 'warn' : ''],
      ['At risk (SLA)', s.atRisk, s.atRisk ? 'warn' : ''], ['SLA breached', s.breached, s.breached ? 'bad' : ''], ['Critical open', s.critical, s.critical ? 'bad' : ''],
    ];
    const box = $('kpis');
    const prevBreached = box.dataset.breached ? Number(box.dataset.breached) : null;
    box.replaceChildren(...defs.map(([l, n, cls]) => el('div', { class: 'kpi ' + cls + (l === 'SLA breached' && prevBreached !== null && n > prevBreached ? ' pulse' : '') },
      el('div', { class: 'n', text: String(n) }), el('div', { class: 'l', text: l }))));
    box.dataset.breached = String(s.breached);
  }

  function filters() {
    const q = new URLSearchParams({ page: state.page, pageSize: state.pageSize });
    if ($('f-status').value) q.set('status', $('f-status').value);
    if ($('f-priority').value) q.set('priority', $('f-priority').value);
    if ($('f-search').value.trim()) q.set('search', $('f-search').value.trim());
    if ($('f-breached').checked) q.set('breached', 'true');
    if ($('f-mine').checked) q.set('mine', 'true');
    return q;
  }

  async function loadTickets() {
    const mine = ++seq.list;
    const res = await api('GET', '/api/tickets?' + filters());
    if (mine !== seq.list) return; // a newer request (typing, filter, live event) superseded this one

    const pages = Math.max(1, Math.ceil(res.totalCount / state.pageSize));
    if (res.items.length === 0 && res.totalCount > 0 && state.page > pages) {
      state.page = pages; // the page we were on no longer exists: step back instead of showing "page 3 of 2"
      return loadTickets();
    }

    state.tickets = res.items; state.total = res.totalCount;
    $('tickets-body').replaceChildren(...res.items.map((t) => {
      const open = () => openTicket(t.id);
      return el('tr', { class: 'row-link', onclick: open },
        el('td', { class: 'num' }, el('button', { class: 'linkbtn', type: 'button', text: t.ticketNumber, 'aria-label': 'Open ticket ' + t.ticketNumber, onclick: (e) => { e.stopPropagation(); open(); } })),
        el('td', { text: t.title }),
        el('td', {}, el('span', { class: 'badge b-' + t.priority, text: t.priority })),
        el('td', {}, el('span', { class: 'badge s-' + t.status, text: label(t.status) })),
        el('td', { text: t.assignedTo ? t.assignedTo.fullName : '-' }),
        el('td', {}, slaChip(t)));
    }));
    show($('empty'), res.items.length === 0);
    $('page-info').textContent = 'Page ' + state.page + ' of ' + pages + ' - ' + res.totalCount + ' ticket(s)';
    $('prev').disabled = state.page <= 1; $('next').disabled = state.page >= pages;
  }

  for (const id of ['f-status', 'f-priority', 'f-breached', 'f-mine']) $(id).addEventListener('change', () => { state.page = 1; loadTickets(); });
  $('f-search').addEventListener('input', () => { clearTimeout(timers.search); timers.search = setTimeout(() => { state.page = 1; loadTickets(); }, 250); });
  $('prev').addEventListener('click', () => { if (state.page > 1) { state.page--; loadTickets(); } });
  $('next').addEventListener('click', () => { state.page++; loadTickets(); });

  // ------------------------------------------------------------------ ticket drawer
  async function openTicket(id) {
    state.openId = id;
    selectTab('comments');
    showError($('d-error'), '');
    if (!$('ticket-dialog').open) $('ticket-dialog').showModal();
    await loadDetail(id);
  }

  async function loadDetail(id, quiet) {
    const mine = ++seq.detail;
    try {
      const [t, comments, history] = await Promise.all([
        api('GET', '/api/tickets/' + id), api('GET', '/api/tickets/' + id + '/comments'), api('GET', '/api/tickets/' + id + '/history')]);
      if (mine !== seq.detail || state.openId !== id) return; // user already opened another ticket
      state.ticket = t;
      paintDetail(t, comments, history);
    } catch (err) { if (!quiet && !err.silent) showError($('d-error'), err.message); }
  }

  function paintDetail(t, comments, history) {
    $('d-number').textContent = t.ticketNumber;
    $('d-title').textContent = t.title;
    $('d-desc').textContent = t.description;
    const meta = [
      el('span', { class: 'badge b-' + t.priority, text: t.priority }),
      el('span', { class: 'badge s-' + t.status, text: label(t.status) }),
      slaChip(t),
      el('span', { class: 'kv', text: 'Requester: ' + t.createdBy.fullName }),
      el('span', { class: 'kv', text: 'Assignee: ' + (t.assignedTo ? t.assignedTo.fullName : 'unassigned') }),
      el('span', { class: 'kv', text: 'Opened ' + fmtDate(t.createdAt) }),
      el('span', { class: 'kv', text: 'Resolve by ' + fmtDate(t.resolutionDeadline) }),
    ];
    $('d-meta').replaceChildren(...meta);

    show($('d-resolution'), !!t.resolutionSummary && (t.status === 'Resolved' || t.status === 'Closed'));
    $('d-resolution').textContent = 'Resolution: ' + (t.resolutionSummary || '');

    const actions = t.allowedTransitions.map((to) => el('button', {
      class: 'btn' + (to === 'Resolved' ? ' primary' : ''), type: 'button',
      text: t.status === 'Resolved' && to === 'InProgress' && !isStaff() ? 'Not fixed - reopen' : t.status === 'Closed' ? 'Reopen' : ACTION_LABEL[to] || to,
      onclick: () => startTransition(t, to),
    }));
    if (isStaff() && ['Assigned', 'InProgress', 'PendingClient'].includes(t.status)) {
      actions.push(el('button', { class: 'btn', type: 'button', text: 'Reassign', onclick: () => startAssign(t) }));
    }
    $('d-actions').replaceChildren(...actions);

    $('d-comments').replaceChildren(...(comments.length
      ? comments.map((c) => el('li', { class: 'msg' + (c.isInternalNote ? ' internal' : '') },
        el('div', { class: 'by', text: c.author.fullName + ' - ' + fmtDate(c.createdAt) + (c.isInternalNote ? ' - internal note' : '') }),
        el('div', { class: 'body', text: c.content })))
      : [el('li', { class: 'muted', text: 'No messages yet.' })]));

    show($('internal-wrap'), isStaff());
    show($('comment-form'), t.status !== 'Closed');

    $('d-history').replaceChildren(...history.map((h) => {
      const who = h.changedBy ? h.changedBy.fullName : 'System';
      const what = h.field === 'Created'
        ? el('span', { text: 'created the ticket' })
        : el('span', {}, 'changed ', el('strong', { text: FIELD[h.field] || h.field }), ' from ', el('code', { text: h.oldValue == null ? 'empty' : trunc(h.oldValue) }), ' to ', el('code', { text: h.newValue == null ? 'empty' : trunc(h.newValue) }));
      return el('li', {}, el('div', { class: 'when', text: fmtDate(h.timestamp) }), el('div', { class: 'chg' }, el('strong', { text: who }), ' ', what));
    }));
  }
  const trunc = (s) => (s.length > 80 ? s.slice(0, 77) + '...' : s);

  function selectTab(name) {
    document.querySelectorAll('.tab').forEach((b) => {
      const on = b.dataset.tab === name;
      b.classList.toggle('active', on); b.setAttribute('aria-selected', String(on)); b.tabIndex = on ? 0 : -1;
    });
    show($('tab-comments'), name === 'comments'); show($('tab-history'), name === 'history');
  }
  document.querySelectorAll('.tab').forEach((b) => b.addEventListener('click', () => selectTab(b.dataset.tab)));
  $('d-close').addEventListener('click', () => $('ticket-dialog').close());
  $('ticket-dialog').addEventListener('close', () => { state.openId = null; seq.detail++; });

  $('comment-form').addEventListener('submit', async (e) => {
    e.preventDefault();
    showError($('d-error'), '');
    try {
      await api('POST', '/api/tickets/' + state.openId + '/comments', { content: $('comment-text').value, isInternalNote: isStaff() && $('comment-internal').checked });
      $('comment-text').value = ''; $('comment-internal').checked = false;
      await loadDetail(state.openId);
    } catch (err) { if (!err.silent) showError($('d-error'), err.message); }
  });

  // ------------------------------------------------------------------ transitions
  let pending = null;
  function openAction(title, { assignee, summary }, onConfirm) {
    $('a-title').textContent = title;
    show($('a-assignee-wrap'), !!assignee); show($('a-summary-wrap'), !!summary);
    if (assignee) {
      $('a-assignee').replaceChildren(...state.staff.map((u) => el('option', { value: u.id, text: u.fullName })));
      $('a-assignee').value = state.ticket && state.ticket.assignedTo ? state.ticket.assignedTo.id : state.user.id;
    }
    $('a-summary').value = ''; showError($('a-error'), '');
    pending = onConfirm; $('action-dialog').showModal();
  }
  $('a-cancel').addEventListener('click', () => $('action-dialog').close());
  $('action-form').addEventListener('submit', async (e) => {
    e.preventDefault();
    try { await pending(); $('action-dialog').close(); } catch (err) { if (!err.silent) showError($('a-error'), err.message); }
  });

  function startTransition(t, to) {
    showError($('d-error'), '');
    const send = async (extra) => {
      await api('POST', '/api/tickets/' + t.id + '/transitions', { to, version: t.version, ...extra });
      await refresh();
    };
    if (to === 'Assigned') openAction('Assign ticket', { assignee: true }, () => send({ assigneeId: $('a-assignee').value }));
    else if (to === 'Resolved') openAction('Resolve ticket', { summary: true }, () => send({ resolutionSummary: $('a-summary').value }));
    else send({}).catch((err) => { if (!err.silent) showError($('d-error'), err.message); refresh().catch(() => { }); });
  }
  function startAssign(t) {
    openAction('Reassign ticket', { assignee: true }, async () => {
      await api('POST', '/api/tickets/' + t.id + '/assign', { assigneeId: $('a-assignee').value, version: t.version });
      await refresh();
    });
  }

  // ------------------------------------------------------------------ new ticket
  $('new-btn').addEventListener('click', () => {
    $('n-priority').replaceChildren(...['Medium', 'Low', 'High', 'Critical'].map((p) => el('option', { value: p, text: p })));
    $('new-form').reset(); $('n-priority').value = 'Medium'; showError($('n-error'), '');
    $('new-dialog').showModal();
  });
  $('n-cancel').addEventListener('click', () => $('new-dialog').close());
  $('new-form').addEventListener('submit', async (e) => {
    e.preventDefault();
    try {
      await api('POST', '/api/tickets', { title: $('n-title').value, description: $('n-desc').value, priority: $('n-priority').value });
      $('new-dialog').close(); state.page = 1; await refresh();
    } catch (err) { if (!err.silent) showError($('n-error'), err.message); }
  });

  // ------------------------------------------------------------------ live updates (SignalR)
  function setLive(mode, text) { $('live-dot').className = 'dot ' + mode; $('live-text').textContent = text; }

  function connectLive() {
    if (!window.signalR || !state.token) return;
    const mine = ++seq.hub;
    if (state.connection) state.connection.stop();

    // Retry forever with a capped back-off: a laptop that slept or a server that restarted comes back by itself.
    const conn = new signalR.HubConnectionBuilder()
      .withUrl('/hubs/ops', { accessTokenFactory: () => state.token })
      .withAutomaticReconnect({ nextRetryDelayInMilliseconds: (ctx) => Math.min(30000, 1000 * 2 ** ctx.previousRetryCount) })
      .build();
    conn.on('ticketEvent', onTicketEvent);
    conn.onreconnecting(() => {
      setLive('wait', 'reconnecting');
      api('GET', '/api/auth/me').catch(() => { }); // an expired token ends the session here (401 -> logout) instead of retrying forever
    });
    conn.onreconnected(() => { setLive('on', 'live'); refresh().catch(() => { }); });
    conn.onclose(async () => {
      if (mine !== seq.hub || !state.token) return; // we closed it ourselves (logout / new connection)
      setLive('off', 'offline');
      try { await api('GET', '/api/auth/me'); } catch { return; } // 401 -> logout() already ran; network error -> retry below
      clearTimeout(timers.reconnect);
      timers.reconnect = setTimeout(() => { if (mine === seq.hub && state.token) connectLive(); }, 5000);
    });
    setLive('wait', 'connecting');
    conn.start().then(() => { if (mine === seq.hub) setLive('on', 'live'); }).catch(() => {
      if (mine !== seq.hub || !state.token) return;
      setLive('off', 'offline');
      clearTimeout(timers.reconnect);
      timers.reconnect = setTimeout(() => { if (mine === seq.hub && state.token) connectLive(); }, 5000);
    });
    state.connection = conn;
  }

  const EVENT_TEXT = {
    TicketCreated: ['New ticket', ''], TicketUpdated: ['Ticket updated', ''], TicketTransitioned: ['Status changed', ''],
    CommentAdded: ['New comment', ''], SlaWarning: ['SLA warning', 'warn'], SlaResponseBreached: ['First response overdue', 'bad'], SlaBreached: ['SLA breached', 'bad'],
  };
  function onTicketEvent(e) {
    const [title, cls] = EVENT_TEXT[e.type] || [e.type, ''];
    toast(title + ' - ' + e.ticketNumber, e.title + (e.detail ? ' (' + e.detail + ')' : ''), cls);
    if (cls === 'bad') beep();
    clearTimeout(timers.refresh);
    timers.refresh = setTimeout(async () => {
      try { await refresh(); markFlash(e.ticketId); } catch (err) { if (!err.silent) toast('Could not refresh', err.message, 'warn'); }
    }, 150);
  }
  function markFlash(id) {
    const idx = state.tickets.findIndex((t) => t.id === id);
    const row = idx >= 0 ? $('tickets-body').children[idx] : null;
    if (row) row.classList.add('flash');
  }

  function toast(title, detail, cls) {
    const t = el('div', { class: 'toast ' + (cls || '') }, el('div', { class: 't', text: title }), el('div', { class: 'd', text: detail }));
    $('toasts').append(t); setTimeout(() => t.remove(), 6000);
  }
  // Last line of defence: a failed background request must be visible, never a silent dead button.
  window.addEventListener('unhandledrejection', (ev) => {
    const err = ev.reason;
    if (err && err.silent) return;
    toast('Something went wrong', (err && err.message) || 'Unexpected error', 'warn');
  });

  let audio;
  function beep() {
    if (!state.sound) return;
    try {
      audio = audio || new (window.AudioContext || window.webkitAudioContext)();
      const o = audio.createOscillator(), g = audio.createGain();
      o.type = 'sine'; o.frequency.value = 880; g.gain.value = 0.06;
      o.connect(g); g.connect(audio.destination); o.start(); o.stop(audio.currentTime + 0.25);
    } catch { /* audio not available */ }
  }
  $('sound-btn').addEventListener('click', () => { state.sound = !state.sound; $('sound-btn').textContent = 'Sound: ' + (state.sound ? 'on' : 'off'); beep(); });

  // ------------------------------------------------------------------ admin
  $('admin-btn').addEventListener('click', async () => {
    $('admin-msg').textContent = ''; $('admin-dialog').showModal();
    try { await loadAdmin(); } catch (err) { if (!err.silent) $('admin-msg').textContent = err.message; }
  });
  $('admin-close').addEventListener('click', () => $('admin-dialog').close());

  async function loadAdmin() {
    const [policies, users] = await Promise.all([api('GET', '/api/sla-policies'), api('GET', '/api/users')]);
    $('sla-body').replaceChildren(...policies.map((p) => {
      const fr = el('input', { type: 'number', min: '1', value: String(p.firstResponseMinutes), 'aria-label': p.priority + ' first response minutes' });
      const rs = el('input', { type: 'number', min: '1', value: String(p.resolutionMinutes), 'aria-label': p.priority + ' resolution minutes' });
      return el('tr', {}, el('td', {}, el('span', { class: 'badge b-' + p.priority, text: p.priority })), el('td', {}, fr), el('td', {}, rs),
        el('td', {}, el('button', { class: 'btn', type: 'button', text: 'Save', onclick: async () => {
          try { await api('PUT', '/api/sla-policies/' + p.priority, { firstResponseMinutes: Number(fr.value), resolutionMinutes: Number(rs.value) }); $('admin-msg').textContent = p.priority + ' policy saved.'; }
          catch (err) { if (!err.silent) $('admin-msg').textContent = err.message; }
        } })));
    }));
    $('user-list').replaceChildren(...users.map((u) => el('li', {},
      el('span', {}, el('strong', { text: u.fullName }), ' ', el('span', { class: 'muted small', text: u.email + ' - ' + label(u.role) + (u.isActive ? '' : ' - inactive') })),
      u.id === state.user.id ? el('span') : el('button', { class: 'btn' + (u.isActive ? ' danger' : ''), type: 'button', text: u.isActive ? 'Deactivate' : 'Activate', onclick: async () => {
        try { await api('PATCH', '/api/users/' + u.id + '/status', { isActive: !u.isActive }); await loadAdmin(); } catch (err) { if (!err.silent) $('admin-msg').textContent = err.message; }
      } }))));
  }
  $('user-form').addEventListener('submit', async (e) => {
    e.preventDefault();
    try {
      await api('POST', '/api/users', { fullName: $('u-name').value, email: $('u-email').value, role: $('u-role').value, password: $('u-pass').value });
      $('user-form').reset(); $('admin-msg').textContent = 'User added.'; state.staff = await api('GET', '/api/users?staffOnly=true'); await loadAdmin();
    } catch (err) { if (!err.silent) $('admin-msg').textContent = err.message; }
  });

  // ------------------------------------------------------------------ boot
  async function boot() {
    $('f-status').append(...STATUSES.map((s) => el('option', { value: s, text: label(s) })));
    $('f-priority').append(...PRIORITIES.map((p) => el('option', { value: p, text: p })));
    selectTab('comments');

    try {
      const meta = await fetch('/api/meta').then((r) => (r.ok ? r.json() : null));
      if (meta && meta.demo) {
        show($('demo-box'), true);
        const demo = [['acme', 'admin@acme.test', 'Acme admin'], ['acme', 'agent1@acme.test', 'Acme agent'], ['acme', 'employee1@acme.test', 'Acme employee'], ['globex', 'agent1@globex.test', 'Globex agent']];
        $('demo-chips').replaceChildren(...demo.map(([slug, email, text]) => el('button', { class: 'chip', type: 'button', text,
          onclick: () => login(slug, email, meta.demoPassword).catch((err) => { if (!err.silent) showError($('login-error'), err.message); }) })));
      }
    } catch { /* meta is optional */ }

    if (loadSession()) {
      try { await api('GET', '/api/auth/me'); await enterApp(); return; } catch { if (state.token) logout(false); }
    }
    show($('login-view'), true);
  }
  boot();
})();

