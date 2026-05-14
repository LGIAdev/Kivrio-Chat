import assert from 'node:assert/strict';
import { mkdir, mkdtemp, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';

import { preflightCandidateBundle } from '../integrations/searxng/packaging/candidate_preflight.mjs';

{
  const missingPath = path.join(tmpdir(), `kivrio-web-search-missing-${Date.now()}`);
  const result = await preflightCandidateBundle({ candidatePath: missingPath });

  assert.equal(result.ok, false);
  assert.equal(result.status, 'candidate_missing');
  assert(hasDiagnostic(result, 'candidate_missing'), 'missing candidate should be diagnosed');
}

{
  const candidate = await mkdtemp(path.join(tmpdir(), 'kivrio-web-search-candidate-incomplete-'));
  try {
    const result = await preflightCandidateBundle({ candidatePath: candidate });

    assert.equal(result.ok, false);
    assert.equal(result.status, 'candidate_incomplete');
    assert(hasDiagnostic(result, 'python_absent'), 'incomplete candidate should report absent Python');
    assert(hasDiagnostic(result, 'searxng_absent'), 'incomplete candidate should report absent SearXNG');
    assert(hasDiagnostic(result, 'manifest_absent'), 'incomplete candidate should report absent manifest');
  } finally {
    await rm(candidate, { recursive: true, force: true });
  }
}

{
  const candidate = await mkdtemp(path.join(tmpdir(), 'kivrio-web-search-candidate-ready-'));
  try {
    await createMinimalCandidate(candidate);
    const result = await preflightCandidateBundle({ candidatePath: candidate });

    assert.equal(result.ok, true, formatPreflightFailure(result));
    assert.equal(result.status, 'candidate_ready');
    assert.equal(result.audit.bundle.pythonPresent, true);
    assert.equal(result.audit.bundle.searxngPresent, true);
    assert.equal(result.audit.bundle.manifestPresent, true);
  } finally {
    await rm(candidate, { recursive: true, force: true });
  }
}

{
  const candidate = await mkdtemp(path.join(tmpdir(), 'kivrio-web-search-candidate-cache-'));
  try {
    await createMinimalCandidate(candidate);
    const cacheDir = path.join(candidate, 'runtime', 'python', '__pycache__');
    await mkdir(cacheDir, { recursive: true });
    await writeFile(path.join(cacheDir, 'module.pyc'), 'cache');

    const result = await preflightCandidateBundle({ candidatePath: candidate });

    assert.equal(result.ok, false);
    assert.equal(result.status, 'candidate_non_compliant');
    assert(
      result.audit.bundle.violations.some((violation) => violation.includes('__pycache__')),
      'candidate preflight should identify denied Python caches',
    );
    assert(hasDiagnostic(result, 'candidate_non_compliant'), 'non-compliant candidate should be diagnosed');
  } finally {
    await rm(candidate, { recursive: true, force: true });
  }
}

{
  const candidate = await mkdtemp(path.join(tmpdir(), 'kivrio-web-search-candidate-manifest-'));
  try {
    await createMinimalCandidate(candidate);
    await writeFile(path.join(candidate, 'runtime-manifest.json'), '{not-json', 'utf8');

    const result = await preflightCandidateBundle({ candidatePath: candidate });

    assert.equal(result.ok, false);
    assert.equal(result.status, 'candidate_non_compliant');
    assert(hasDiagnostic(result, 'manifest_invalid'), 'invalid manifest should be diagnosed');
  } finally {
    await rm(candidate, { recursive: true, force: true });
  }
}

console.log('web search candidate preflight tests passed');

async function createMinimalCandidate(candidate) {
  const pythonDir = path.join(candidate, 'runtime', 'python');
  const searxngDir = path.join(candidate, 'integrations', 'searxng', 'vendor', 'searxng');
  await mkdir(pythonDir, { recursive: true });
  await mkdir(searxngDir, { recursive: true });
  await writeFile(path.join(pythonDir, 'python.exe'), '', 'utf8');
  await writeFile(path.join(searxngDir, 'webapp.py'), 'def app():\n    return None\n', 'utf8');
  await writeFile(
    path.join(candidate, 'runtime-manifest.json'),
    JSON.stringify({
      name: 'kivrio-chat-web-search-runtime',
      manifest_version: 1,
      bundle_version: '0.0.0-test',
      runtime: {
        python: { path: 'runtime/python/python.exe', version: '3.x' },
        searxng: { path: 'integrations/searxng/vendor/searxng/', version: 'test' },
      },
    }, null, 2),
    'utf8',
  );
}

function hasDiagnostic(result, code) {
  return result.diagnostics.some((item) => item.code === code);
}

function formatPreflightFailure(result) {
  const violations = result.audit
    ? [...result.audit.policy.violations, ...result.audit.bundle.violations]
    : [];
  return violations.length === 0
    ? 'candidate preflight should pass'
    : `candidate preflight should pass:\n${violations.join('\n')}`;
}
