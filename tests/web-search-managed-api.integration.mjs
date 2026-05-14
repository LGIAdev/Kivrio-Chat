import assert from 'node:assert/strict';
import { execFile, spawn } from 'node:child_process';
import { existsSync } from 'node:fs';
import { readFile, rm } from 'node:fs/promises';
import http from 'node:http';
import net from 'node:net';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { promisify } from 'node:util';

const execFileAsync = promisify(execFile);
const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const serverExe = path.join(os.tmpdir(), 'KivrioChatPhase20Server.exe');
const pidFile = path.join(repoRoot, 'integrations', 'searxng', 'runtime', 'searxng.pid');
const pythonExe = path.join(repoRoot, 'runtime', 'python', 'python.exe');
const stopScript = path.join(repoRoot, 'integrations', 'searxng', 'launcher', 'stop_searxng.py');
const healthcheckScript = path.join(repoRoot, 'integrations', 'searxng', 'launcher', 'healthcheck.py');
const launchSettings = path.join(repoRoot, 'integrations', 'searxng', 'runtime', 'settings-launch.yml');

let serverProcess = null;

try {
  await stopManagedSearxng();
  await compileServer();

  const port = await freePort();
  serverProcess = startServer(port);
  await waitForServer(port);

  const response = await postJson(
    `http://127.0.0.1:${port}/api/web-search`,
    { query: 'kivrio phase vingt', max_results: 1 },
    90000,
  );

  assert.equal(response.statusCode, 200, 'managed web search API should return HTTP 200');
  assert.equal(response.body.ok, true, formatFailure('managed web search should report success', response));
  assert.equal(response.body.available, true, formatFailure('managed web search should report availability', response));
  assert(Array.isArray(response.body.results), 'managed web search should return a results array');
  assert(response.body.results.length <= 1, 'managed web search should respect max_results');
  assert.equal(typeof response.body.message, 'string', 'managed web search should return a message field');
  assert(existsSync(pidFile), 'managed backend start should record a SearXNG PID file');

  const managedBaseUrl = await managedBaseUrlFromSettings();
  serverProcess.kill();
  serverProcess = null;
  await delay(500);

  await stopManagedSearxng();
  assert(!existsSync(pidFile), 'managed SearXNG PID file should be removed after stop');
  await waitForSearxngStopped(managedBaseUrl);

  console.log('web search managed API integration test passed');
} finally {
  if (serverProcess) {
    serverProcess.kill();
  }
  await stopManagedSearxng();
  await rm(serverExe, { force: true });
}

async function compileServer() {
  const csc = findCsc();
  assert(csc, 'csc.exe should be available to compile the local server');
  await execFileAsync(csc, [
    '/nologo',
    '/target:exe',
    `/out:${serverExe}`,
    '/r:System.Web.Extensions.dll',
    path.join(repoRoot, 'server', 'KivrioChatServer.cs'),
  ], {
    cwd: repoRoot,
    windowsHide: true,
    maxBuffer: 1024 * 1024,
  });
}

function findCsc() {
  const windir = process.env.WINDIR || 'C:\\Windows';
  const candidates = [
    path.join(windir, 'Microsoft.NET', 'Framework64', 'v4.0.30319', 'csc.exe'),
    path.join(windir, 'Microsoft.NET', 'Framework', 'v4.0.30319', 'csc.exe'),
  ];
  return candidates.find((candidate) => existsSync(candidate)) || '';
}

function startServer(port) {
  const env = { ...process.env };
  env.KIVRO_DISABLE_AUTH = '1';
  delete env.KIVRIO_WEB_SEARCH_ENABLE_MANAGED;
  delete env.KIVRIO_WEB_SEARCH_BASE_URL;
  delete env.KIVRIO_WEB_SEARCH_ALLOW_MOCK;
  delete env.KIVRIO_WEB_SEARCH_MOCK_BASE_URL;

  const child = spawn(serverExe, [
    '--root',
    repoRoot,
    '--host',
    '127.0.0.1',
    '--port',
    String(port),
  ], {
    cwd: repoRoot,
    env,
    windowsHide: true,
    stdio: ['ignore', 'pipe', 'pipe'],
  });

  child.stdout.setEncoding('utf8');
  child.stderr.setEncoding('utf8');
  child.stdout.on('data', () => {});
  child.stderr.on('data', () => {});
  return child;
}

async function waitForServer(port) {
  const deadline = Date.now() + 10000;
  let lastError = null;
  while (Date.now() < deadline) {
    try {
      const response = await getJson(`http://127.0.0.1:${port}/api/conversations`, 1000);
      if (response.statusCode === 200) return;
    } catch (error) {
      lastError = error;
    }
    await delay(100);
  }
  throw lastError || new Error('Kivrio Chat server did not start in time');
}

async function freePort() {
  return new Promise((resolve, reject) => {
    const server = net.createServer();
    server.listen(0, '127.0.0.1', () => {
      const address = server.address();
      const port = address.port;
      server.close(() => resolve(port));
    });
    server.on('error', reject);
  });
}

async function getJson(url, timeoutMs) {
  return requestJson('GET', url, null, timeoutMs);
}

async function postJson(url, body, timeoutMs) {
  return requestJson('POST', url, body, timeoutMs);
}

async function requestJson(method, url, body, timeoutMs) {
  return new Promise((resolve, reject) => {
    const payload = body ? JSON.stringify(body) : '';
    const request = http.request(url, {
      method,
      headers: {
        'Content-Type': 'application/json',
        'Content-Length': Buffer.byteLength(payload),
        'Origin': new URL(url).origin,
      },
      timeout: timeoutMs,
    }, (response) => {
      let text = '';
      response.setEncoding('utf8');
      response.on('data', (chunk) => { text += chunk; });
      response.on('end', () => {
        try {
          resolve({ statusCode: response.statusCode, body: text ? JSON.parse(text) : null, text });
        } catch (error) {
          reject(new Error(`Invalid JSON response: ${error.message}; body=${text.slice(0, 500)}`));
        }
      });
    });
    request.on('error', reject);
    request.on('timeout', () => request.destroy(new Error(`HTTP ${method} timed out: ${url}`)));
    if (payload) request.write(payload);
    request.end();
  });
}

async function stopManagedSearxng() {
  if (!existsSync(pythonExe) || !existsSync(stopScript)) return;
  return runPython([stopScript]);
}

async function managedBaseUrlFromSettings() {
  const text = await readFile(launchSettings, 'utf8');
  const match = text.match(/^\s*port:\s*(\d+)\s*$/m);
  assert(match, 'managed launch settings should include the SearXNG port');
  return `http://127.0.0.1:${match[1]}/`;
}

async function waitForSearxngStopped(baseUrl) {
  const deadline = Date.now() + 30000;
  let lastStop = null;
  let lastHealth = null;
  while (Date.now() < deadline) {
    lastStop = await stopManagedSearxng();
    lastHealth = await runPython([healthcheckScript, '--base-url', baseUrl, '--timeout', '0.2']);
    if (lastHealth.exitCode !== 0) return;
    await delay(250);
  }
  throw new Error(
    'managed SearXNG should no longer answer after stop'
    + `; lastStopExit=${lastStop ? lastStop.exitCode : 'n/a'}`
    + `; lastStopStdout=${(lastStop && lastStop.stdout || '').trim()}`
    + `; lastStopStderr=${(lastStop && lastStop.stderr || '').trim()}`
    + `; lastHealth=${(lastHealth && lastHealth.stdout || '').trim()}`,
  );
}

async function runPython(args) {
  try {
    const completed = await execFileAsync(pythonExe, args, {
      cwd: repoRoot,
      windowsHide: true,
      timeout: 30000,
      maxBuffer: 1024 * 1024,
    });
    return { exitCode: 0, stdout: completed.stdout || '', stderr: completed.stderr || '' };
  } catch (error) {
    return {
      exitCode: typeof error.code === 'number' ? error.code : 1,
      stdout: error.stdout || '',
      stderr: error.stderr || '',
    };
  }
}

function delay(ms) {
  return new Promise((resolve) => setTimeout(resolve, ms));
}

function formatFailure(message, response) {
  return `${message}: ${JSON.stringify(response.body)}`;
}
