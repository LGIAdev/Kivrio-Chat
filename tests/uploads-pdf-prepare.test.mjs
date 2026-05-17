import assert from 'node:assert/strict';

function jsonResponse(payload, { status = 200 } = {}) {
  return {
    ok: status >= 200 && status < 300,
    status,
    async json() {
      return payload;
    },
  };
}

globalThis.window = {
  location: { origin: 'http://127.0.0.1:8020' },
  dispatchEvent() {},
};

const calls = [];
globalThis.fetch = async (url, init = {}) => {
  calls.push({ url: String(url), init });
  const parsedUrl = new URL(String(url));
  assert.equal(parsedUrl.pathname, '/api/attachments/a1/text');
  assert.equal(init.method || 'GET', 'GET');
  return jsonResponse({
    ok: true,
    attachmentId: 'a1',
    filename: 'manuel.pdf',
    pageCount: 1,
    text: 'Texte PDF extrait pour le modele.',
    truncated: false,
  });
};

const { preparePendingUploadsForSend } = await import('../js/features/uploads.js');

const result = await preparePendingUploadsForSend({
  model: 'mistral',
  userText: 'Resume ce document.',
  items: [
    {
      kind: 'pdf',
      file: {
        name: 'manuel.pdf',
        type: 'application/pdf',
        size: 1234,
      },
    },
  ],
  uploadedAttachments: [
    {
      id: 'a1',
      filename: 'manuel.pdf',
      mimeType: 'application/pdf',
      textUrl: '/api/attachments/a1/text',
      isPdf: true,
    },
  ],
});

assert.equal(result.ok, true);
assert.equal(calls.length, 1);
assert.match(result.promptText, /Resume ce document\./);
assert.match(result.promptText, /Contenu des fichiers joints:/);
assert.match(result.promptText, /Fichier: manuel\.pdf/);
assert.match(result.promptText, /Texte PDF extrait pour le modele\./);

console.log('uploads PDF prepare test passed');
