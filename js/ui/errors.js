const DEFAULT_ERROR = 'Action impossible pour le moment.';
const NETWORK_ERROR = 'Connexion impossible. Verifiez que le serveur local est demarre.';

const SERVER_MESSAGE_MAP = new Map([
  ['Authentication required.', 'Session requise.'],
  ['Invalid credentials.', 'Mot de passe incorrect.'],
  ['Password setup required.', 'Choisissez d abord votre mot de passe.'],
  ['Erreur serveur interne.', 'Erreur serveur interne. Reessayez dans un instant.'],
  ['Requete trop volumineuse.', 'La requete est trop volumineuse.'],
  ['Trop de tentatives. Reessayez plus tard.', 'Trop de tentatives. Reessayez plus tard.'],
  ['Origine de requete invalide.', 'Action refusee par securite. Rechargez la page puis reessayez.'],
  ['Endpoint introuvable.', 'Action indisponible dans cette version.'],
  ['Resource not found.', 'Ressource introuvable.'],
  ['Conversation introuvable.', 'Conversation introuvable.'],
  ['Message introuvable.', 'Message introuvable.'],
  ['Dossier introuvable.', 'Dossier introuvable.'],
  ['Piece jointe introuvable.', 'Piece jointe introuvable.'],
  ['Fichier joint introuvable.', 'Fichier joint introuvable.'],
  ['Les mots de passe ne correspondent pas.', 'Les mots de passe ne correspondent pas.'],
  ['Un traitement est deja en cours.', 'Un traitement est deja en cours.'],
]);

export function decorateHttpError(error, { status = 0, serverMessage = '', path = '' } = {}) {
  const enriched = error instanceof Error ? error : new Error(String(error || DEFAULT_ERROR));
  enriched.status = Number(status || 0);
  enriched.serverMessage = String(serverMessage || enriched.message || '');
  enriched.path = String(path || '');
  return enriched;
}

export function userMessageForError(error, fallback = DEFAULT_ERROR) {
  const status = Number(error?.status || error?.statusCode || 0);
  const raw = String(error?.serverMessage || error?.message || error || '').trim();

  if (SERVER_MESSAGE_MAP.has(raw)) {
    return SERVER_MESSAGE_MAP.get(raw);
  }

  if (raw.startsWith('Le mot de passe doit contenir ')
    || raw.startsWith('Le mot de passe ne peut pas depasser ')) {
    return raw;
  }

  if (raw.startsWith('Type de fichier non pris en charge:')) {
    return raw.replace(':', ' :');
  }
  if (raw.startsWith('Fichier trop volumineux:')) {
    return raw.replace(':', ' :');
  }
  if (raw === 'Maximum 5 fichiers par message.'
    || raw === 'Le total des fichiers depasse la limite autorisee.') {
    return raw;
  }

  if (status === 401) return 'Session requise.';
  if (status === 403) return 'Action refusee par securite.';
  if (status === 404) return 'Ressource introuvable.';
  if (status === 413) return 'Fichier ou requete trop volumineux.';
  if (status === 429) return 'Trop de tentatives. Reessayez plus tard.';
  if (status >= 500) return 'Erreur serveur interne. Reessayez dans un instant.';

  if (isNetworkError(error, raw)) return NETWORK_ERROR;
  if (/^HTTP\s+\d+/.test(raw)) return fallback || DEFAULT_ERROR;

  return fallback || DEFAULT_ERROR;
}

export function assistantErrorMessage(error, fallback = 'Generation impossible.') {
  return 'Erreur: ' + userMessageForError(error, fallback);
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
