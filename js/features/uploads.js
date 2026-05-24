import { canModelReadFiles } from '../config/file-capable-models.js';
import { getAttachmentText } from '../net/conversationsApi.js';
import { t } from '../i18n/i18n.js';

const LIMITS = {
  image: 10 * 1024 * 1024,
  pdf: 20 * 1024 * 1024,
  text: 2 * 1024 * 1024,
  total: 25 * 1024 * 1024,
  count: 5,
};

const state = {
  items: [],
  addBtn: null,
  addMenu: null,
  addFileMenuItem: null,
  webSearchMenuItem: null,
  webSearchIndicator: null,
  webSearchClear: null,
  fileInput: null,
  list: null,
  error: null,
  webSearchEnabled: false,
};

function makeId() {
  if (typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function') {
    return crypto.randomUUID();
  }
  return `att-${Date.now()}-${Math.random().toString(16).slice(2, 10)}`;
}

function getExt(name) {
  const parts = String(name || '').split('.');
  return parts.length > 1 ? parts.at(-1).toLowerCase() : '';
}

function kindForFile(file) {
  const type = String(file?.type || '').toLowerCase();
  const ext = getExt(file?.name || '');
  if (type.startsWith('image/') || ['jpg', 'jpeg', 'png', 'webp'].includes(ext)) return 'image';
  if (type === 'application/pdf' || ext === 'pdf') return 'pdf';
  if (type.startsWith('text/') || ['txt', 'md'].includes(ext)) return 'text';
  return 'unsupported';
}

function formatBytes(bytes) {
  const value = Number(bytes || 0);
  if (value < 1024) return `${value} o`;
  if (value < 1024 * 1024) return `${Math.round(value / 1024)} Ko`;
  return `${(value / (1024 * 1024)).toFixed(value >= 10 * 1024 * 1024 ? 0 : 1)} Mo`;
}

function totalBytes(items = state.items) {
  return items.reduce((sum, item) => sum + Number(item.file?.size || 0), 0);
}

function setError(message = '') {
  if (!state.error) return;
  state.error.textContent = message;
  state.error.hidden = !message;
}

function statusLabel(item) {
  if (item.status === 'uploading') return t('uploads.statusUploading');
  if (item.status === 'error') return item.error || t('common.error');
  return t('uploads.statusReady');
}

function makeFileBadge(item) {
  const badge = document.createElement('div');
  badge.className = 'pending-attachment-thumb';
  if (item.kind === 'image' && item.objectUrl) {
    const img = new Image();
    img.src = item.objectUrl;
    img.alt = item.file.name;
    badge.appendChild(img);
    return badge;
  }

  const label = document.createElement('span');
  label.textContent = (getExt(item.file.name || '') || 'FILE').slice(0, 4).toUpperCase();
  badge.appendChild(label);
  return badge;
}

function renderPendingUploads() {
  if (!state.list) return;
  state.list.innerHTML = '';
  for (const item of state.items) {
    const card = document.createElement('div');
    card.className = 'pending-attachment';
    if (item.status === 'error') card.classList.add('is-error');

    const preview = makeFileBadge(item);
    const meta = document.createElement('div');
    meta.className = 'pending-attachment-meta';

    const name = document.createElement('div');
    name.className = 'pending-attachment-name';
    name.textContent = item.file.name;

    const info = document.createElement('div');
    info.className = 'pending-attachment-info';
    info.textContent = `${item.kind.toUpperCase()} - ${formatBytes(item.file.size)}`;

    const status = document.createElement('div');
    status.className = 'pending-attachment-status';
    status.textContent = statusLabel(item);

    const remove = document.createElement('button');
    remove.type = 'button';
    remove.className = 'pending-attachment-remove';
    remove.dataset.id = item.id;
    remove.textContent = t('uploads.remove');
    remove.disabled = item.status === 'uploading';

    meta.append(name, info, status);
    card.append(preview, meta, remove);
    state.list.appendChild(card);
  }
}

function pushFiles(files) {
  const next = [...state.items];
  let error = '';

  for (const file of files) {
    const kind = kindForFile(file);
    if (kind === 'unsupported') {
      error = t('uploads.unsupportedType', { name: file.name });
      continue;
    }
    if (next.length >= LIMITS.count) {
      error = t('uploads.maxFiles', { count: LIMITS.count });
      break;
    }
    if (file.size > LIMITS[kind]) {
      error = t('uploads.tooLarge', { name: file.name });
      continue;
    }
    if (totalBytes([...next, { file }]) > LIMITS.total) {
      error = t('uploads.totalTooLarge');
      break;
    }
    next.push({
      id: makeId(),
      file,
      kind,
      status: 'selected',
      error: '',
      objectUrl: kind === 'image' ? URL.createObjectURL(file) : null,
    });
  }

  state.items = next;
  setError(error);
  renderPendingUploads();
}

function removeItem(id) {
  const idx = state.items.findIndex((item) => item.id === id);
  if (idx < 0) return;
  const [removed] = state.items.splice(idx, 1);
  if (removed?.objectUrl) URL.revokeObjectURL(removed.objectUrl);
  setError('');
  renderPendingUploads();
}

function readFileAsBase64(file) {
  return new Promise((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => {
      const raw = String(reader.result || '');
      const comma = raw.indexOf(',');
      resolve(comma >= 0 ? raw.slice(comma + 1) : raw);
    };
    reader.onerror = () => reject(reader.error || new Error(t('uploads.readImpossible', { name: file.name })));
    reader.readAsDataURL(file);
  });
}

function appendPromptBlock(promptText, label, blocks) {
  if (!blocks.length) return promptText;
  const prefix = promptText ? '\n\n' : '';
  return `${promptText}${prefix}${label}\n\n${blocks.join('\n\n---\n\n')}`;
}

function textFragmentsToPromptBlocks(textFragments) {
  return textFragments.map((item) => [t('uploads.fileBlockLabel', { name: item.name }), item.content].join('\n'));
}

function isPdfAttachmentRecord(attachment) {
  const mime = String(attachment?.mimeType || '').toLowerCase();
  const name = String(attachment?.filename || '').toLowerCase();
  return Boolean(attachment?.isPdf) || mime === 'application/pdf' || name.endsWith('.pdf');
}

function findUploadedAttachmentForItem(item, uploadedAttachments, usedIndexes) {
  const itemName = String(item?.file?.name || '').toLowerCase();
  for (let index = 0; index < uploadedAttachments.length; index += 1) {
    if (usedIndexes.has(index)) continue;
    const attachment = uploadedAttachments[index];
    if (!isPdfAttachmentRecord(attachment)) continue;
    const attachmentName = String(attachment?.filename || '').toLowerCase();
    if (!itemName || !attachmentName || itemName === attachmentName) {
      usedIndexes.add(index);
      return attachment;
    }
  }
  return null;
}

async function readPdfTextFragments(pdfItems, uploadedAttachments, onStatus) {
  const fragments = [];
  const usedIndexes = new Set();
  const records = Array.isArray(uploadedAttachments) ? uploadedAttachments : [];

  for (const item of pdfItems) {
    const attachment = findUploadedAttachmentForItem(item, records, usedIndexes);
    const name = attachment?.filename || item?.file?.name || 'document.pdf';
    if (!attachment?.id && !attachment?.textUrl) {
      return {
        ok: false,
        message: t('uploads.pdfReadUnavailable', { name }),
      };
    }

    try {
      if (typeof onStatus === 'function') onStatus(t('uploads.pdfReading', { name }));
      const payload = await getAttachmentText(attachment);
      const content = String(payload?.text || '').trim();
      if (!content) {
        return {
          ok: false,
          message: t('uploads.pdfNoText', { name }),
        };
      }
      fragments.push({ name, content });
    } catch (_) {
      return {
        ok: false,
        message: t('uploads.pdfReadImpossible', { name }),
      };
    }
  }

  return { ok: true, fragments };
}

function defaultPromptForAttachments({ mode, userText }) {
  const trimmed = String(userText || '').trim();
  if (trimmed) return trimmed;
  if (mode === 'image') return t('uploads.promptImage');
  return t('uploads.promptDocument');
}

function setAddMenuOpen(open) {
  if (!state.addMenu || !state.addBtn) return;
  state.addMenu.hidden = !open;
  state.addMenu.classList.toggle('is-open', open);
  state.addBtn.setAttribute('aria-expanded', open ? 'true' : 'false');
}

function openFilePicker() {
  if (!state.fileInput) return;
  if (typeof state.fileInput.showPicker === 'function') {
    state.fileInput.showPicker();
    return;
  }
  state.fileInput.click();
}

function renderWebSearchSelection() {
  if (state.webSearchMenuItem) {
    state.webSearchMenuItem.classList.toggle('is-active', state.webSearchEnabled);
    state.webSearchMenuItem.setAttribute('aria-checked', state.webSearchEnabled ? 'true' : 'false');
  }
  if (state.webSearchIndicator) {
    state.webSearchIndicator.hidden = !state.webSearchEnabled;
  }
}

function clearWebSearchSelection() {
  state.webSearchEnabled = false;
  renderWebSearchSelection();
}

export function isWebSearchEnabled() {
  return state.webSearchEnabled;
}

export function consumeWebSearchSelection() {
  return state.webSearchEnabled;
}

export function wireUploads() {
  state.addBtn = document.getElementById('add-btn');
  state.addMenu = document.getElementById('composer-add-menu');
  state.addFileMenuItem = document.getElementById('add-file-menu-item');
  state.webSearchMenuItem = document.getElementById('web-search-menu-item');
  state.webSearchIndicator = document.getElementById('web-search-indicator');
  state.webSearchClear = document.getElementById('web-search-clear');
  state.fileInput = document.getElementById('file-input');
  state.list = document.getElementById('composer-attachments');
  state.error = document.getElementById('composer-upload-error');
  if (!state.addBtn || !state.fileInput || !state.list || !state.error) return;

  state.addBtn.addEventListener('click', (event) => {
    event.preventDefault();
    setAddMenuOpen(state.addMenu ? state.addMenu.hidden : false);
  });
  if (state.addFileMenuItem) {
    state.addFileMenuItem.addEventListener('click', () => {
      setAddMenuOpen(false);
      openFilePicker();
    });
  } else {
    state.addBtn.addEventListener('dblclick', openFilePicker);
  }
  if (state.webSearchMenuItem) {
    state.webSearchMenuItem.addEventListener('click', () => {
      state.webSearchEnabled = !state.webSearchEnabled;
      renderWebSearchSelection();
      setAddMenuOpen(false);
    });
  }
  if (state.webSearchClear) {
    state.webSearchClear.addEventListener('click', clearWebSearchSelection);
  }
  document.addEventListener('click', (event) => {
    const target = event.target;
    if (!(target instanceof Node)) return;
    if (state.addBtn?.contains(target) || state.addMenu?.contains(target)) return;
    setAddMenuOpen(false);
  });
  document.addEventListener('keydown', (event) => {
    if (event.key === 'Escape') setAddMenuOpen(false);
  });
  state.fileInput.addEventListener('change', () => {
    const files = Array.from(state.fileInput.files || []);
    if (files.length) pushFiles(files);
    state.fileInput.value = '';
  });
  state.list.addEventListener('click', (event) => {
    const target = event.target;
    if (!(target instanceof HTMLElement)) return;
    const id = target.dataset.id;
    if (!id) return;
    removeItem(id);
  });

  renderPendingUploads();
  renderWebSearchSelection();
  document.addEventListener('i18n:language-changed', () => {
    renderPendingUploads();
    renderWebSearchSelection();
  });
}

export function hasPendingUploads() {
  return state.items.length > 0;
}

export function getPendingUploads() {
  return state.items.slice();
}

export function detachPendingUploads() {
  const items = state.items.slice();
  state.items = [];
  setError('');
  renderPendingUploads();
  return items;
}

export function restorePendingUploads(items, error = '') {
  state.items = Array.isArray(items) ? items.slice() : [];
  setError(error);
  renderPendingUploads();
}

export function releaseUploadItems(items) {
  for (const item of (items || [])) {
    if (item?.objectUrl) URL.revokeObjectURL(item.objectUrl);
  }
}

export function clearPendingUploads() {
  for (const item of state.items) {
    if (item.objectUrl) URL.revokeObjectURL(item.objectUrl);
  }
  state.items = [];
  setError('');
  renderPendingUploads();
}

export function setPendingUploadsState(status, error = '') {
  state.items = state.items.map((item) => ({
    ...item,
    status,
    error: status === 'error' ? error : '',
  }));
  setError(status === 'error' ? error : '');
  renderPendingUploads();
}

export async function preparePendingUploadsForSend({ model, userText, onStatus, items: providedItems, uploadedAttachments = [] }) {
  const items = Array.isArray(providedItems) ? providedItems.slice() : getPendingUploads();
  if (!items.length) {
    return {
      ok: true,
      promptText: String(userText || '').trim(),
      imagePayloads: [],
      suggestedTitle: String(userText || '').trim(),
    };
  }

  const imageItems = items.filter((item) => item.kind === 'image');
  const pdfItems = items.filter((item) => item.kind === 'pdf');
  const textItems = items.filter((item) => item.kind === 'text');
  const textFragments = [];
  for (const item of textItems) {
    const content = (await item.file.text()).trim();
    if (!content) continue;
    textFragments.push({ name: item.file.name, content });
  }
  if (pdfItems.length) {
    const pdfResult = await readPdfTextFragments(pdfItems, uploadedAttachments, onStatus);
    if (!pdfResult.ok) return pdfResult;
    textFragments.push(...pdfResult.fragments);
  }

  const imagePayloads = [];
  let promptText = String(userText || '').trim();

  if (canModelReadFiles(model)) {
    for (const item of imageItems) {
      imagePayloads.push(await readFileAsBase64(item.file));
    }
    if (!promptText && (imagePayloads.length || textFragments.length)) {
      promptText = defaultPromptForAttachments({
        mode: imagePayloads.length ? 'image' : 'text',
        userText,
      });
    }
  } else if (imageItems.length) {
    return {
      ok: false,
      message: t('uploads.imagesNeedMultimodal'),
    };
  } else if (!promptText && textFragments.length) {
    promptText = defaultPromptForAttachments({ mode: 'text', userText });
  }

  if (textFragments.length) {
    promptText = appendPromptBlock(
      promptText,
      t('uploads.attachedFilesContent'),
      textFragmentsToPromptBlocks(textFragments),
    );
  }

  return {
    ok: true,
    promptText,
    imagePayloads,
    suggestedTitle: String(userText || '').trim() || items[0]?.file?.name || t('chat.attachment'),
  };
}

if (typeof window !== 'undefined') {
  window.kivrioClearPendingUploads = clearPendingUploads;
  window.kivrioIsWebSearchEnabled = isWebSearchEnabled;
  window.kivrioClearWebSearchSelection = clearWebSearchSelection;
}
