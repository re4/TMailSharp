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

const securityCheck = document.getElementById('security-check');
const verifyButton = document.getElementById('security-check-button');
const checkStatus = document.getElementById('security-check-status');
let readyProof, checking, expiryTimer, cancelWork;

function checkMessage(message, verified = false) {
  if (!securityCheck) return;
  securityCheck.classList.toggle('verified', verified);
  checkStatus.textContent = message;
}

async function verifyBrowser() {
  if (!securityCheck) throw new Error('Reload the page to start the security check.');
  if (readyProof && readyProof.expiresAt > Date.now() + 5000) return readyProof;
  if (checking) return checking;
  verifyButton.disabled = true;
  verifyButton.textContent = 'Checking…';
  checkMessage('Checking your browser. You can keep filling in the form.');
  clearTimeout(expiryTimer);
  checking = (async () => {
    if (!window.Worker) throw new Error('This browser does not support the security check. Please use an updated browser.');
    const challenge = await post(securityCheck.dataset.endpoint, { purpose: securityCheck.dataset.purpose });
    const expiresAt = Date.parse(challenge.expiresAt);
    if (!Number.isFinite(expiresAt) || expiresAt <= Date.now()) throw new Error('The security check expired. Please try again.');
    const nonce = await new Promise((resolve, reject) => {
      const worker = new Worker(securityCheck.dataset.worker);
      const finish = (error, value) => {
        clearTimeout(timer);
        worker.terminate();
        cancelWork = undefined;
        if (error) reject(error); else resolve(value);
      };
      const timer = setTimeout(() => finish(new Error('The security check timed out. Please try again.')),
        Math.min(60000, expiresAt - Date.now()));
      cancelWork = () => finish(new Error('The security check was paused. Please try again.'));
      worker.onmessage = event => {
        if (typeof event.data?.nonce === 'string') finish(null, event.data.nonce);
        else finish(new Error(event.data?.error || 'The security check failed. Please try again.'));
      };
      worker.onerror = () => finish(new Error('The security check could not run. Reload the page and try again.'));
      worker.postMessage({ challenge: challenge.challenge, difficulty: challenge.difficulty });
    });
    readyProof = { proofToken: challenge.token, proofNonce: nonce, expiresAt };
    checkMessage('Verified. You can continue.', true);
    verifyButton.textContent = 'Verified';
    expiryTimer = setTimeout(() => {
      readyProof = undefined;
      verifyButton.disabled = false;
      verifyButton.textContent = 'Verify again';
      checkMessage('The check expired. Please verify again.');
    }, Math.max(0, expiresAt - Date.now() - 5000));
    return readyProof;
  })();
  try { return await checking; }
  catch (error) {
    readyProof = undefined;
    verifyButton.disabled = false;
    verifyButton.textContent = 'Try again';
    checkMessage(error.message);
    throw error;
  } finally { checking = undefined; }
}

async function takeProof() {
  const result = await verifyBrowser();
  readyProof = undefined;
  clearTimeout(expiryTimer);
  verifyButton.disabled = false;
  verifyButton.textContent = 'Verify again';
  return { proofToken: result.proofToken, proofNonce: result.proofNonce };
}

verifyButton?.addEventListener('click', () => { void verifyBrowser().catch(() => {}); });
if (securityCheck) {
  const form = securityCheck.closest('form');
  let submitting = false, nativeSubmit = false;
  form.addEventListener('submit', async event => {
    if (nativeSubmit) return;
    event.preventDefault();
    if (submitting) return;
    submitting = true;
    const submitter = event.submitter;
    if (submitter) submitter.disabled = true;
    try {
      const proof = await takeProof();
      form.elements.namedItem('ProofToken').value = proof.proofToken;
      form.elements.namedItem('ProofNonce').value = proof.proofNonce;
      if (submitter) submitter.disabled = false;
      nativeSubmit = true;
      if (submitter) form.requestSubmit(submitter); else form.requestSubmit();
    } catch { /* The check displays its own retry message. */ }
    finally {
      nativeSubmit = false;
      submitting = false;
      if (submitter) submitter.disabled = false;
    }
  });
  document.addEventListener('visibilitychange', () => { if (document.hidden) cancelWork?.(); });
  window.addEventListener('pagehide', () => cancelWork?.());
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
  const options = optionsFromJson(await post('/account/passkeys/login-options', await takeProof()), false);
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
