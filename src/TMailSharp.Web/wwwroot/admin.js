import { api, toast, element } from './app.js';
const $ = selector => document.querySelector(selector);

async function load() {
  try {
    const data = await api('/admin/overview');
    $('#login-panel').hidden = true;
    $('#admin-content').hidden = false;
    $('#stat-inboxes').textContent = data.stats.activeMailboxes;
    $('#stat-messages').textContent = data.stats.storedMessages;
    $('#stat-domains').textContent = data.stats.activeDomains;
    $('#smtp-status').textContent = data.smtpListening ? `Listening · ${data.smtpPort}` : 'Offline';
    $('#setting-lifetime').textContent = `${data.stats.lifetimeMinutes} minutes`;
    $('#setting-size').textContent = `${data.stats.maxMessageBytes / 1024 / 1024} MB`;
    $('#setting-limit').textContent = data.stats.maxMessagesPerMailbox;
    $('#setting-tls').textContent = data.smtpTls ? 'STARTTLS enabled' : 'Not configured';
    const rows = data.domains.map(domain => {
      const row = element('tr');
      row.append(element('td', '', domain.name), element('td', '', domain.enabled ? 'Enabled' : 'Disabled'));
      const action = element('td');
      const toggle = element('button', `toggle-button${domain.enabled ? ' enabled' : ''}`, domain.enabled ? 'Disable' : 'Enable');
      toggle.setAttribute('aria-label', `${domain.enabled ? 'Disable' : 'Enable'} ${domain.name}`);
      toggle.addEventListener('click', async () => {
        toggle.disabled = true;
        try { await api('/admin/domains', { method: 'PUT', body: { name: domain.name, enabled: !domain.enabled } }); await load(); }
        catch (error) { toast(error.message, true); toggle.disabled = false; }
      });
      action.append(toggle); row.append(action); return row;
    });
    $('#domain-rows').replaceChildren(...rows);
  } catch (error) {
    if (error.status === 401) { $('#admin-content').hidden = true; $('#login-panel').hidden = false; }
    else toast(error.message, true);
  }
}

$('#login-form').addEventListener('submit', async event => {
  event.preventDefault();
  const button = event.currentTarget.querySelector('button');
  button.disabled = true;
  $('#login-error').textContent = '';
  try { await api('/admin/login', { method: 'POST', body: { password: $('#admin-password').value } }); $('#admin-password').value = ''; await load(); }
  catch (error) { $('#login-error').textContent = error.message; }
  finally { button.disabled = false; }
});
$('#logout').addEventListener('click', async () => {
  try { await api('/admin/logout', { method: 'POST' }); await load(); }
  catch (error) { toast(error.message, true); }
});
$('#domain-form').addEventListener('submit', async event => {
  event.preventDefault();
  const button = event.currentTarget.querySelector('button');
  button.disabled = true;
  try { await api('/admin/domains', { method: 'PUT', body: { name: $('#new-domain').value, enabled: true } }); $('#new-domain').value = ''; await load(); toast('Domain added. Remember to configure its DNS.'); }
  catch (error) { toast(error.message, true); }
  finally { button.disabled = false; }
});
void load();
setInterval(() => { if (!document.hidden && !$('#admin-content').hidden) void load(); }, 30000);
