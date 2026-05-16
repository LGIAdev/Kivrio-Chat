import { execFile } from 'node:child_process';
import { stat } from 'node:fs/promises';
import path from 'node:path';
import { promisify } from 'node:util';
import { fileURLToPath, pathToFileURL } from 'node:url';

const execFileAsync = promisify(execFile);
const thisFile = fileURLToPath(import.meta.url);
const defaultRepoRoot = path.resolve(path.dirname(thisFile), '..', '..', '..');
const defaultTimeoutMs = 30000;

export async function smokecheckRuntimeLaunch(options = {}) {
  const repoRoot = path.resolve(options.repoRoot || defaultRepoRoot);
  const pythonExePath = options.pythonExePath || path.join(repoRoot, 'runtime', 'python', 'python.exe');
  const launcherRoot = options.launcherRoot || path.join(repoRoot, 'integrations', 'searxng', 'launcher');
  const startScriptPath = options.startScriptPath || path.join(launcherRoot, 'start_searxng.py');
  const stopScriptPath = options.stopScriptPath || path.join(launcherRoot, 'stop_searxng.py');
  const healthcheckPath = options.healthcheckPath || path.join(launcherRoot, 'healthcheck.py');
  const pidFilePath = options.pidFilePath
    || path.join(repoRoot, 'integrations', 'searxng', 'runtime', 'searxng-launch-smokecheck.pid');
  const runCommand = options.runCommand || runCommandDefault;
  const diagnostics = [];
  const checks = {
    launch: null,
    healthcheck: null,
    stop: null,
  };

  if (!(await exists(pythonExePath))) {
    diagnostics.push(diagnostic('python_missing', 'error', 'runtime/python/python.exe is absent.'));
    return result('python_missing', diagnostics, checks);
  }

  for (const [code, filePath] of [
    ['launcher_missing', startScriptPath],
    ['launcher_missing', stopScriptPath],
    ['launcher_missing', healthcheckPath],
  ]) {
    if (!(await exists(filePath))) {
      diagnostics.push(diagnostic(code, 'error', `${path.relative(repoRoot, filePath)} is absent.`));
      return result(code, diagnostics, checks);
    }
  }

  let shouldStop = false;
  let finalStatus = 'launch_failed';
  try {
    const launch = await runPython(
      runCommand,
      pythonExePath,
      [
        startScriptPath,
        '--json',
        '--real-start',
        '--pid-file',
        pidFilePath,
        '--startup-timeout',
        String(options.startupTimeoutSeconds || 20),
      ],
      path.dirname(startScriptPath),
      defaultTimeoutMs,
    );
    checks.launch = parseJsonCheck(launch);

    if (!checks.launch.ok) {
      diagnostics.push(diagnostic('launch_failed', 'error', launchFailureMessage(checks.launch)));
      finalStatus = 'launch_failed';
      return result(finalStatus, diagnostics, checks);
    }

    shouldStop = true;
    const baseUrl = checks.launch.base_url || '';
    const healthcheck = await runPython(
      runCommand,
      pythonExePath,
      [healthcheckPath, '--base-url', baseUrl, '--path', '/healthz', '--timeout', '2'],
      path.dirname(healthcheckPath),
      10000,
    );
    checks.healthcheck = parseJsonCheck(healthcheck);
    if (!checks.healthcheck.ok) {
      diagnostics.push(diagnostic('healthcheck_failed', 'error', 'Local SearXNG healthcheck failed.'));
      finalStatus = 'healthcheck_failed';
    } else {
      finalStatus = 'launch_ready';
    }
  } finally {
    if (shouldStop) {
      const stop = await runPython(
        runCommand,
        pythonExePath,
        [stopScriptPath, '--pid-file', pidFilePath, '--purge-runtime'],
        path.dirname(stopScriptPath),
        10000,
      );
      checks.stop = parseJsonCheck(stop);
      if (!checks.stop.ok) {
        diagnostics.push(diagnostic('stop_failed', 'error', 'SearXNG launch smokecheck could not stop the managed process.'));
        finalStatus = 'stop_failed';
      }
    }
  }

  return result(finalStatus, diagnostics, checks);
}

async function runPython(runCommand, pythonExePath, args, cwd, timeoutMs) {
  const completed = await runCommand({
    file: pythonExePath,
    args,
    cwd,
    timeoutMs,
    env: {
      ...process.env,
      PYTHONNOUSERSITE: '1',
      PYTHONDONTWRITEBYTECODE: '1',
    },
  });

  const stdout = completed.stdout || '';
  const stderr = completed.stderr || '';
  return {
    ok: completed.exitCode === 0,
    exitCode: completed.exitCode,
    stdout: stdout.trim(),
    stderr: stderr.trim(),
  };
}

async function runCommandDefault({ file, args, cwd, env, timeoutMs }) {
  try {
    const completed = await execFileAsync(file, args, {
      cwd,
      env,
      timeout: timeoutMs,
      windowsHide: true,
      maxBuffer: 1024 * 1024,
    });
    return {
      exitCode: 0,
      stdout: completed.stdout || '',
      stderr: completed.stderr || '',
    };
  } catch (error) {
    return {
      exitCode: typeof error.code === 'number' ? error.code : 1,
      stdout: error.stdout || '',
      stderr: error.stderr || '',
    };
  }
}

function parseJsonCheck(check) {
  const text = (check.stdout || '').trim();
  if (!text) {
    return { ...check, ok: false, parseError: 'empty stdout' };
  }

  try {
    const parsed = JSON.parse(text);
    return { ...check, ...parsed, ok: check.ok && parsed.ok === true };
  } catch (error) {
    return { ...check, ok: false, parseError: error.message };
  }
}

async function exists(filePath) {
  try {
    await stat(filePath);
    return true;
  } catch (error) {
    if (error && error.code === 'ENOENT') return false;
    throw error;
  }
}

function result(status, diagnostics, checks) {
  return {
    ok: status === 'launch_ready',
    status,
    diagnostics,
    checks,
  };
}

function diagnostic(code, severity, message) {
  return { code, severity, message };
}

function launchFailureMessage(launch) {
  if (launch.message) return launch.message;
  if (launch.stderr) return launch.stderr;
  return 'SearXNG process did not start.';
}

function formatSummary(smokecheck) {
  const diagnostics = smokecheck.diagnostics.map((item) => `${item.severity}:${item.code}`).join(', ');
  return `web search runtime launch smokecheck: ${smokecheck.status}; ok=${smokecheck.ok}; diagnostics=${diagnostics || 'none'}`;
}

if (isMain()) {
  const smokecheck = await smokecheckRuntimeLaunch();
  if (process.argv.includes('--json')) {
    console.log(JSON.stringify(smokecheck, null, 2));
  } else {
    console.log(formatSummary(smokecheck));
    for (const item of smokecheck.diagnostics) {
      console.log(`- ${item.message}`);
    }
  }
  if (!smokecheck.ok) {
    process.exitCode = 1;
  }
}

function isMain() {
  if (!process.argv[1]) return false;
  return import.meta.url === pathToFileURL(process.argv[1]).href;
}
