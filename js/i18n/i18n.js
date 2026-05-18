import { DEFAULT_LANGUAGE, LANGUAGE_STORAGE_KEY, translations } from './translations.js';

let currentLanguage = DEFAULT_LANGUAGE;
const listeners = new Set();

function readSavedLanguage() {
  try {
    return localStorage.getItem(LANGUAGE_STORAGE_KEY);
  } catch (_) {
    return null;
  }
}

function persistLanguage(language) {
  try {
    localStorage.setItem(LANGUAGE_STORAGE_KEY, language);
  } catch (_) {}
}

function hasLanguage(language) {
  return Boolean(translations[language]);
}

function resolveKey(dictionary, key) {
  return String(key || '')
    .split('.')
    .reduce((value, part) => (value && Object.prototype.hasOwnProperty.call(value, part) ? value[part] : undefined), dictionary);
}

function interpolate(value, params = {}) {
  return String(value).replace(/\{\{(\w+)\}\}/g, (_, name) => {
    const next = params[name];
    return next == null ? '' : String(next);
  });
}

export function getLanguage() {
  return currentLanguage;
}

export function t(key, params = {}) {
  const value = resolveKey(translations[currentLanguage], key)
    ?? resolveKey(translations[DEFAULT_LANGUAGE], key)
    ?? key;
  return interpolate(value, params);
}

export function onLanguageChange(callback) {
  if (typeof callback !== 'function') return () => {};
  listeners.add(callback);
  return () => listeners.delete(callback);
}

function applyStaticTranslations(root = document) {
  if (!root?.querySelectorAll) return;

  root.querySelectorAll('[data-i18n]').forEach((node) => {
    node.textContent = t(node.dataset.i18n);
  });

  root.querySelectorAll('[data-i18n-placeholder]').forEach((node) => {
    node.setAttribute('placeholder', t(node.dataset.i18nPlaceholder));
  });

  root.querySelectorAll('[data-i18n-title]').forEach((node) => {
    node.setAttribute('title', t(node.dataset.i18nTitle));
  });

  root.querySelectorAll('[data-i18n-aria-label]').forEach((node) => {
    node.setAttribute('aria-label', t(node.dataset.i18nAriaLabel));
  });

  root.querySelectorAll('[data-i18n-alt]').forEach((node) => {
    node.setAttribute('alt', t(node.dataset.i18nAlt));
  });
}

function syncLanguageControls() {
  document.querySelectorAll('[data-language-option]').forEach((input) => {
    if (input instanceof HTMLInputElement) {
      input.checked = input.value === currentLanguage;
    }
  });
}

function applyLanguage(language, { notify = true } = {}) {
  currentLanguage = hasLanguage(language) ? language : DEFAULT_LANGUAGE;
  document.documentElement.lang = currentLanguage;
  applyStaticTranslations(document);
  syncLanguageControls();
  persistLanguage(currentLanguage);

  if (notify) {
    const event = new CustomEvent('i18n:language-changed', { detail: { language: currentLanguage } });
    document.dispatchEvent(event);
    listeners.forEach((listener) => {
      try {
        listener(currentLanguage);
      } catch (error) {
        console.warn('[i18n] language listener failed', error);
      }
    });
  }
}

export function setLanguage(language) {
  applyLanguage(language);
}

export function initI18n() {
  applyLanguage(readSavedLanguage() || DEFAULT_LANGUAGE, { notify: false });

  document.querySelectorAll('[data-language-option]').forEach((input) => {
    input.addEventListener('change', () => {
      if (input instanceof HTMLInputElement && input.checked) {
        setLanguage(input.value);
      }
    });
  });
}

export function translateFragment(root) {
  applyStaticTranslations(root);
}
