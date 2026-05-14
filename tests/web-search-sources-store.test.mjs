import assert from 'node:assert/strict';

const localStorageData = new Map();

globalThis.window = {
  addEventListener() {},
  dispatchEvent() {},
  location: { origin: 'http://127.0.0.1:8020' },
};

globalThis.document = {
  addEventListener() {},
  querySelector() {
    return null;
  },
};

globalThis.localStorage = {
  getItem(key) {
    return localStorageData.has(key) ? localStorageData.get(key) : null;
  },
  setItem(key, value) {
    localStorageData.set(key, String(value));
  },
  removeItem(key) {
    localStorageData.delete(key);
  },
};

function jsonResponse(payload, { status = 200, ok = true } = {}) {
  return {
    status,
    ok,
    async json() {
      return payload;
    },
  };
}

const { Store } = await import('../js/store/conversations.js');

const calls = [];
globalThis.fetch = async (url, init = {}) => {
  calls.push({ url: String(url), init });
  const parsedUrl = new URL(String(url));

  if (parsedUrl.pathname === '/api/conversations' && init.method === 'POST') {
    return jsonResponse({
      id: 'conv_sources',
      title: 'Sources',
      folderId: null,
      createdAt: 1,
      updatedAt: 1,
      archived: 0,
      messages: [],
    }, { status: 201 });
  }

  if (parsedUrl.pathname === '/api/conversations/conv_sources/messages' && init.method === 'POST') {
    const body = JSON.parse(init.body);
    return jsonResponse({
      id: 'msg_sources',
      conversationId: 'conv_sources',
      role: body.role,
      content: body.content,
      reasoningText: body.reasoning_text,
      model: body.model,
      reasoningDurationMs: body.reasoning_duration_ms,
      webSources: body.web_sources,
      createdAt: 2,
      position: 0,
      attachments: [],
    }, { status: 201 });
  }

  throw new Error(`Unexpected request: ${init.method || 'GET'} ${parsedUrl.pathname}`);
};

const conversation = await Store.create({ title: 'Sources' });
const message = await Store.addMsg(conversation.id, 'assistant', 'Reponse citee', {
  model: 'test-model',
  webSources: [
    {
      index: 1,
      title: 'Source A',
      url: 'https://example.test/a',
      snippet: 'Extrait A',
      source: 'searxng',
    },
  ],
});

const messageRequest = calls.find((call) => new URL(call.url).pathname.endsWith('/messages'));
const postedBody = JSON.parse(messageRequest.init.body);

assert.equal(postedBody.web_sources.length, 1);
assert.equal(postedBody.web_sources[0].title, 'Source A');
assert.equal(postedBody.web_sources[0].url, 'https://example.test/a');
assert.equal(message.webSources.length, 1);
assert.equal(message.webSources[0].source, 'searxng');
assert.equal(Store.get(conversation.id).messages[0].webSources[0].title, 'Source A');

console.log('web search source store tests passed');
