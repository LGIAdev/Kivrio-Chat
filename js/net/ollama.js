// js/net/ollama.js
// Flux reseau Ollama + rendu des messages (compatible KaTeX)

import { bindMessageRecord, renderMsg, updateBubbleContent } from '../chat/render.js';
import { Store, fmtTitle, mountHistory } from '../store/conversations.js';
import { qs } from '../core/dom.js';
import {
  consumeWebSearchSelection,
  detachPendingUploads,
  getPendingUploads,
  preparePendingUploadsForSend,
  releaseUploadItems,
  restorePendingUploads,
} from '../features/uploads.js';
import {
  getSystemPrompt,
  saveSystemPrompt,
  uploadConversationAttachments,
  webSearch,
} from './conversationsApi.js';
import { assistantErrorMessage, showToast, userMessageForError } from '../ui/errors.js';
import { t } from '../i18n/i18n.js';

const LS = { base: 'ollamaBase', model: 'ollamaModel' };
const THINK_START_TAG = '<think>';
const THINK_END_TAG = '</think>';
const CHAT_REASONING_PATHS = [
  'message.thinking',
  'message.reasoning',
  'message.reasoning_content',
  'message.thought',
  'thinking',
  'reasoning',
  'reasoning_content',
  'thought',
];
const CHAT_ANSWER_PATHS = [
  'message.content',
  'response',
];
const GENERATE_REASONING_PATHS = [
  'thinking',
  'reasoning',
  'reasoning_content',
  'message.thinking',
  'message.reasoning',
  'message.reasoning_content',
];
const GENERATE_ANSWER_PATHS = [
  'response',
  'message.content',
];
const webSearchUnavailableMessage = () => t('webSearch.unavailable');
const webSearchUnavailableAssistantMessage = () => t('webSearch.unavailableAssistant');
const WEB_SEARCH_CONTEXT_MAX_RESULTS = 5;
const WEB_SEARCH_CONTEXT_TITLE_MAX = 180;
const WEB_SEARCH_CONTEXT_SNIPPET_MAX = 700;
const WEB_SEARCH_CONTEXT_SOURCE_MAX = 120;
let isSendInFlight = false;
let systemPrompt = '';
let systemPromptLoadPromise = null;
let activeStreamRequest = null;
const getRaw = (k) => { try { return localStorage.getItem(k); } catch (_) { return null; } };
const setLS = (k, v) => { try { localStorage.setItem(k, v); } catch (_) {} };

function createAbortError() {
  if (typeof DOMException === 'function') {
    return new DOMException(t('status.cancelled'), 'AbortError');
  }
  const error = new Error(t('status.cancelled'));
  error.name = 'AbortError';
  return error;
}

export function isAbortError(error) {
  return Boolean(error && (error.name === 'AbortError' || error.code === 20));
}

function throwIfAborted(signal) {
  if (signal?.aborted) throw createAbortError();
}

function publishAbortController(controller) {
  if (typeof window === 'undefined') return;
  window.kivrioAbortController = controller || null;
}

function beginStreamRequest() {
  const controller = new AbortController();
  const request = { controller };
  activeStreamRequest = request;
  publishAbortController(controller);
  return request;
}

function endStreamRequest(request) {
  if (activeStreamRequest !== request) return;
  activeStreamRequest = null;
  if (typeof window !== 'undefined' && window.kivrioAbortController === request?.controller) {
    publishAbortController(null);
  }
}

function isRequestAborted(request) {
  return Boolean(request?.controller?.signal?.aborted);
}

function isCurrentRequest(request) {
  return activeStreamRequest === request && !isRequestAborted(request);
}

export function stopCurrentResponse() {
  const request = activeStreamRequest;
  if (!request) return false;
  try {
    request.controller?.abort?.();
  } catch (_) {}
  endStreamRequest(request);
  isSendInFlight = false;
  setSendButtonBusy(false);
  return true;
}

if (typeof window !== 'undefined') {
  window.kivrioStopCurrentResponse = stopCurrentResponse;
}

export function readBase() {
  const v = (getRaw(LS.base) || '').trim();
  if (!v || !/^(https?:)?\/\//i.test(v)) return 'http://127.0.0.1:11434';
  return v.replace(/\/+$/, '');
}

export async function listModels() {
  const base = readBase();
  const res = await fetch(`${base}/api/tags`, { headers: { Accept: 'application/json' } });
  if (!res.ok) throw new Error('/api/tags ' + res.status);

  const data = await res.json();
  const arr = Array.isArray(data) ? data : (data.models || []);
  return [...new Set(arr.map((m) => m.name || m.model).filter(Boolean))]
    .sort((a, b) => a.localeCompare(b));
}

export function readModel() {
  const v = (getRaw(LS.model) || '').trim();
  return v || 'gpt-oss:20b';
}

export function readSys() {
  return systemPrompt;
}

export async function loadSystemPrompt(force = false) {
  if (!force && systemPromptLoadPromise) return systemPromptLoadPromise;

  systemPromptLoadPromise = (async () => {
    const payload = await getSystemPrompt();
    systemPrompt = String(payload?.prompt || '');
    return systemPrompt;
  })();

  try {
    return await systemPromptLoadPromise;
  } catch (err) {
    systemPromptLoadPromise = null;
    throw err;
  }
}

export async function saveSystemPromptValue(prompt) {
  const payload = await saveSystemPrompt(prompt);
  systemPrompt = String(payload?.prompt || '');
  systemPromptLoadPromise = Promise.resolve(systemPrompt);
  return systemPrompt;
}

function readPathValue(obj, path) {
  return String(path || '')
    .split('.')
    .filter(Boolean)
    .reduce((acc, key) => (acc == null ? undefined : acc[key]), obj);
}

function coerceTextValue(value) {
  if (typeof value === 'string') return value;
  if (Array.isArray(value)) {
    return value.map((item) => coerceTextValue(item)).filter(Boolean).join('');
  }
  if (value && typeof value === 'object') {
    if (typeof value.text === 'string') return value.text;
    if (typeof value.content === 'string') return value.content;
  }
  return '';
}

function pickFirstString(obj, paths) {
  for (const path of (paths || [])) {
    const value = coerceTextValue(readPathValue(obj, path));
    if (value) return value;
  }
  return '';
}

function normalizeStreamChunk(obj, kind) {
  const reasoningChunk = pickFirstString(
    obj,
    kind === 'generate' ? GENERATE_REASONING_PATHS : CHAT_REASONING_PATHS,
  );
  const answerChunk = pickFirstString(
    obj,
    kind === 'generate' ? GENERATE_ANSWER_PATHS : CHAT_ANSWER_PATHS,
  );

  return {
    reasoningChunk,
    answerChunk,
  };
}

function createAssistantStreamState() {
  return {
    answerText: '',
    reasoningText: '',
    reasoningStartedAt: null,
    reasoningEndedAt: null,
    tagMode: 'answer',
    tagBuffer: '',
    nativeReasoningSeen: false,
  };
}

function markReasoningStarted(state) {
  if (state.reasoningStartedAt === null) state.reasoningStartedAt = Date.now();
}

function markReasoningEnded(state) {
  if (state.reasoningStartedAt !== null && state.reasoningEndedAt === null) {
    state.reasoningEndedAt = Date.now();
  }
}

function appendReasoningText(state, text) {
  const value = String(text || '');
  if (!value) return;
  markReasoningStarted(state);
  state.reasoningText += value;
}

function appendAnswerText(state, text) {
  const value = String(text || '');
  if (!value) return;
  if (state.reasoningStartedAt !== null && state.reasoningEndedAt === null) {
    markReasoningEnded(state);
  }
  state.answerText += value;
}

function partialTagSuffixLength(text, tag) {
  const source = String(text || '');
  for (let len = Math.min(source.length, tag.length - 1); len > 0; len -= 1) {
    if (tag.startsWith(source.slice(-len))) return len;
  }
  return 0;
}

function consumeTaggedAnswerChunk(state, chunk) {
  let input = state.tagBuffer + String(chunk || '');
  state.tagBuffer = '';
  let cursor = 0;

  while (cursor < input.length) {
    if (state.tagMode === 'reasoning') {
      const closeIdx = input.indexOf(THINK_END_TAG, cursor);
      if (closeIdx === -1) {
        const partialLength = partialTagSuffixLength(input.slice(cursor), THINK_END_TAG);
        const end = input.length - partialLength;
        appendReasoningText(state, input.slice(cursor, end));
        state.tagBuffer = input.slice(end);
        break;
      }

      appendReasoningText(state, input.slice(cursor, closeIdx));
      cursor = closeIdx + THINK_END_TAG.length;
      state.tagMode = 'answer';
      markReasoningEnded(state);
      continue;
    }

    const openIdx = input.indexOf(THINK_START_TAG, cursor);
    if (openIdx === -1) {
      const partialLength = partialTagSuffixLength(input.slice(cursor), THINK_START_TAG);
      const end = input.length - partialLength;
      appendAnswerText(state, input.slice(cursor, end));
      state.tagBuffer = input.slice(end);
      break;
    }

    appendAnswerText(state, input.slice(cursor, openIdx));
    cursor = openIdx + THINK_START_TAG.length;
    state.tagMode = 'reasoning';
  }
}

function mergeAssistantStreamChunk(state, chunk) {
  if (!chunk) return;

  if (chunk.reasoningChunk) {
    state.nativeReasoningSeen = true;
    appendReasoningText(state, chunk.reasoningChunk);
  }

  if (!chunk.answerChunk) return;
  if (state.nativeReasoningSeen) {
    appendAnswerText(state, chunk.answerChunk);
    return;
  }
  consumeTaggedAnswerChunk(state, chunk.answerChunk);
}

function buildAssistantPayload(state, { live = false } = {}) {
  const reasoningText = String(state?.reasoningText || '');
  const answerText = String(state?.answerText || '');
  let durationMs = null;
  if (state?.reasoningStartedAt !== null) {
    const endedAt = state.reasoningEndedAt ?? (live ? Date.now() : null);
    if (endedAt !== null) {
      durationMs = Math.max(1, endedAt - state.reasoningStartedAt);
    }
  }
  return {
    answerText,
    reasoningText,
    reasoningDurationMs: durationMs,
  };
}

function finalizeAssistantStreamState(state) {
  if (state.tagBuffer) {
    if (state.tagMode === 'reasoning') {
      appendReasoningText(state, state.tagBuffer);
    } else {
      appendAnswerText(state, state.tagBuffer);
    }
    state.tagBuffer = '';
  }
  if (state.reasoningStartedAt !== null && state.reasoningEndedAt === null) {
    markReasoningEnded(state);
  }
  return buildAssistantPayload(state);
}

export async function ping(base) {
  const res = await fetch(base + '/api/tags', { method: 'GET' });
  if (!res.ok) throw new Error('HTTP ' + res.status);
  return res.json();
}

function readHistory(convId) {
  const conversation = Store.get(convId);
  return Array.isArray(conversation?.messages) ? conversation.messages : [];
}

function toChatHistory(arr) {
  return (arr || [])
    .map((m) => {
      const role = (m.role || m.r || '').toLowerCase();
      const content = (m.content ?? m.text ?? '').toString();
      if (role === 'user' || role === 'assistant') return { role, content };
      return null;
    })
    .filter(Boolean);
}

function buildEffectiveSystemPrompt(sys, userText, convId, extraGuidance = '') {
  const base = String(sys || '').trim();
  const addition = String(extraGuidance || '').trim();
  if (!base && !addition) return '';
  return [base, addition].filter(Boolean).join('\n\n');
}

function cleanWebSearchContextText(value, maxLength) {
  const text = String(value == null ? '' : value)
    .replace(/<[^>]*>/g, ' ')
    .replace(/\s+/g, ' ')
    .replace(/\s+([.,;:!?])/g, '$1')
    .trim();
  if (!maxLength || text.length <= maxLength) return text;
  return text.slice(0, Math.max(0, maxLength - 1)).trimEnd() + '...';
}

function normalizeWebSearchContextUrl(value) {
  try {
    const url = new URL(String(value || '').trim());
    if (url.protocol !== 'http:' && url.protocol !== 'https:') return '';
    return url.href;
  } catch (_) {
    return '';
  }
}

export function buildWebSearchPromptContext(payload, { maxResults = WEB_SEARCH_CONTEXT_MAX_RESULTS } = {}) {
  if (!payload?.available) {
    return { available: false, sources: [], promptContext: '' };
  }

  const results = Array.isArray(payload.results) ? payload.results : [];
  const seenUrls = new Set();
  const sources = [];
  const limit = Math.max(0, Number(maxResults) || WEB_SEARCH_CONTEXT_MAX_RESULTS);

  for (const result of results) {
    if (sources.length >= limit) break;

    const url = normalizeWebSearchContextUrl(result?.url);
    if (!url) continue;

    const dedupeKey = url.toLowerCase();
    if (seenUrls.has(dedupeKey)) continue;
    seenUrls.add(dedupeKey);

    const title = cleanWebSearchContextText(result?.title || url, WEB_SEARCH_CONTEXT_TITLE_MAX);
    const snippet = cleanWebSearchContextText(result?.content || result?.snippet || '', WEB_SEARCH_CONTEXT_SNIPPET_MAX);
    const source = cleanWebSearchContextText(result?.engine || result?.source || '', WEB_SEARCH_CONTEXT_SOURCE_MAX);

    sources.push({
      index: sources.length + 1,
      title,
      url,
      snippet,
      source,
    });
  }

  if (!sources.length) {
    return { available: false, sources: [], promptContext: '' };
  }

  const lines = [t('webSearch.contextTitle')];
  for (const source of sources) {
    lines.push('');
    lines.push(`[${source.index}] ${source.title}`);
    if (source.source) lines.push(t('webSearch.source', { source: source.source }));
    lines.push(t('webSearch.url', { url: source.url }));
    if (source.snippet) lines.push(t('webSearch.excerpt', { snippet: source.snippet }));
  }

  return {
    available: true,
    sources,
    promptContext: lines.join('\n'),
  };
}

export function buildPromptWithWebSearchContext(userPrompt, webPromptContext) {
  const prompt = String(userPrompt || '').trim();
  const context = String(webPromptContext || '').trim();
  if (!context) return prompt;

  return [
    t('webSearch.userQuestion'),
    prompt,
    '',
    context,
    '',
    t('webSearch.answerInstructions'),
    t('webSearch.instructionUseContext'),
    t('webSearch.instructionCite'),
    t('webSearch.instructionLimits'),
  ].join('\n');
}

export function buildWebSearchUnavailableAssistantMessage(message = '') {
  const detail = String(message || '').trim();
  if (!detail || detail === webSearchUnavailableMessage()) return webSearchUnavailableAssistantMessage();
  return `${webSearchUnavailableAssistantMessage()}\n\n${t('webSearch.technicalDetail', { detail })}`;
}

export function shouldBlockModelForUnavailableWebSearch(webSearchRequested, webPromptContext) {
  if (!webSearchRequested || webPromptContext?.aborted) return false;
  return !String(webPromptContext?.promptContext || '').trim();
}

function buildChatMessages({ sys, convId, userText, historyUserText = userText, maxPast = 16, images = [], extraSystemGuidance = '' }) {
  const out = [];
  const history = toChatHistory(readHistory(convId));
  const effectiveSys = buildEffectiveSystemPrompt(sys, userText, convId, extraSystemGuidance);

  let hist = history.slice();
  if (hist.length) {
    const last = hist[hist.length - 1];
    if (last.role === 'user' && last.content === historyUserText) {
      hist = hist.slice(0, -1);
    }
  }

  const trimmed = hist.slice(-maxPast);

  if (effectiveSys) out.push({ role: 'system', content: effectiveSys });
  for (const message of trimmed) out.push({ role: message.role, content: message.content });

  const current = { role: 'user', content: userText };
  if (images.length) current.images = images;
  out.push(current);
  return out;
}

function buildGeneratePrompt({ sys, convId, userText, historyUserText = userText, maxPast = 16, extraSystemGuidance = '' }) {
  let history = toChatHistory(readHistory(convId));
  if (history.length) {
    const last = history[history.length - 1];
    if (last.role === 'user' && last.content === historyUserText) {
      history = history.slice(0, -1);
    }
  }
  history = history.slice(-maxPast);
  const parts = [];
  const effectiveSys = buildEffectiveSystemPrompt(sys, userText, convId, extraSystemGuidance);
  if (effectiveSys) parts.push(`System:\n${effectiveSys}`);
  for (const message of history) {
    parts.push((message.role === 'user' ? 'User' : 'Assistant') + ':\n' + message.content);
  }
  parts.push('User:\n' + userText);
  parts.push('Assistant:');
  return parts.join('\n\n');
}

export async function* streamChat({ base, model, sys, prompt, convId, historyUserText = prompt, maxPast = 16, images = [], extraSystemGuidance = '', signal = null }) {
  throwIfAborted(signal);
  const body = {
    model,
    messages: buildChatMessages({ sys, convId, userText: prompt, historyUserText, maxPast, images, extraSystemGuidance }),
    stream: true,
  };
  const res = await fetch(base + '/api/chat', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
    signal,
  });

  if ((res.status === 404 || res.status === 400) && !images.length) {
    return yield* streamGenerate({ base, model, sys, prompt, convId, historyUserText, maxPast, extraSystemGuidance, signal });
  }
  if (!res.ok) throw new Error('HTTP ' + res.status);

  const reader = res.body.getReader();
  const dec = new TextDecoder();
  let buf = '';

  while (true) {
    throwIfAborted(signal);
    const { value, done } = await reader.read();
    if (done) break;
    buf += dec.decode(value, { stream: true });
    let idx;
    while ((idx = buf.indexOf('\n')) >= 0) {
      const line = buf.slice(0, idx).trim();
      buf = buf.slice(idx + 1);
      if (!line) continue;
      try {
        const obj = JSON.parse(line);
        yield normalizeStreamChunk(obj, 'chat');
        if (obj.done) return;
      } catch (_) {}
    }
  }

  const tail = buf.trim();
  if (!tail) return;
  try {
    yield normalizeStreamChunk(JSON.parse(tail), 'chat');
  } catch (_) {}
}

export async function* streamGenerate({ base, model, sys, prompt, convId, historyUserText = prompt, maxPast = 16, extraSystemGuidance = '', signal = null }) {
  throwIfAborted(signal);
  const effectiveSys = buildEffectiveSystemPrompt(sys, prompt, convId, extraSystemGuidance);
  const res = await fetch(base + '/api/generate', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({
      model,
      system: effectiveSys || undefined,
      prompt: buildGeneratePrompt({ sys, convId, userText: prompt, historyUserText, maxPast, extraSystemGuidance }),
      stream: true,
    }),
    signal,
  });
  if (!res.ok) throw new Error('HTTP ' + res.status + ' (/api/generate)');

  const reader = res.body.getReader();
  const dec = new TextDecoder();
  let buf = '';

  while (true) {
    throwIfAborted(signal);
    const { value, done } = await reader.read();
    if (done) break;
    buf += dec.decode(value, { stream: true });
    let idx;
    while ((idx = buf.indexOf('\n')) >= 0) {
      const line = buf.slice(0, idx).trim();
      buf = buf.slice(idx + 1);
      if (!line) continue;
      try {
        const obj = JSON.parse(line);
        yield normalizeStreamChunk(obj, 'generate');
        if (obj.done) return;
      } catch (_) {}
    }
  }

  const tail = buf.trim();
  if (!tail) return;
  try {
    yield normalizeStreamChunk(JSON.parse(tail), 'generate');
  } catch (_) {}
}

function renderAssistantChunk(target, payload, options = {}) {
  const answerText = payload?.answerText ?? '';
  updateBubbleContent(target, 'assistant', answerText, {
    ...options,
    answerText,
    reasoningText: payload?.reasoningText ?? '',
    reasoningDurationMs: payload?.reasoningDurationMs ?? null,
  });
}

function renderConversationSnapshot(conversation) {
  const log = qs('#chat-log');
  if (log) log.innerHTML = '';

  for (const message of (conversation?.messages || [])) {
    renderMsg(message.role, message.content, {
      messageId: message.id,
      conversationId: message.conversationId,
      attachments: message.attachments || [],
      webSources: message.webSources || [],
      reasoningText: message.reasoningText,
      model: message.model,
      reasoningDurationMs: message.reasoningDurationMs,
    });
  }
}

function setSendButtonBusy(isBusy) {
  const btn = qs('#send-btn');
  if (!(btn instanceof HTMLButtonElement)) return;
  btn.disabled = false;
  btn.classList.toggle('is-busy', isBusy);
  btn.setAttribute('aria-busy', isBusy ? 'true' : 'false');
  const titleKey = isBusy ? 'composer.stopTitle' : 'composer.sendTitle';
  btn.dataset.i18nTitle = titleKey;
  btn.dataset.i18nAriaLabel = titleKey;
  btn.title = t(titleKey);
  btn.setAttribute('aria-label', t(titleKey));
}

async function resolveWebSearchPromptContextForCurrentMessage(query, { signal = null } = {}) {
  const trimmed = String(query || '').trim();
  if (!trimmed) {
    return {
      available: false,
      sources: [],
      promptContext: '',
      assistantMessage: buildWebSearchUnavailableAssistantMessage(),
    };
  }

  try {
    const payload = await webSearch(trimmed, { maxResults: WEB_SEARCH_CONTEXT_MAX_RESULTS, signal });
    const context = buildWebSearchPromptContext(payload);
    if (context.available) return context;

    const message = String(payload?.message || webSearchUnavailableMessage()).trim();
    if (message) {
      showToast(message, { tone: 'info' });
    }
    return {
      available: false,
      sources: [],
      promptContext: '',
      assistantMessage: buildWebSearchUnavailableAssistantMessage(),
    };
  } catch (err) {
    if (isAbortError(err)) return { available: false, sources: [], promptContext: '', aborted: true };
    const message = userMessageForError(err, webSearchUnavailableMessage());
    showToast(message, { tone: 'info' });
    return {
      available: false,
      sources: [],
      promptContext: '',
      assistantMessage: buildWebSearchUnavailableAssistantMessage(),
    };
  }
}

export async function regenerateFromEditedMessage({ conversationId, messageId, content }) {
  if (!conversationId || messageId == null) {
    throw new Error('Message introuvable.');
  }
  if (isSendInFlight) {
    throw new Error('Un traitement est deja en cours.');
  }

  isSendInFlight = true;
  setSendButtonBusy(true);
  const streamRequest = beginStreamRequest();

  let aiB = null;
  try {
    const rewrittenConversation = await Store.rewriteFromMessage(conversationId, messageId, {
      content,
      truncate_following: true,
    });
    if (!isCurrentRequest(streamRequest)) return Store.get(conversationId) || rewrittenConversation;
    const conversation = await Store.fetch(conversationId).catch(() => rewrittenConversation);
    if (!isCurrentRequest(streamRequest)) return Store.get(conversationId) || conversation;
    renderConversationSnapshot(conversation);
    try { await mountHistory(); } catch (_) {}
    if (!isCurrentRequest(streamRequest)) return Store.get(conversationId) || conversation;

    const messages = Array.isArray(conversation?.messages) ? conversation.messages : [];
    const lastMessage = messages[messages.length - 1] || null;
    if (!lastMessage || lastMessage.role !== 'user' || !String(lastMessage.content || '').trim()) {
      return conversation;
    }

    const model = readModel();
    const base = readBase();
    let sys = '';
    try {
      await loadSystemPrompt();
      sys = readSys();
    } catch (err) {
      const msg = assistantErrorMessage(err, t('prompt.loadError'));
      aiB = renderMsg('assistant', msg, { model });
      const savedError = await Store.addMsg(conversationId, 'assistant', msg, { model });
      bindMessageRecord(aiB, savedError);
      try { await mountHistory(); } catch (_) {}
      return Store.get(conversationId) || conversation;
    }

    aiB = renderMsg('assistant', '', { model });
    const assistantState = createAssistantStreamState();

    try {
      for await (const chunk of streamChat({
        base,
        model,
        sys,
        prompt: lastMessage.content,
        convId: conversationId,
        images: [],
        signal: streamRequest.controller.signal,
      })) {
        if (!isCurrentRequest(streamRequest)) return Store.get(conversationId) || conversation;
        mergeAssistantStreamChunk(assistantState, chunk);
        const livePayload = buildAssistantPayload(assistantState, { live: true });
        if (!livePayload.answerText.trim() && !livePayload.reasoningText.trim()) continue;
        renderAssistantChunk(aiB, livePayload, { model });
      }

      if (!isCurrentRequest(streamRequest)) return Store.get(conversationId) || conversation;
      const finalPayload = finalizeAssistantStreamState(assistantState);
      if (finalPayload.answerText.trim() || finalPayload.reasoningText.trim()) {
        renderAssistantChunk(aiB, finalPayload, { model });
      }
      if (finalPayload.answerText.trim() || finalPayload.reasoningText.trim()) {
        const savedAssistantMessage = await Store.addMsg(conversationId, 'assistant', finalPayload.answerText, {
          reasoningText: finalPayload.reasoningText,
          model,
          reasoningDurationMs: finalPayload.reasoningDurationMs,
        });
        bindMessageRecord(aiB, savedAssistantMessage);
      }
      try { await mountHistory(); } catch (_) {}
      return Store.get(conversationId) || conversation;
    } catch (err) {
      if (isAbortError(err) || isRequestAborted(streamRequest)) {
        return Store.get(conversationId) || conversation;
      }
      const msg = assistantErrorMessage(err, t('errors.generationImpossible'));
      renderAssistantChunk(aiB, { answerText: msg, reasoningText: '', reasoningDurationMs: null }, { model });
      const savedError = await Store.addMsg(conversationId, 'assistant', msg, { model });
      bindMessageRecord(aiB, savedError);
      try { await mountHistory(); } catch (_) {}
      return Store.get(conversationId) || conversation;
    }
  } finally {
    const shouldResetSendState = activeStreamRequest === streamRequest;
    endStreamRequest(streamRequest);
    if (shouldResetSendState) {
      isSendInFlight = false;
      setSendButtonBusy(false);
    }
  }
}

export async function sendCurrent() {
  const ta = qs('#composer-input');
  if (!ta) {
    showToast(t('status.inputMissing'));
    return;
  }
  if (isSendInFlight) return;

  const text = (ta.value || '').trim();
  const pendingUploads = getPendingUploads();
  if (!text && !pendingUploads.length) return;
  const model = readModel();
  let sys = '';
  try {
    await loadSystemPrompt();
    sys = readSys();
  } catch (err) {
    showToast(userMessageForError(err, t('prompt.loadError')));
    return;
  }
  const detachedUploads = pendingUploads.length ? detachPendingUploads() : [];
  const localAttachments = detachedUploads.map((item) => ({
    filename: item?.file?.name || t('chat.attachment'),
    mimeType: item?.file?.type || '',
    sizeBytes: Number(item?.file?.size || 0),
    previewUrl: item?.objectUrl || null,
    url: item?.objectUrl || null,
    isImage: item?.kind === 'image',
    isPdf: item?.kind === 'pdf',
  }));
  let shouldReleaseDetachedUploads = true;

  isSendInFlight = true;
  setSendButtonBusy(true);
  const streamRequest = beginStreamRequest();

  try {
    const userBubble = renderMsg('user', text, { attachments: localAttachments });
    ta.value = '';
    const webSearchRequested = consumeWebSearchSelection();

    if (window.kivrioEnsureConversationPromise) {
      try { await window.kivrioEnsureConversationPromise; } catch (_) {}
    }
    if (!isCurrentRequest(streamRequest)) return;

    const base = readBase();
    let aiB = null;

    let convId = Store.currentId?.() || null;
    if (convId) {
      try {
        const existingConversation = await Store.ensureLoaded(convId);
        if (!existingConversation?.id) throw new Error('Conversation not found');
      } catch (_) {
        try { Store.clearCurrent?.(); } catch (_) {}
        convId = null;
      }
    }
    if (!isCurrentRequest(streamRequest)) return;
    if (!convId && Store.create) {
      const conversation = await Store.create(t('sidebar.newConversationTitle'));
      convId = conversation.id;
    }
    if (!isCurrentRequest(streamRequest)) return;
    if (!convId) {
      const message = t('chat.createConversationImpossible');
      if (detachedUploads.length) {
        restorePendingUploads(detachedUploads, message);
        shouldReleaseDetachedUploads = false;
      }
      showToast(message);
      return;
    }

    let uploadedAttachments = [];
    if (detachedUploads.length) {
      try {
        uploadedAttachments = await uploadConversationAttachments(convId, detachedUploads.map((item) => item.file));
      } catch (err) {
        const message = userMessageForError(err, t('uploads.uploadImpossible'));
        restorePendingUploads(detachedUploads, message);
        shouldReleaseDetachedUploads = false;
        if (aiB) {
          renderAssistantChunk(aiB, { answerText: message }, { model });
        } else {
          showToast(message);
        }
        return;
      }
    }
    if (!isCurrentRequest(streamRequest)) return;

    try {
      const savedUserMessage = await Store.addMsg(convId, 'user', text, {
        attachmentIds: uploadedAttachments.map((item) => item.id),
      });
      bindMessageRecord(userBubble, savedUserMessage);
    } catch (_) {
    }
    if (!isCurrentRequest(streamRequest)) return;

    const prepared = await preparePendingUploadsForSend({
      model,
      userText: text,
      items: detachedUploads,
      uploadedAttachments,
    });
    if (!prepared.ok) {
      const message = prepared.message || t('uploads.cannotSend');
      if (aiB) {
        renderAssistantChunk(aiB, { answerText: message }, { model });
      } else {
        showToast(message);
      }
      return;
    }
    if (!isCurrentRequest(streamRequest)) return;

    let promptForModel = prepared.promptText || text;
    let webSourcesForAssistant = [];
    if (webSearchRequested) {
      const webPromptContext = await resolveWebSearchPromptContextForCurrentMessage(text, {
        signal: streamRequest.controller.signal,
      });
      if (webPromptContext.aborted) return;
      if (webPromptContext.promptContext) {
        promptForModel = buildPromptWithWebSearchContext(promptForModel, webPromptContext.promptContext);
        webSourcesForAssistant = Array.isArray(webPromptContext.sources) ? webPromptContext.sources : [];
      } else if (shouldBlockModelForUnavailableWebSearch(webSearchRequested, webPromptContext)) {
        const message = webPromptContext.assistantMessage || buildWebSearchUnavailableAssistantMessage();
        if (!aiB) {
          aiB = renderMsg('assistant', message, { model });
        } else {
          renderAssistantChunk(aiB, { answerText: message, reasoningText: '', reasoningDurationMs: null }, { model });
        }
        if (convId) {
          const savedAssistantMessage = await Store.addMsg(convId, 'assistant', message, {
            model,
            webSources: [],
          });
          bindMessageRecord(aiB, savedAssistantMessage);
        }
        try { await mountHistory(); } catch (_) {}
        return;
      }
    }
    if (!isCurrentRequest(streamRequest)) return;

    try {
      await Store.renameIfDefault(convId, fmtTitle(prepared.suggestedTitle || text || t('chat.attachment')));
    } catch (_) {}
    try {
      await mountHistory();
    } catch (_) {}

    if (!aiB) aiB = renderMsg('assistant', '', { model });
    const assistantState = createAssistantStreamState();
    try {
      for await (const chunk of streamChat({
        base,
        model,
        sys,
        prompt: promptForModel,
        historyUserText: text,
        convId,
        images: prepared.imagePayloads || [],
        signal: streamRequest.controller.signal,
      })) {
        if (!isCurrentRequest(streamRequest)) return;
        mergeAssistantStreamChunk(assistantState, chunk);
        const livePayload = buildAssistantPayload(assistantState, { live: true });
        if (!livePayload.answerText.trim() && !livePayload.reasoningText.trim()) continue;
        renderAssistantChunk(aiB, livePayload, { model, webSources: webSourcesForAssistant });
      }
      if (!isCurrentRequest(streamRequest)) return;
      const finalPayload = finalizeAssistantStreamState(assistantState);
      if (finalPayload.answerText.trim() || finalPayload.reasoningText.trim()) {
        renderAssistantChunk(aiB, finalPayload, { model, webSources: webSourcesForAssistant });
      }
      if (convId && (finalPayload.answerText.trim() || finalPayload.reasoningText.trim())) {
        const savedAssistantMessage = await Store.addMsg(convId, 'assistant', finalPayload.answerText, {
          reasoningText: finalPayload.reasoningText,
          model,
          reasoningDurationMs: finalPayload.reasoningDurationMs,
          webSources: webSourcesForAssistant,
        });
        bindMessageRecord(aiB, savedAssistantMessage);
      }
      try { await mountHistory(); } catch (_) {}
    } catch (err) {
      if (isAbortError(err) || isRequestAborted(streamRequest)) {
        return;
      }
      const msg = assistantErrorMessage(err, t('errors.generationImpossible'));
      renderAssistantChunk(aiB, { answerText: msg, reasoningText: '', reasoningDurationMs: null }, { model });
      if (convId) await Store.addMsg(convId, 'assistant', msg, { model });
      try { await mountHistory(); } catch (_) {}
      console.warn('Fetch error', err);
    }
  } finally {
    const shouldResetSendState = activeStreamRequest === streamRequest;
    endStreamRequest(streamRequest);
    if (shouldReleaseDetachedUploads) releaseUploadItems(detachedUploads);
    if (shouldResetSendState) {
      isSendInFlight = false;
      setSendButtonBusy(false);
    }
  }
}

if (typeof document !== 'undefined' && typeof document.addEventListener === 'function') {
  document.addEventListener('settings:model-changed', (e) => {
    const model = (e.detail || '').trim();
    if (model) setLS(LS.model, model);
  });
}
