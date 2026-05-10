import assert from 'node:assert/strict';

globalThis.window = {
  addEventListener() {},
  dispatchEvent() {},
  kivrioAbortController: null,
};

globalThis.document = {
  addEventListener() {},
  querySelector() {
    return null;
  },
};

globalThis.localStorage = {
  getItem() {
    return null;
  },
  setItem() {},
  removeItem() {},
};

function emptyStreamResponse({ status = 200, ok = true } = {}) {
  return {
    status,
    ok,
    body: {
      getReader() {
        return {
          async read() {
            return { done: true, value: undefined };
          },
        };
      },
    },
  };
}

async function drain(iterator) {
  for await (const _chunk of iterator) {
    // Drain the stream.
  }
}

const { isAbortError, streamChat } = await import('../js/net/ollama.js');

{
  const controller = new AbortController();
  const calls = [];
  globalThis.fetch = async (url, init) => {
    calls.push({ url, init });
    return emptyStreamResponse();
  };

  await drain(streamChat({
    base: 'http://ollama.test',
    model: 'test-model',
    sys: '',
    prompt: 'hello',
    convId: 'c1',
    signal: controller.signal,
  }));

  assert.equal(calls.length, 1);
  assert.equal(calls[0].url, 'http://ollama.test/api/chat');
  assert.equal(calls[0].init.signal, controller.signal);
}

{
  const controller = new AbortController();
  const calls = [];
  globalThis.fetch = async (url, init) => {
    calls.push({ url, init });
    if (url.endsWith('/api/chat')) return emptyStreamResponse({ status: 404, ok: false });
    return emptyStreamResponse();
  };

  await drain(streamChat({
    base: 'http://ollama.test',
    model: 'test-model',
    sys: '',
    prompt: 'hello',
    convId: 'c1',
    signal: controller.signal,
  }));

  assert.equal(calls.length, 2);
  assert.equal(calls[0].url, 'http://ollama.test/api/chat');
  assert.equal(calls[1].url, 'http://ollama.test/api/generate');
  assert.equal(calls[0].init.signal, controller.signal);
  assert.equal(calls[1].init.signal, controller.signal);
}

{
  const controller = new AbortController();
  controller.abort();
  let fetchCalled = false;
  globalThis.fetch = async () => {
    fetchCalled = true;
    return emptyStreamResponse();
  };

  const iterator = streamChat({
    base: 'http://ollama.test',
    model: 'test-model',
    sys: '',
    prompt: 'hello',
    convId: 'c1',
    signal: controller.signal,
  });

  await assert.rejects(
    async () => {
      await iterator.next();
    },
    (error) => isAbortError(error),
  );
  assert.equal(fetchCalled, false);
}

console.log('ollama abort tests passed');
