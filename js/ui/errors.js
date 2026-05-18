import { t } from '../i18n/i18n.js';

const SERVER_MESSAGE_KEYS = new Map([
  ['Authentication required.', 'auth.sessionRequired'],
  ['Invalid credentials.', 'auth.invalidCredentials'],
  ['Password setup required.', 'auth.choosePassword'],
  ['Le mot de passe est deja configure.', 'auth.passwordAlreadyConfigured'],
  ['Erreur serveur interne.', 'errors.serverInternal'],
  ['Requete trop volumineuse.', 'errors.requestTooLarge'],
  ['Trop de tentatives. Reessayez plus tard.', 'errors.tooManyAttempts'],
  ['Origine de requete invalide.', 'errors.securityRefusedReload'],
  ['Endpoint introuvable.', 'errors.unavailableVersion'],
  ['Resource not found.', 'errors.notFound'],
  ['Conversation introuvable.', 'errors.conversationNotFound'],
  ['Message introuvable.', 'errors.messageNotFound'],
  ['Dossier introuvable.', 'errors.folderNotFound'],
  ['Piece jointe introuvable.', 'errors.attachmentNotFound'],
  ['Fichier joint introuvable.', 'errors.fileAttachmentNotFound'],
  ['Les mots de passe ne correspondent pas.', 'auth.passwordMismatch'],
  ['Un traitement est deja en cours.', 'status.inProgress'],
]);

export function decorateHttpError(error, { status = 0, serverMessage = '', path = '' } = {}) {
  const enriched = error instanceof Error ? error : new Error(String(error || t('errors.default')));
  enriched.status = Number(status || 0);
  enriched.serverMessage = String(serverMessage || enriched.message || '');
  enriched.path = String(path || '');
  return enriched;
}

export function userMessageForError(error, fallback = t('errors.default')) {
  const status = Number(error?.status || error?.statusCode || 0);
  const raw = String(error?.serverMessage || error?.message || error || '').trim();

  if (SERVER_MESSAGE_KEYS.has(raw)) {
    return t(SERVER_MESSAGE_KEYS.get(raw));
  }

  const passwordMin = raw.match(/^Le mot de passe doit contenir au moins (\d+) caracteres\.$/);
  if (passwordMin) {
    return t('auth.passwordMin', { count: passwordMin[1] });
  }

  const passwordMax = raw.match(/^Le mot de passe ne peut pas depasser (\d+) caracteres\.$/);
  if (passwordMax) {
    return t('auth.passwordMax', { count: passwordMax[1] });
  }

  if (raw.startsWith('Type de fichier non pris en charge:')) {
    return t('uploads.unsupportedFileType', { name: raw.slice('Type de fichier non pris en charge:'.length).trim() });
  }
  if (raw.startsWith('Fichier trop volumineux:')) {
    return t('uploads.tooLarge', { name: raw.slice('Fichier trop volumineux:'.length).trim() });
  }
  if (raw === 'Maximum 5 fichiers par message.') {
    return t('uploads.maxFiles', { count: 5 });
  }
  if (raw === 'Le total des fichiers depasse la limite autorisee.') {
    return t('uploads.totalTooLarge');
  }

  if (status === 401) return t('auth.sessionRequired');
  if (status === 403) return t('errors.securityRefused');
  if (status === 404) return t('errors.notFound');
  if (status === 413) return t('errors.fileOrRequestTooLarge');
  if (status === 429) return t('errors.tooManyAttempts');
  if (status >= 500) return t('errors.serverInternal');

  if (isNetworkError(error, raw)) return t('errors.network');
  if (/^HTTP\s+\d+/.test(raw)) return fallback || t('errors.default');

  return fallback || t('errors.default');
}

export function assistantErrorMessage(error, fallback = t('errors.generationImpossible')) {
  return t('errors.prefix', { message: userMessageForError(error, fallback) });
}

export function showToast(message, { tone = 'error', durationMs = 5200 } = {}) {
  if (typeof document === 'undefined') return;
  const text = String(message || '').trim();
  if (!text) return;

  const region = ensureToastRegion();
  const toast = document.createElement('div');
  toast.className = `kivrio-toast is-${tone}`;
  toast.setAttribute('role', tone === 'error' ? 'alert' : 'status');
  toast.textContent = text;
  region.appendChild(toast);

  const remove = () => {
    toast.classList.add('is-hiding');
    window.setTimeout(() => {
      try { toast.remove(); } catch (_) {}
    }, 180);
  };

  window.setTimeout(remove, Math.max(1200, Number(durationMs || 0)));
}

function ensureToastRegion() {
  let region = document.getElementById('kivrio-toast-region');
  if (region) return region;
  region = document.createElement('div');
  region.id = 'kivrio-toast-region';
  region.className = 'kivrio-toast-region';
  region.setAttribute('aria-live', 'polite');
  region.setAttribute('aria-atomic', 'true');
  document.body.appendChild(region);
  return region;
}

function isNetworkError(error, raw) {
  return error instanceof TypeError
    || raw === 'Failed to fetch'
    || raw === 'NetworkError when attempting to fetch resource.'
    || raw.includes('Load failed');
}
