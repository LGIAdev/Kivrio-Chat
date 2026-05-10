import assert from 'node:assert/strict';
import {
  assistantErrorMessage,
  decorateHttpError,
  showToast,
  userMessageForError,
} from '../js/ui/errors.js';

const serverError = decorateHttpError(new Error('HTTP 500'), {
  status: 500,
  serverMessage: 'Erreur serveur interne.',
  path: '/api/conversations',
});
assert.equal(
  userMessageForError(serverError, 'fallback'),
  'Erreur serveur interne. Reessayez dans un instant.',
);
assert.equal(
  assistantErrorMessage(serverError, 'Generation impossible.'),
  'Erreur: Erreur serveur interne. Reessayez dans un instant.',
);

const authError = decorateHttpError(new Error('Invalid credentials.'), {
  status: 401,
  serverMessage: 'Invalid credentials.',
});
assert.equal(userMessageForError(authError, 'Connexion impossible.'), 'Mot de passe incorrect.');

const uploadError = decorateHttpError(new Error('Type de fichier non pris en charge: run.exe'), {
  status: 400,
  serverMessage: 'Type de fichier non pris en charge: run.exe',
});
assert.equal(
  userMessageForError(uploadError, 'Televersement impossible.'),
  'Type de fichier non pris en charge : run.exe',
);

assert.equal(userMessageForError(new TypeError('Failed to fetch')), 'Connexion impossible. Verifiez que le serveur local est demarre.');
assert.equal(userMessageForError(new Error('HTTP 404'), 'Action impossible.'), 'Action impossible.');
assert.doesNotThrow(() => showToast('Document-less call is ignored.'));

console.log('error UX tests passed');
