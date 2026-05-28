import assert from 'node:assert/strict';

globalThis.window = {
  addEventListener() {},
  setTimeout,
  clearTimeout,
  requestAnimationFrame(callback) {
    return setTimeout(callback, 0);
  },
};

globalThis.document = {
  addEventListener() {},
  querySelector() {
    return null;
  },
  createElement() {
    return {
      style: {},
      setAttribute() {},
      appendChild() {},
      select() {},
      remove() {},
    };
  },
  body: {
    appendChild() {},
    removeChild() {},
  },
};

const { renderMarkdown } = await import('../js/chat/render.js');

{
  const html = renderMarkdown('```html\n<!doctype html>\n<html><head><title>Demo</title></head><body>OK</body></html>\n```');
  assert.match(html, /class="kivrio-html-code-card"/);
  assert.match(html, /data-code-action="copy-html"/);
  assert.match(html, /data-code-action="preview-html"/);
  assert.match(html, /data-code-action="download-html"/);
}

{
  const html = renderMarkdown('```\n<!doctype html>\n<html><body>OK</body></html>\n```');
  assert.match(html, /class="kivrio-html-code-card"/);
}

{
  const js = renderMarkdown('```js\nconsole.log("<html>");\n```');
  assert.doesNotMatch(js, /class="kivrio-html-code-card"/);
  assert.doesNotMatch(js, /data-code-action="preview-html"/);
}

console.log('html code action render tests passed');
