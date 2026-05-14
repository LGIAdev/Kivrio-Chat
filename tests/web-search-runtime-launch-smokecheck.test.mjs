import assert from 'node:assert/strict';
import { mkdir, mkdtemp, rm, writeFile } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import path from 'node:path';

import { smokecheckRuntimeLaunch } from '../integrations/searxng/packaging/runtime_launch_smokecheck.mjs';

{
  const root = await mkdtemp(path.join(tmpdir(), 'kivrio-launch-smoke-missing-python-'));
  try {
    await createLauncherFiles(root);
    const result = await smokecheckRuntimeLaunch({ repoRoot: root, runCommand: successRunner() });

    assert.equal(result.ok, false);
    assert.equal(result.status, 'python_missing');
    assert(hasDiagnostic(result, 'python_missing'), 'missing Python should be diagnosed');
  } finally {
    await rm(root, { recursive: true, force: true });
  }
}

{
  const root = await createCompleteRoot('kivrio-launch-smoke-launch-failed-');
  try {
    const result = await smokecheckRuntimeLaunch({
      repoRoot: root,
      runCommand: sequenceRunner([
        { exitCode: 1, stdout: '{"ok":false,"message":"startup failed"}\n', stderr: '' },
      ]),
    });

    assert.equal(result.ok, false);
    assert.equal(result.status, 'launch_failed');
    assert(hasDiagnostic(result, 'launch_failed'), 'launch failure should be diagnosed');
  } finally {
    await rm(root, { recursive: true, force: true });
  }
}

{
  const root = await createCompleteRoot('kivrio-launch-smoke-health-failed-');
  try {
    const result = await smokecheckRuntimeLaunch({
      repoRoot: root,
      runCommand: sequenceRunner([
        { exitCode: 0, stdout: '{"ok":true,"base_url":"http://127.0.0.1:8030/"}\n', stderr: '' },
        { exitCode: 1, stdout: '{"ok":false,"base_url":"http://127.0.0.1:8030/"}\n', stderr: '' },
        { exitCode: 0, stdout: '{"ok":true,"stopped":true,"pid":123}\n', stderr: '' },
      ]),
    });

    assert.equal(result.ok, false);
    assert.equal(result.status, 'healthcheck_failed');
    assert(hasDiagnostic(result, 'healthcheck_failed'), 'healthcheck failure should be diagnosed');
    assert.equal(result.checks.stop.stopped, true, 'process should be stopped after healthcheck failure');
  } finally {
    await rm(root, { recursive: true, force: true });
  }
}

{
  const root = await createCompleteRoot('kivrio-launch-smoke-ready-');
  try {
    const result = await smokecheckRuntimeLaunch({
      repoRoot: root,
      runCommand: successRunner(),
    });

    assert.equal(result.ok, true);
    assert.equal(result.status, 'launch_ready');
    assert.deepEqual(result.diagnostics, []);
    assert.equal(result.checks.stop.stopped, true);
  } finally {
    await rm(root, { recursive: true, force: true });
  }
}

{
  const root = await createCompleteRoot('kivrio-launch-smoke-stop-failed-');
  try {
    const result = await smokecheckRuntimeLaunch({
      repoRoot: root,
      runCommand: sequenceRunner([
        { exitCode: 0, stdout: '{"ok":true,"base_url":"http://127.0.0.1:8030/"}\n', stderr: '' },
        { exitCode: 0, stdout: '{"ok":true,"base_url":"http://127.0.0.1:8030/","path":"/healthz"}\n', stderr: '' },
        { exitCode: 1, stdout: '{"ok":false,"stopped":false}\n', stderr: '' },
      ]),
    });

    assert.equal(result.ok, false);
    assert.equal(result.status, 'stop_failed');
    assert(hasDiagnostic(result, 'stop_failed'), 'stop failure should be diagnosed');
  } finally {
    await rm(root, { recursive: true, force: true });
  }
}

console.log('web search runtime launch smokecheck tests passed');

async function createCompleteRoot(prefix) {
  const root = await mkdtemp(path.join(tmpdir(), prefix));
  await createPythonExe(root);
  await createLauncherFiles(root);
  return root;
}

async function createPythonExe(root) {
  const pythonDir = path.join(root, 'runtime', 'python');
  await mkdir(pythonDir, { recursive: true });
  await writeFile(path.join(pythonDir, 'python.exe'), '', 'utf8');
}

async function createLauncherFiles(root) {
  const launcherDir = path.join(root, 'integrations', 'searxng', 'launcher');
  await mkdir(launcherDir, { recursive: true });
  await writeFile(path.join(launcherDir, 'start_searxng.py'), '', 'utf8');
  await writeFile(path.join(launcherDir, 'stop_searxng.py'), '', 'utf8');
  await writeFile(path.join(launcherDir, 'healthcheck.py'), '', 'utf8');
}

function successRunner() {
  return sequenceRunner([
    { exitCode: 0, stdout: '{"ok":true,"base_url":"http://127.0.0.1:8030/"}\n', stderr: '' },
    { exitCode: 0, stdout: '{"ok":true,"base_url":"http://127.0.0.1:8030/","path":"/healthz"}\n', stderr: '' },
    { exitCode: 0, stdout: '{"ok":true,"stopped":true,"pid":123}\n', stderr: '' },
  ]);
}

function sequenceRunner(responses) {
  let index = 0;
  return async () => {
    const response = responses[index] || responses[responses.length - 1];
    index += 1;
    return response;
  };
}

function hasDiagnostic(result, code) {
  return result.diagnostics.some((item) => item.code === code);
}
