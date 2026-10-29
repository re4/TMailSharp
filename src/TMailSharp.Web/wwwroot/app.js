const $ = (selector, root = document) => root.querySelector(selector);
const $$ = (selector, root = document) => [...root.querySelectorAll(selector)];
const state = { mailboxes: [], domains: [], messages: [], activeId: null, detail: null, config: null, loading: false, version: 0 };
let toastTimer;
let preferredId;
let csrfToken = document.querySelector('meta[name="csrf-token"]')?.content;
try {
  preferredId = localStorage.getItem('tmailsharp.active');
} catch { /* Storage may be unavailable in private browser configurations. */ }

export async function api(path, options = {}) {
  const headers = { 'X-TMail-Request': '1', ...options.headers };
  if (csrfToken) headers['X-CSRF-TOKEN'] = csrfToken;
  if (options.body !== undefined) headers['Content-Type'] = 'application/json';
  const response = await fetch(`/inbox${path}`, { credentials: 'same-origin', cache: 'no-store', ...options, headers,
    body: options.body === undefined ? undefined : JSON.stringify(options.body), signal: options.signal ?? AbortSignal.timeout(15000) });
  if (response.status === 204) return null;
  let result;
  try { result = await response.json(); } catch { result = {}; }
  if (!response.ok) {
    const error = new Error(result.error || `The request could not be completed (${response.status}).`);
    error.status = response.status;
    throw error;
  }
  if (result.csrfToken) csrfToken = result.csrfToken;
  return result;
}

export function toast(message, error = false) {
  const node = $('#toast');
  if (!node) return;
  clearTimeout(toastTimer);
  node.textContent = message;
  node.classList.toggle('error', error);
  node.hidden = false;
  toastTimer = setTimeout(() => { node.hidden = true; }, 4500);
}

export function icon(name, className = '') {
  const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
  svg.setAttribute('aria-hidden', 'true');
  if (className) svg.setAttribute('class', className);
  const use = document.createElementNS('http://www.w3.org/2000/svg', 'use');
  use.setAttribute('href', `/icons.svg#${name}`);
  svg.append(use);
  return svg;
}

export function element(tag, className, text) {
  const node = document.createElement(tag);
  if (className) node.className = className;
  if (text !== undefined) node.textContent = text;
  return node;
}

function banner(text) {
  $('#connection-banner').textContent = text;
  $('#connection-banner').hidden = !text;
}

function activeMailbox() { return state.mailboxes.find(m => m.id === state.activeId); }
function remember() { try { localStorage.setItem('tmailsharp.active', state.activeId || ''); } catch { /* Optional preference. */ } }
function showDialog(id) { const dialog = document.getElementById(id); if (dialog && !dialog.open) dialog.showModal(); }

function renderMailboxes() {
  const nav = $('#mailbox-nav');
  nav.replaceChildren();
  for (const mailbox of state.mailboxes) {
    const button = element('button', `mailbox-item${mailbox.id === state.activeId ? ' active' : ''}`);
    button.title = mailbox.address;
    button.append(icon('mail'), element('span', '', mailbox.address));
    button.addEventListener('click', () => selectMailbox(mailbox.id));
    nav.append(button);
  }
  const mailbox = activeMailbox();
  const address = $('#email-address');
  if (mailbox) {
    const [name, domain] = mailbox.address.split('@');
    address.replaceChildren(document.createTextNode(name), document.createElement('wbr'), document.createTextNode(`@${domain}`));
  } else address.textContent = 'Your next inbox is a click away.';
  $('#copy-email').disabled = !mailbox;
  $('#delete-inbox').disabled = !mailbox;
  $('#active-badge').hidden = !mailbox;
  $('#reserve-inbox').hidden = !mailbox || mailbox.isPermanent || !state.config?.user;
  $('#domain-select').replaceChildren(...state.domains.map(domain => {
    const option = element('option', '', `@${domain.name}`);
    option.value = domain.name;
    return option;
  }));
  if (mailbox) $('#domain-select').value = mailbox.domain;
  $('#mobile-mailbox-select').replaceChildren(...state.mailboxes.map(item => {
    const option = element('option', '', item.address);
    option.value = item.id;
    return option;
  }));
  if (mailbox) $('#mobile-mailbox-select').value = mailbox.id;
  updateExpiry();
}

async function selectMailbox(id) {
  state.activeId = id;
  state.version++;
  state.messages = [];
  remember();
  renderMailboxes();
  renderMessages();
  await refresh(true);
}

async function bootstrap() {
  try {
    const data = await api('/bootstrap');
    state.config = data;
    state.mailboxes = data.mailboxes;
    state.domains = data.domains;
    state.activeId = state.mailboxes.find(m => m.id === preferredId)?.id ?? state.mailboxes[0]?.id ?? null;
    $('#sample-message').hidden = !data.demoEnabled;
    if (!data.smtpListening) banner('Incoming mail is currently offline. Your inbox is still available; the server administrator can check the receiver.');
    else if (state.domains.every(d => d.name.endsWith('.localhost'))) banner('Local preview · These example domains receive test mail locally. Add your own domain and MX record to receive internet email.');
    else banner('');
    renderMailboxes();
    if (!state.activeId && state.domains.length) await createMailbox({});
    else if (state.activeId) await refresh();
    else banner('No email domains are available yet. Add an enabled domain in the administration page.');
  } catch (error) {
    $('#email-address').textContent = 'Your inbox is taking a moment.';
    banner(`Could not connect. ${error.message} Use Refresh to try again.`);
  }
}

async function createMailbox(body) {
  if (body.permanent === undefined) body.permanent = Boolean(state.config?.user);
  const mailbox = await api('/mailboxes', { method: 'POST', body });
  state.mailboxes.unshift(mailbox);
  await selectMailbox(mailbox.id);
  return mailbox;
}

async function refresh(manual = false) {
  const id = state.activeId;
  if (!id) { if (manual) await bootstrap(); return; }
  if (state.loading) return;
  state.loading = true;
  $('#refresh').classList.add('busy');
  try {
    const data = await api(`/mailboxes/${id}/messages`);
    if (state.activeId !== id) return;
    state.messages = data.messages;
    const index = state.mailboxes.findIndex(m => m.id === id);
    if (index >= 0) state.mailboxes[index] = data.mailbox;
    renderMessages();
    $('#last-checked').textContent = 'Just checked';
    if (manual) toast('Your inbox is up to date.');
  } catch (error) {
    if (error.status === 404) await expireMailbox(id);
    else {
      $('#last-checked').textContent = 'Connection interrupted · retrying';
      if (manual) toast(error.message, true);
    }
  } finally {
    state.loading = false;
    $('#refresh').classList.remove('busy');
    if (state.activeId && state.activeId !== id) void refresh();
  }
}

function timeLabel(value) {
  const date = new Date(value);
  const minutes = Math.max(0, Math.floor((Date.now() - date.getTime()) / 60000));
  if (minutes < 1) return 'Just now';
  if (minutes < 60) return `${minutes} min ago`;
  return date.toLocaleTimeString([], { hour: 'numeric', minute: '2-digit' });
}

function renderMessages() {
  const query = $('#search').value.toLocaleLowerCase().trim();
  const visible = state.messages.filter(m => `${m.senderName} ${m.sender} ${m.subject} ${m.preview}`.toLocaleLowerCase().includes(query));
  const list = $('#message-list');
  list.replaceChildren();
  const unread = state.messages.filter(m => !m.isRead).length;
  $('#sidebar-count').textContent = unread;
  $('#message-count').textContent = state.messages.length;
  document.title = `${unread ? `(${unread}) ` : ''}TMailSharp — A little less noise.`;
  $('#empty-inbox').hidden = state.messages.length > 0;
  $('#no-results').hidden = !(state.messages.length > 0 && visible.length === 0);
  for (const message of visible) {
    const row = element('button', `message-row${message.isRead ? '' : ' unread'}`);
    const avatar = element('span', 'sender-avatar', (message.senderName || message.sender).slice(0, 1).toUpperCase());
    const summary = element('span', 'message-summary');
    const top = element('span', 'message-row-top');
    top.append(element('span', 'message-sender', message.senderName || message.sender), element('span', 'message-time', timeLabel(message.receivedAt)));
    summary.append(top, element('span', 'message-subject', message.subject), element('span', 'message-preview', message.preview || 'Open to read this message'));
    row.append(avatar, summary);
    if (message.attachmentCount) row.append(icon('attachment', 'attachment-icon'));
    if (!message.isRead) row.append(element('span', 'unread-dot'));
    row.addEventListener('click', () => openMessage(message.id));
    list.append(row);
  }
}

async function openMessage(messageId) {
  const mailboxId = state.activeId;
  try {
    const detail = await api(`/mailboxes/${mailboxId}/messages/${messageId}`);
    if (mailboxId !== state.activeId) return;
    state.detail = { ...detail, mailboxId };
    const message = state.messages.find(m => m.id === messageId);
    if (message) message.isRead = true;
    renderMessages();
    $('#detail-subject').textContent = detail.subject;
    $('#detail-sender').textContent = detail.senderName || detail.sender;
    $('#detail-address').textContent = `${detail.sender} → ${detail.recipient}`;
    $('#detail-avatar').textContent = (detail.senderName || detail.sender).slice(0, 1).toUpperCase();
    $('#detail-date').textContent = new Date(detail.receivedAt).toLocaleString([], { dateStyle: 'medium', timeStyle: 'short' });
    $('#html-tab').hidden = !detail.htmlBody;
    showMessageBody(Boolean(detail.htmlBody));
    $('#detail-attachments').replaceChildren(...detail.attachments.map(attachment => {
      const link = element('a', 'attachment-link');
      link.href = `/inbox/mailboxes/${mailboxId}/messages/${messageId}/attachments/${attachment.id}`;
      link.setAttribute('download', attachment.fileName);
      link.append(icon('attachment'), document.createTextNode(attachment.fileName), element('span', '', `${Math.max(1, Math.ceil(attachment.size / 1024))} KB ↓`));
      return link;
    }));
    showDialog('message-dialog');
  } catch (error) { toast(error.message, true); }
}

function showMessageBody(formatted) {
  if (!state.detail) return;
  const container = $('#detail-content');
  container.replaceChildren();
  $('#html-tab').classList.toggle('active', formatted);
  $('#text-tab').classList.toggle('active', !formatted);
  if (formatted) {
    const iframe = document.createElement('iframe');
    iframe.title = 'Email content';
    iframe.setAttribute('sandbox', 'allow-popups allow-popups-to-escape-sandbox');
    iframe.setAttribute('referrerpolicy', 'no-referrer');
    iframe.src = `/inbox/mailboxes/${state.detail.mailboxId}/messages/${state.detail.id}/content?theme=${document.documentElement.dataset.theme === 'light' ? 'light' : 'dark'}`;
    container.append(iframe);
  } else container.append(element('pre', '', state.detail.textBody || 'This email has no text content.'));
}

function updateExpiry() {
  const mailbox = activeMailbox();
  if (!mailbox) { $('#expiry-time').textContent = '—'; return; }
  if (mailbox.isPermanent) {
    $('#expiry-label').textContent = 'Permanent address';
    $('#expiry-time').textContent = '';
    const retention = state.domains.find(domain => domain.name === mailbox.domain)?.retentionMinutes ?? state.config?.messageRetentionMinutes ?? 60;
    $('#expiry-hint').textContent = `Messages kept for ${retention} minutes.`;
    return;
  }
  $('#expiry-label').textContent = 'Disappears in';
  $('#expiry-hint').textContent = 'A fresh start, automatically.';
  const remaining = new Date(mailbox.expiresAt).getTime() - Date.now();
  if (remaining <= 0) { void expireMailbox(mailbox.id); return; }
  const seconds = Math.ceil(remaining / 1000);
  $('#expiry-time').textContent = `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, '0')}`;
}

async function expireMailbox(id) {
  if (!state.mailboxes.some(m => m.id === id)) return;
  state.mailboxes = state.mailboxes.filter(m => m.id !== id);
  // Keep an already opened message visible until the reader closes it.
  if (state.activeId === id) {
    state.activeId = state.mailboxes[0]?.id ?? null;
    state.messages = [];
    remember();
    toast('That inbox has expired. Create a fresh address when you need one.');
  }
  renderMailboxes();
  renderMessages();
}

function openAddressForm() {
  $('#address-error').textContent = '';
  $('#local-part').value = '';
  showDialog('address-dialog');
}

export function bindDialogs() {
  for (const button of $$('[data-dialog]')) button.addEventListener('click', () => showDialog(button.dataset.dialog));
  for (const button of $$('[data-close]')) button.addEventListener('click', () => button.closest('dialog').close());
  for (const dialog of $$('dialog')) dialog.addEventListener('click', event => {
    if (event.target !== dialog) return;
    const bounds = dialog.getBoundingClientRect();
    if (event.clientX < bounds.left || event.clientX > bounds.right || event.clientY < bounds.top || event.clientY > bounds.bottom) dialog.close();
  });
}

async function perform(button, action) {
  if (button.disabled) return;
  button.disabled = true;
  try { await action(); } catch (error) { toast(error.message, true); }
  finally { button.disabled = false; }
}

function initializeInbox() {
  bindDialogs();
  document.addEventListener('tmailthemechange', () => {
    if (state.detail && $('#message-dialog').open) showMessageBody($('#html-tab').classList.contains('active'));
  });
  $('#copy-email').addEventListener('click', async () => {
    const address = activeMailbox()?.address;
    if (!address) return;
    try { await navigator.clipboard.writeText(address); toast('Address copied. A little less typing.'); }
    catch { toast('Select the address to copy it. Clipboard access is unavailable in this browser.', true); }
  });
  for (const id of ['customize-address', 'add-inbox']) document.getElementById(id).addEventListener('click', openAddressForm);
  $('#new-address').addEventListener('click', event => perform(event.currentTarget, async () => { await createMailbox({ domain: activeMailbox()?.domain }); toast('A fresh address, just for you.'); }));
  $('#delete-inbox').addEventListener('click', () => { if (activeMailbox()) showDialog('confirm-dialog'); });
  $('#confirm-delete').addEventListener('click', event => perform(event.currentTarget, async () => {
    const id = state.activeId;
    if (!id) return;
    await api(`/mailboxes/${id}`, { method: 'DELETE' });
    state.mailboxes = state.mailboxes.filter(m => m.id !== id);
    state.activeId = state.mailboxes[0]?.id ?? null;
    state.messages = [];
    state.version++;
    remember();
    $('#confirm-dialog').close();
    renderMailboxes(); renderMessages();
    if (state.activeId) await refresh();
    toast('Inbox deleted. A clean slate.');
  }));
  $('#address-form').addEventListener('submit', async event => {
    event.preventDefault();
    const submit = $('button[type="submit"]', event.currentTarget);
    submit.disabled = true;
    $('#address-error').textContent = '';
    try {
      await createMailbox({ localPart: $('#local-part').value.trim() || null, domain: $('#domain-select').value,
        permanent: $('#permanent-address')?.checked ?? false });
      $('#address-dialog').close(); toast('Your new address is ready.');
    } catch (error) { $('#address-error').textContent = error.message; }
    finally { submit.disabled = false; }
  });
  $('#refresh').addEventListener('click', () => refresh(true));
  $('#search').addEventListener('input', renderMessages);
  $('#mobile-mailbox-select').addEventListener('change', event => selectMailbox(event.target.value));
  $('#sample-message').addEventListener('click', event => perform(event.currentTarget, async () => {
    if (!state.activeId) throw new Error('Create an inbox first.');
    await api(`/mailboxes/${state.activeId}/sample`, { method: 'POST' });
    await refresh(); toast('A little hello has arrived.');
  }));
  $('#html-tab').addEventListener('click', () => showMessageBody(true));
  $('#text-tab').addEventListener('click', () => showMessageBody(false));
  $('#message-dialog').addEventListener('close', () => {
    if ($('#message-dialog').open) return;
    $('#detail-content').replaceChildren(); state.detail = null;
  });
  $('#reserve-inbox').addEventListener('click', event => perform(event.currentTarget, async () => {
    if (!state.activeId) return;
    const mailbox = await api(`/mailboxes/${state.activeId}/reserve`, { method: 'POST' });
    const index = state.mailboxes.findIndex(item => item.id === mailbox.id);
    if (index >= 0) state.mailboxes[index] = mailbox;
    renderMailboxes(); toast('This address is now reserved for your account.');
  }));
  $('#delete-message').addEventListener('click', event => perform(event.currentTarget, async () => {
    if (!state.detail) return;
    const { mailboxId, id } = state.detail;
    await api(`/mailboxes/${mailboxId}/messages/${id}`, { method: 'DELETE' });
    state.messages = state.messages.filter(m => m.id !== id);
    $('#message-dialog').close(); renderMessages(); toast('Message deleted.');
  }));
  document.addEventListener('visibilitychange', () => { if (!document.hidden) { updateExpiry(); void refresh(); } });
  setInterval(() => { if (!document.hidden) void refresh(); }, 6000);
  setInterval(updateExpiry, 1000);
  void bootstrap();
}

if ($('#email-address')) initializeInbox();
