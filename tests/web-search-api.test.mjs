import assert from 'node:assert/strict';

globalThis.window = {
  location: { origin: 'http://127.0.0.1:8020' },
  dispatchEvent() {},
};

const { webSearch } = await import('../js/net/conversationsApi.js');

{
  let call = null;
  globalThis.fetch = async (url, init) => {
    call = { url, init };
    return {
      ok: true,
      status: 200,
      async json() {
        return {
          ok: false,
          available: false,
          results: [],
          message: 'La recherche Web est momentan\u00e9ment indisponible. Vous pouvez r\u00e9essayer ou continuer sans recherche Web.',
        };
      },
    };
  };

  const payload = await webSearch('message courant', { maxResults: 5 });

  assert.equal(call.url, 'http://127.0.0.1:8020/api/web-search');
  assert.equal(call.init.method, 'POST');
  assert.equal(call.init.credentials, 'same-origin');
  assert.equal(call.init.headers.Accept, 'application/json');
  assert.equal(call.init.headers['Content-Type'], 'application/json');
  assert.equal(call.init.body, '{"query":"message courant","max_results":5}');
  assert.equal(payload.available, false);
  assert.deepEqual(payload.results, []);
}

console.log('web search API tests passed');
