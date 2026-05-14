import assert from 'node:assert/strict';

const localStorageData = new Map();

globalThis.window = {
  addEventListener() {},
  dispatchEvent() {},
  kivrioAbortController: null,
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

const { Store } = await import('../js/store/conversations.js');
const {
  buildPromptWithWebSearchContext,
  buildWebSearchUnavailableAssistantMessage,
  buildWebSearchPromptContext,
  shouldBlockModelForUnavailableWebSearch,
  streamChat,
} = await import('../js/net/ollama.js');

{
  const context = buildWebSearchPromptContext({
    available: true,
    results: [
      {
        title: 'Premier <b>resultat</b>',
        url: 'https://example.test/a',
        content: 'Extrait avec   espaces et balises <em>HTML</em>.',
        engine: 'searxng',
      },
      {
        title: 'Doublon ignore',
        url: 'https://example.test/a',
        content: 'Ne doit pas etre repris.',
      },
      {
        title: 'Second resultat',
        url: 'https://example.test/b',
        snippet: 'Autre extrait utile.',
        source: 'source-test',
      },
    ],
  });

  assert.equal(context.available, true);
  assert.equal(context.sources.length, 2);
  assert.match(context.promptContext, /\[1\] Premier resultat/);
  assert.match(context.promptContext, /Source: searxng/);
  assert.match(context.promptContext, /URL: https:\/\/example\.test\/a/);
  assert.match(context.promptContext, /Extrait: Extrait avec espaces et balises HTML\./);
  assert.match(context.promptContext, /\[2\] Second resultat/);
  assert.doesNotMatch(context.promptContext, /Doublon ignore/);
  assert.doesNotMatch(context.promptContext, /<b>|<em>/);
}

{
  const unavailable = buildWebSearchPromptContext({
    available: false,
    results: [{ title: 'Ignore', url: 'https://example.test/ignore' }],
  });

  assert.equal(unavailable.available, false);
  assert.equal(unavailable.promptContext, '');
  assert.deepEqual(unavailable.sources, []);
}

{
  const context = buildWebSearchPromptContext({
    available: true,
    results: [{ title: 'Source A', url: 'https://example.test/a', content: 'Fait A.' }],
  });
  const enriched = buildPromptWithWebSearchContext('Question originale', context.promptContext);

  assert.match(enriched, /Question utilisateur:\nQuestion originale/);
  assert.match(enriched, /Contexte Web:/);
  assert.match(enriched, /\[1\] Source A/);
  assert.match(enriched, /references \[1\], \[2\]/);
  assert.equal(buildPromptWithWebSearchContext('Question originale', ''), 'Question originale');
}

{
  const unavailableMessage = buildWebSearchUnavailableAssistantMessage('La recherche Web est momentan\u00e9ment indisponible.');
  assert.match(unavailableMessage, /^Je ne peux pas effectuer la recherche Web actuellement\./);
  assert.equal(
    shouldBlockModelForUnavailableWebSearch(true, { available: false, sources: [], promptContext: '' }),
    true,
    'requested unavailable web search should block model generation',
  );
  assert.equal(
    shouldBlockModelForUnavailableWebSearch(true, { aborted: true, promptContext: '' }),
    false,
    'aborted web search should not create an assistant fallback',
  );
  assert.equal(
    shouldBlockModelForUnavailableWebSearch(false, { available: false, sources: [], promptContext: '' }),
    false,
    'normal messages without web search should still reach the model',
  );
}

{
  globalThis.fetch = async (url) => {
    if (String(url).endsWith('/api/conversations')) {
      return jsonResponse({ id: 'conv-web-context', title: 'Test Web', messages: [] });
    }
    return emptyStreamResponse();
  };

  const conversation = await Store.create({ title: 'Test Web' });
  const cachedConversation = Store.get(conversation.id);
  cachedConversation.messages = [
    { id: 1, role: 'user', content: 'Question originale' },
  ];
  cachedConversation.messagesLoaded = true;
  cachedConversation.messageCount = cachedConversation.messages.length;

  const calls = [];
  const enrichedPrompt = [
    'Question utilisateur:',
    'Question originale',
    '',
    'Contexte Web:',
    '[1] Source A',
    'URL: https://example.test/a',
  ].join('\n');

  globalThis.fetch = async (url, init) => {
    calls.push({ url: String(url), init });
    return emptyStreamResponse();
  };

  await drain(streamChat({
    base: 'http://ollama.test',
    model: 'test-model',
    sys: '',
    prompt: enrichedPrompt,
    historyUserText: 'Question originale',
    convId: conversation.id,
  }));

  assert.equal(calls.length, 1);
  const body = JSON.parse(calls[0].init.body);
  const userMessages = body.messages.filter((message) => message.role === 'user');
  assert.equal(userMessages.length, 1);
  assert.equal(userMessages[0].content, enrichedPrompt);
}

console.log('web search prompt injection tests passed');
