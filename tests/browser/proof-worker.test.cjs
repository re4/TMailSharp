const assert = require('node:assert/strict');
const { createHash } = require('node:crypto');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const source = fs.readFileSync(path.join(__dirname, '../../src/TMailSharp.Web/wwwroot/proof-worker.js'), 'utf8');

function run(challenge, difficulty) {
  return new Promise((resolve, reject) => {
    const timeout = setTimeout(() => reject(new Error('Worker did not complete')), 65000);
    const self = { postMessage(result) { clearTimeout(timeout); resolve(result); } };
    vm.runInNewContext(source, { self, setTimeout, atob: value => Buffer.from(value, 'base64').toString('binary') });
    self.onmessage({ data: { challenge, difficulty } });
  });
}

(async () => {
  for (const [prefix, difficulty] of [[Buffer.alloc(32), 16], [Buffer.from(Array.from({ length: 32 }, (_, i) => i)), 16], [Buffer.alloc(32, 255), 20]]) {
    const result = await run(prefix.toString('base64url'), difficulty);
    assert.equal(result.error, undefined);
    assert.match(result.nonce, /^\d+$/);
    const bytes = Buffer.alloc(36);
    prefix.copy(bytes);
    bytes.writeUInt32BE(Number(result.nonce), 32);
    assert.ok(createHash('sha256').update(bytes).digest().readUInt32BE(0) < 2 ** (32 - difficulty));
  }
  assert.ok((await run('invalid', 16)).error);
  assert.ok((await run(Buffer.alloc(32).toString('base64url'), 8)).error);
  console.log('Proof worker: native SHA-256 verification and input limits passed.');
})().catch(error => { console.error(error); process.exitCode = 1; });
