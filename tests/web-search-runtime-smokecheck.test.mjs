import assert from 'node:assert/strict';
import { mkdir, mkdtemp, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';

import { smokecheckRuntime } from '../integrations/searxng/packaging/runtime_smokecheck.mjs';

{
  const root = await mkdtemp(path.join(tmpdir(), 'kivrio-runtime-smoke-missing-python-'));
  try {
    await createSearxFiles(root);
    const result = await smokecheckRuntime({ repoRoot: root, runCommand: successRunner });

    assert.equal(result.ok, false);
    assert.equal(result.status, 'python_missing');
    assert(hasDiagnostic(result, 'python_missing'), 'missing Python should be diagnosed');
  } finally {
    await rm(root, { recursive: true, force: true });
  }
}

{
  const root = await mkdtemp(path.join(tmpdir(), 'kivrio-runtime-smoke-missing-searx-'));
  try {
    await createPythonExe(root);
    const result = await smokecheckRuntime({ repoRoot: root, runCommand: successRunner });

    assert.equal(result.ok, false);
    assert.equal(result.status, 'searx_missing');
    assert(hasDiagnostic(result, 'searx_missing'), 'missing SearXNG files should be diagnosed');
  } finally {
    await rm(root, { recursive: true, force: true });
  }
}

{
  const root = await createCompleteRoot('kivrio-runtime-smoke-python-broken-');
  try {
    const result = await smokecheckRuntime({
      repoRoot: root,
      runCommand: async () => ({ exitCode: 1, stdout: '', stderr: 'broken python' }),
    });

    assert.equal(result.ok, false);
    assert.equal(result.status, 'python_broken');
    assert(hasDiagnostic(result, 'python_broken'), 'broken Python should be diagnosed');
  } finally {
    await rm(root, { recursive: true, force: true });
  }
}

{
  const root = await createCompleteRoot('kivrio-runtime-smoke-dependency-missing-');
  try {
    const result = await smokecheckRuntime({
      repoRoot: root,
      runCommand: runnerWithImport({
        ok: false,
        error_type: 'ModuleNotFoundError',
        missing: 'msgspec',
        message: "No module named 'msgspec'",
      }),
    });

    assert.equal(result.ok, false);
    assert.equal(result.status, 'dependency_missing');
    assert(hasDiagnostic(result, 'dependency_missing'), 'missing dependency should be diagnosed');
  } finally {
    await rm(root, { recursive: true, force: true });
  }
}

{
  const root = await createCompleteRoot('kivrio-runtime-smoke-searx-import-failed-');
  try {
    const result = await smokecheckRuntime({
      repoRoot: root,
      runCommand: runnerWithImport({
        ok: false,
        error_type: 'RuntimeError',
        message: 'unexpected import error',
      }),
    });

    assert.equal(result.ok, false);
    assert.equal(result.status, 'searx_import_failed');
    assert(hasDiagnostic(result, 'searx_import_failed'), 'SearXNG import failure should be diagnosed');
  } finally {
    await rm(root, { recursive: true, force: true });
  }
}

{
  const root = await createCompleteRoot('kivrio-runtime-smoke-ready-');
  try {
    const result = await smokecheckRuntime({
      repoRoot: root,
      runCommand: runnerWithImport({
        ok: true,
        file: 'searx/__init__.py',
      }),
    });

    assert.equal(result.ok, true);
    assert.equal(result.status, 'runtime_ready');
    assert.deepEqual(result.diagnostics, []);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
}

console.log('web search runtime smokecheck tests passed');

async function createCompleteRoot(prefix) {
  const root = await mkdtemp(path.join(tmpdir(), prefix));
  await createPythonExe(root);
  await createSearxFiles(root);
  return root;
}

async function createPythonExe(root) {
  const pythonDir = path.join(root, 'runtime', 'python');
  await mkdir(pythonDir, { recursive: true });
  await writeFile(path.join(pythonDir, 'python.exe'), '', 'utf8');
}

async function createSearxFiles(root) {
  const searxDir = path.join(root, 'integrations', 'searxng', 'vendor', 'searxng', 'searx');
  await mkdir(searxDir, { recursive: true });
  await writeFile(path.join(searxDir, '__init__.py'), '', 'utf8');
  await writeFile(
    path.join(root, 'integrations', 'searxng', 'vendor', 'searxng', 'requirements.txt'),
    'msgspec==0.21.1\n',
    'utf8',
  );
}

async function successRunner({ args }) {
  if (args.includes('-V')) {
    return { exitCode: 0, stdout: 'Python 3.13.13\n', stderr: '' };
  }
  return { exitCode: 0, stdout: '{"ok":true,"version":"3.13.13"}\n', stderr: '' };
}

function runnerWithImport(importResult) {
  let calls = 0;
  return async ({ args }) => {
    calls += 1;
    if (args.includes('-V')) {
      return { exitCode: 0, stdout: 'Python 3.13.13\n', stderr: '' };
    }
    if (calls === 2) {
      return { exitCode: 0, stdout: '{"ok":true,"version":"3.13.13"}\n', stderr: '' };
    }
    return {
      exitCode: importResult.ok ? 0 : 13,
      stdout: `${JSON.stringify(importResult)}\n`,
      stderr: '',
    };
  };
}

function hasDiagnostic(result, code) {
  return result.diagnostics.some((item) => item.code === code);
}
