import assert from 'node:assert/strict';
import { mkdir, mkdtemp, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';

import {
  inventoryRuntimeDependencies,
  parseRequirementLine,
  parseRequirements,
} from '../integrations/searxng/packaging/dependency_inventory.mjs';

{
  const parsed = parseRequirementLine('httpx[http2]==0.28.1');
  assert.equal(parsed.normalizedName, 'httpx');
  assert.equal(parsed.extras, 'http2');
  assert.equal(parsed.importName, 'httpx');
  assert.equal(parsed.specifier, '==0.28.1');
}

{
  const dependencies = parseRequirements([
    '# ignored',
    'pyyaml==6.0.3',
    'python-dateutil==2.9.0.post0',
    'markdown-it-py==4.0.0',
    'typing-extensions==4.15.0',
    '-r requirements-dev.txt',
  ].join('\n'));

  assert.deepEqual(
    dependencies.map((dependency) => [dependency.normalizedName, dependency.importName]),
    [
      ['pyyaml', 'yaml'],
      ['python-dateutil', 'dateutil'],
      ['markdown-it-py', 'markdown_it'],
      ['typing-extensions', 'typing_extensions'],
    ],
  );
}

{
  const root = await mkdtemp(path.join(tmpdir(), 'kivrio-dependency-inventory-missing-python-'));
  try {
    await createRequirements(root, 'msgspec==0.21.1\n');
    const result = await inventoryRuntimeDependencies({ repoRoot: root, runCommand: readyRunner({}) });

    assert.equal(result.ok, false);
    assert.equal(result.status, 'python_missing');
    assert(hasDiagnostic(result, 'python_missing'), 'missing Python should be diagnosed');
  } finally {
    await rm(root, { recursive: true, force: true });
  }
}

{
  const root = await mkdtemp(path.join(tmpdir(), 'kivrio-dependency-inventory-missing-requirements-'));
  try {
    await createPythonExe(root);
    const result = await inventoryRuntimeDependencies({ repoRoot: root, runCommand: readyRunner({}) });

    assert.equal(result.ok, false);
    assert.equal(result.status, 'requirements_missing');
    assert(hasDiagnostic(result, 'requirements_missing'), 'missing requirements should be diagnosed');
  } finally {
    await rm(root, { recursive: true, force: true });
  }
}

{
  const root = await createCompleteRoot('kivrio-dependency-inventory-missing-');
  try {
    const result = await inventoryRuntimeDependencies({
      repoRoot: root,
      runCommand: readyRunner({
        pyyaml: { present: true, version: '6.0.3', origin: 'yaml/__init__.py' },
        msgspec: { present: false },
        httpx: { present: false },
      }),
    });

    assert.equal(result.ok, false);
    assert.equal(result.status, 'dependencies_missing');
    assert.equal(result.summary.declared, 3);
    assert.equal(result.summary.present, 1);
    assert.equal(result.summary.missing, 2);
    assert(hasDiagnostic(result, 'dependencies_missing'), 'missing dependencies should be diagnosed');
    assert.deepEqual(
      result.dependencies.filter((dependency) => !dependency.present).map((dependency) => dependency.normalizedName),
      ['msgspec', 'httpx'],
    );
  } finally {
    await rm(root, { recursive: true, force: true });
  }
}

{
  const root = await createCompleteRoot('kivrio-dependency-inventory-ready-');
  try {
    const result = await inventoryRuntimeDependencies({
      repoRoot: root,
      runCommand: readyRunner({
        pyyaml: { present: true, version: '6.0.3', origin: 'yaml/__init__.py' },
        msgspec: { present: true, version: '0.21.1', origin: 'msgspec/__init__.py' },
        httpx: { present: true, version: '0.28.1', origin: 'httpx/__init__.py' },
      }),
    });

    assert.equal(result.ok, true);
    assert.equal(result.status, 'dependencies_ready');
    assert.equal(result.summary.declared, 3);
    assert.equal(result.summary.present, 3);
    assert.equal(result.summary.missing, 0);
    assert.deepEqual(result.diagnostics, []);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
}

{
  const root = await createCompleteRoot('kivrio-dependency-inventory-python-broken-');
  try {
    const result = await inventoryRuntimeDependencies({
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

console.log('web search dependency inventory tests passed');

async function createCompleteRoot(prefix) {
  const root = await mkdtemp(path.join(tmpdir(), prefix));
  await createPythonExe(root);
  await createRequirements(root, [
    'pyyaml==6.0.3',
    'msgspec==0.21.1',
    'httpx[http2]==0.28.1',
  ].join('\n'));
  return root;
}

async function createPythonExe(root) {
  const pythonDir = path.join(root, 'runtime', 'python');
  await mkdir(pythonDir, { recursive: true });
  await writeFile(path.join(pythonDir, 'python.exe'), '', 'utf8');
}

async function createRequirements(root, text) {
  const searxngDir = path.join(root, 'integrations', 'searxng', 'vendor', 'searxng');
  await mkdir(searxngDir, { recursive: true });
  await writeFile(path.join(searxngDir, 'requirements.txt'), text, 'utf8');
}

function readyRunner(packages) {
  return async ({ args }) => {
    if (args.includes('-V')) {
      return { exitCode: 0, stdout: 'Python 3.13.13\n', stderr: '' };
    }
    return {
      exitCode: 0,
      stdout: `${JSON.stringify({ ok: true, packages })}\n`,
      stderr: '',
    };
  };
}

function hasDiagnostic(result, code) {
  return result.diagnostics.some((item) => item.code === code);
}
