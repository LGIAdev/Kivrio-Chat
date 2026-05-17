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

function makeElement(id = '') {
  return {
    id,
    className: '',
    dataset: {},
    disabled: false,
    hidden: false,
    listeners: {},
    style: {},
    textContent: '',
    value: '',
    appendChild() {},
    addEventListener(type, handler) {
      this.listeners[type] = handler;
    },
    focus() {},
    select() {},
    setAttribute(name, value) {
      this[name] = value;
    },
  };
}

const elements = new Map();
const logoutEntry = makeElement('logout-entry');
elements.set('logout-entry', logoutEntry);

function ensureLoginElements() {
  for (const id of [
    'kivrio-login-overlay',
    'login-title',
    'login-hint',
    'login-form',
    'login-password',
    'login-password-confirm',
    'login-confirm-wrap',
    'login-error',
    'login-btn',
  ]) {
    if (!elements.has(id)) {
      elements.set(id, makeElement(id));
    }
  }
}

globalThis.localStorage = {
  removeItem() {},
};

globalThis.window = {
  location: { origin: 'http://127.0.0.1:8020' },
  addEventListener() {},
  dispatchEvent() {},
};

globalThis.CustomEvent = class CustomEvent {
  constructor(type, init = {}) {
    this.type = type;
    this.detail = init.detail;
  }
};
globalThis.HTMLInputElement = Object;
globalThis.HTMLButtonElement = Object;

globalThis.document = {
  body: {
    appendChild(element) {
      if (element?.id) elements.set(element.id, element);
    },
  },
  createElement() {
    const element = makeElement();
    Object.defineProperty(element, 'innerHTML', {
      set() {
        ensureLoginElements();
      },
    });
    return element;
  },
  getElementById(id) {
    return elements.get(id) || null;
  },
};

const calls = [];
globalThis.fetch = async (url, init = {}) => {
  calls.push({ url: String(url), init });
  const parsedUrl = new URL(String(url));
  assert.notEqual(parsedUrl.pathname, '/api/shutdown', 'v2026.5.10 logout pipeline must keep the local server running');
  assert.equal(parsedUrl.pathname, '/api/auth/logout');
  return jsonResponse({ ok: true, authenticated: false });
};

const { wireLogout } = await import('../js/auth/logout.js');

wireLogout();
assert.equal(typeof logoutEntry.listeners.click, 'function', 'logout click handler should be registered');
await logoutEntry.listeners.click({ preventDefault() {} });

assert.equal(calls.length, 1);
assert.equal(new URL(calls[0].url).pathname, '/api/auth/logout');
assert.equal(calls[0].init.method, 'POST');
assert.equal(elements.get('login-title').textContent, 'Connectez-vous a Kivrio Chat');
assert.equal(elements.get('login-confirm-wrap').hidden, true);
assert.equal(elements.get('login-password').disabled, false);
assert.equal(elements.get('login-password-confirm').disabled, false);
assert.equal(elements.get('login-btn').disabled, false);
assert.equal(elements.get('login-error').textContent, 'Session fermee.');

console.log('auth reconnect UI tests passed');
