const token = document.querySelector('meta[name="csrf-token"]').content;
const status = document.getElementById('passkey-status');
const encode = value => btoa(String.fromCharCode(...new Uint8Array(value))).replaceAll('+', '-').replaceAll('/', '_').replace(/=+$/, '');
const decode = value => Uint8Array.from(atob(value.replaceAll('-', '+').replaceAll('_', '/')), c => c.charCodeAt(0)).buffer;

async function post(path, body = {}) {
  const response = await fetch(path, { method: 'POST', credentials: 'same-origin', headers: {
    'Content-Type': 'application/json', 'X-CSRF-TOKEN': token
  }, body: JSON.stringify(body) });
  const result = await response.json().catch(() => ({}));
  if (!response.ok) throw new Error(result.error || 'The request could not be completed. Refresh the page and try again.');
  return result;
}

function credentialJson(credential) {
  if (credential.toJSON) return JSON.stringify(credential.toJSON());
  const response = { clientDataJSON: encode(credential.response.clientDataJSON) };
  for (const name of ['attestationObject', 'authenticatorData', 'signature', 'userHandle']) {
    if (credential.response[name]) response[name] = encode(credential.response[name]);
  }
  if (credential.response.getTransports) response.transports = credential.response.getTransports();
  return JSON.stringify({ id: credential.id, rawId: encode(credential.rawId), type: credential.type, response,
    authenticatorAttachment: credential.authenticatorAttachment, clientExtensionResults: credential.getClientExtensionResults() });
}

function optionsFromJson(options, create) {
  if (create && PublicKeyCredential.parseCreationOptionsFromJSON) return PublicKeyCredential.parseCreationOptionsFromJSON(options);
  if (!create && PublicKeyCredential.parseRequestOptionsFromJSON) return PublicKeyCredential.parseRequestOptionsFromJSON(options);
  options.challenge = decode(options.challenge);
  if (options.user) options.user.id = decode(options.user.id);
  for (const collection of ['excludeCredentials', 'allowCredentials']) {
    for (const credential of options[collection] || []) credential.id = decode(credential.id);
  }
  return options;
}

async function perform(button, action) {
  button.disabled = true;
  status.textContent = '';
  try {
    if (!window.isSecureContext || !window.PublicKeyCredential) throw new Error('Passkeys require HTTPS and a browser that supports security keys.');
    await action();
  } catch (error) {
    status.textContent = error.name === 'NotAllowedError' ? 'The key request was cancelled or timed out. You can try again.' : error.message;
  } finally { button.disabled = false; }
}

document.getElementById('passkey-login')?.addEventListener('click', event => perform(event.currentTarget, async () => {
  const options = optionsFromJson(await post('/account/passkeys/login-options'), false);
  const credential = await navigator.credentials.get({ publicKey: options });
  if (!credential) throw new Error('No credential was returned.');
  const result = await post('/account/passkeys/login', { credentialJson: credentialJson(credential) });
  window.location.assign(result.redirect);
}));

document.getElementById('passkey-add')?.addEventListener('submit', event => {
  event.preventDefault();
  const button = event.currentTarget.querySelector('button');
  void perform(button, async () => {
    const password = document.getElementById('key-password');
    const options = optionsFromJson(await post('/account/passkeys/options', { password: password.value }), true);
    password.value = '';
    const credential = await navigator.credentials.create({ publicKey: options });
    if (!credential) throw new Error('No credential was returned.');
    await post('/account/passkeys/add', { credentialJson: credentialJson(credential), name: document.getElementById('key-name').value });
    window.location.reload();
  });
});
