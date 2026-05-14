import assert from 'node:assert/strict';
import { mkdir, mkdtemp, readFile, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

import { installCandidateBundle } from '../integrations/searxng/packaging/install_candidate.mjs';

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const allowlistPath = path.join(repoRoot, 'integrations', 'searxng', 'packaging', 'runtime-allowlist.txt');
const denylistPath = path.join(repoRoot, 'integrations', 'searxng', 'packaging', 'runtime-denylist.txt');

{
  const root = await createTempRoot('kivrio-web-search-install-confirm-');
  const candidate = path.join(root, 'candidate');
  const target = path.join(root, 'target');
  try {
    await createMinimalCandidate(candidate, 'candidate');
    await mkdir(target, { recursive: true });

    const result = await installCandidateBundle({
      candidatePath: candidate,
      targetRoot: target,
      allowlistPath,
      denylistPath,
    });

    assert.equal(result.ok, false);
    assert.equal(result.status, 'install_rejected');
    assert(hasDiagnostic(result, 'install_confirmation_required'), 'copy should require explicit confirmation');
  } finally {
    await rm(root, { recursive: true, force: true });
  }
}

{
  const root = await createTempRoot('kivrio-web-search-install-incomplete-');
  const candidate = path.join(root, 'candidate');
  const target = path.join(root, 'target');
  try {
    await mkdir(candidate, { recursive: true });
    await mkdir(target, { recursive: true });

    const result = await installCandidateBundle({
      candidatePath: candidate,
      targetRoot: target,
      allowlistPath,
      denylistPath,
      confirm: true,
    });

    assert.equal(result.ok, false);
    assert.equal(result.status, 'install_rejected');
    assert(hasDiagnostic(result, 'candidate_not_ready'), 'incomplete candidate should be rejected');
  } finally {
    await rm(root, { recursive: true, force: true });
  }
}

{
  const root = await createTempRoot('kivrio-web-search-install-ready-');
  const candidate = path.join(root, 'candidate');
  const target = path.join(root, 'target');
  try {
    await createMinimalCandidate(candidate, 'new');
    await mkdir(target, { recursive: true });

    const result = await installCandidateBundle({
      candidatePath: candidate,
      targetRoot: target,
      allowlistPath,
      denylistPath,
      confirm: true,
    });

    assert.equal(result.ok, true, formatInstallFailure(result));
    assert.equal(result.status, 'install_completed');
    assert.equal(result.audit.status, 'bundle_ready');
    assert.equal(await readFile(path.join(target, 'runtime', 'python', 'python.exe'), 'utf8'), 'new python');
    assert.equal(await readFile(path.join(target, 'integrations', 'searxng', 'vendor', 'searxng', 'webapp.py'), 'utf8'), 'new searxng');
    assert.deepEqual(result.backups, []);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
}

{
  const root = await createTempRoot('kivrio-web-search-install-backup-');
  const candidate = path.join(root, 'candidate');
  const target = path.join(root, 'target');
  try {
    await createMinimalCandidate(candidate, 'replacement');
    await createExistingTargetBundle(target);

    const result = await installCandidateBundle({
      candidatePath: candidate,
      targetRoot: target,
      allowlistPath,
      denylistPath,
      confirm: true,
    });

    assert.equal(result.ok, true, formatInstallFailure(result));
    assert.equal(result.status, 'install_completed');
    assert.equal(result.backups.length, 3, 'existing Python, SearXNG, and manifest should be backed up');
    assert.equal(await readFile(path.join(result.backups[0].to, 'old.txt'), 'utf8'), 'old python');
    assert.equal(await readFile(path.join(target, 'runtime', 'python', 'python.exe'), 'utf8'), 'replacement python');
  } finally {
    await rm(root, { recursive: true, force: true });
  }
}

{
  const root = await createTempRoot('kivrio-web-search-install-inside-');
  const target = path.join(root, 'target');
  const candidate = path.join(target, 'candidate');
  try {
    await createMinimalCandidate(candidate, 'inside');

    const result = await installCandidateBundle({
      candidatePath: candidate,
      targetRoot: target,
      allowlistPath,
      denylistPath,
      confirm: true,
    });

    assert.equal(result.ok, false);
    assert.equal(result.status, 'install_rejected');
    assert(hasDiagnostic(result, 'candidate_inside_target'), 'candidate inside target should be rejected');
  } finally {
    await rm(root, { recursive: true, force: true });
  }
}

console.log('web search candidate install tests passed');

async function createTempRoot(prefix) {
  return mkdtemp(path.join(tmpdir(), prefix));
}

async function createMinimalCandidate(candidate, label) {
  const pythonDir = path.join(candidate, 'runtime', 'python');
  const searxngDir = path.join(candidate, 'integrations', 'searxng', 'vendor', 'searxng');
  await mkdir(pythonDir, { recursive: true });
  await mkdir(searxngDir, { recursive: true });
  await writeFile(path.join(pythonDir, 'python.exe'), `${label} python`, 'utf8');
  await writeFile(path.join(searxngDir, 'webapp.py'), `${label} searxng`, 'utf8');
  await writeFile(
    path.join(candidate, 'runtime-manifest.json'),
    JSON.stringify({
      name: 'kivrio-chat-web-search-runtime',
      manifest_version: 1,
      bundle_version: `0.0.0-${label}`,
      runtime: {
        python: { path: 'runtime/python/python.exe', version: '3.x' },
        searxng: { path: 'integrations/searxng/vendor/searxng/', version: label },
      },
    }, null, 2),
    'utf8',
  );
}

async function createExistingTargetBundle(target) {
  const pythonDir = path.join(target, 'runtime', 'python');
  const searxngDir = path.join(target, 'integrations', 'searxng', 'vendor', 'searxng');
  const packagingDir = path.join(target, 'integrations', 'searxng', 'packaging');
  await mkdir(pythonDir, { recursive: true });
  await mkdir(searxngDir, { recursive: true });
  await mkdir(packagingDir, { recursive: true });
  await writeFile(path.join(pythonDir, 'old.txt'), 'old python', 'utf8');
  await writeFile(path.join(searxngDir, 'old.py'), 'old searxng', 'utf8');
  await writeFile(path.join(packagingDir, 'runtime-manifest.json'), JSON.stringify({
    name: 'old-runtime',
    manifest_version: 1,
    bundle_version: 'old',
    runtime: {},
  }), 'utf8');
}

function hasDiagnostic(result, code) {
  return result.diagnostics.some((item) => item.code === code);
}

function formatInstallFailure(result) {
  const violations = result.audit
    ? [...result.audit.policy.violations, ...result.audit.bundle.violations]
    : [];
  return violations.length === 0
    ? 'candidate install should pass'
    : `candidate install should pass:\n${violations.join('\n')}`;
}
