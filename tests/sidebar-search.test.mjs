import assert from 'node:assert/strict';
import { getSidebarSearchMatches } from '../js/store/conversations.js';

const conversations = [
  {
    id: 'c1',
    title: 'Contrat client',
    archived: 0,
    messageCount: 2,
    updatedAt: 20,
    messages: [
      { content: 'Analyse des clauses de resiliation.', attachments: [] },
    ],
  },
  {
    id: 'c2',
    title: 'Recette cuisine',
    archived: 0,
    messageCount: 1,
    updatedAt: 10,
    messages: [
      { content: 'Fichier joint pour le contrat fournisseur.', attachments: [{ filename: 'contrat.pdf' }] },
    ],
  },
  {
    id: 'c3',
    title: 'Archive contrat',
    archived: 1,
    messageCount: 4,
    updatedAt: 1,
    messages: [{ content: 'Invisible', attachments: [] }],
  },
];

const folders = [
  { id: 'f1', name: 'Contrats 2026', conversationCount: 3 },
  { id: 'f2', name: 'Administratif', conversationCount: 1 },
];

{
  const results = getSidebarSearchMatches({ query: 'contrat', filter: 'all', conversations, folders });
  assert.deepEqual(results.map((item) => item.id), ['c1', 'c2', 'f1']);
  assert.equal(results[0].type, 'history');
  assert.equal(results[2].type, 'folders');
}

{
  const results = getSidebarSearchMatches({ query: 'contrat', filter: 'history', conversations, folders });
  assert.deepEqual(results.map((item) => item.id), ['c1', 'c2']);
}

{
  const results = getSidebarSearchMatches({ query: 'contrat', filter: 'folders', conversations, folders });
  assert.deepEqual(results.map((item) => item.id), ['f1']);
}

{
  const results = getSidebarSearchMatches({ query: 'résiliation', filter: 'history', conversations, folders });
  assert.deepEqual(results.map((item) => item.id), ['c1']);
  assert.match(results[0].snippet, /resiliation/i);
}

console.log('sidebar search tests passed');
