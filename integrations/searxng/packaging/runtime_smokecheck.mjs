import { execFile } from 'node:child_process';
import { stat } from 'node:fs/promises';
import path from 'node:path';
import { promisify } from 'node:util';
import { fileURLToPath, pathToFileURL } from 'node:url';

const execFileAsync = promisify(execFile);
const thisFile = fileURLToPath(import.meta.url);
const defaultRepoRoot = path.resolve(path.dirname(thisFile), '..', '..', '..');
const defaultTimeoutMs = 10000;

export async function smokecheckRuntime(options = {}) {
  const repoRoot = path.resolve(options.repoRoot || defaultRepoRoot);
  const pythonExePath = options.pythonExePath || path.join(repoRoot, 'runtime', 'python', 'python.exe');
  const searxngRoot = options.searxngRoot || path.join(repoRoot, 'integrations', 'searxng', 'vendor', 'searxng');
  const runCommand = options.runCommand || runCommandDefault;
  const diagnostics = [];
  const checks = {
    pythonVersion: null,
    pythonStdlib: null,
    searxFiles: null,
    searxImport: null,
  };

  if (!(await exists(pythonExePath))) {
    diagnostics.push(diagnostic('python_missing', 'error', 'runtime/python/python.exe is absent.'));
    return result('python_missing', diagnostics, checks);
  }

  const requiredSearxFiles = [
    path.join(searxngRoot, 'searx', '__init__.py'),
    path.join(searxngRoot, 'requirements.txt'),
  ];
  const missingSearxFiles = [];
  for (const filePath of requiredSearxFiles) {
    if (!(await exists(filePath))) {
      missingSearxFiles.push(path.relative(repoRoot, filePath));
    }
  }

  if (missingSearxFiles.length > 0) {
    checks.searxFiles = { ok: false, missing: missingSearxFiles };
    diagnostics.push(diagnostic('searx_missing', 'error', 'SearXNG runtime files are missing.'));
    return result('searx_missing', diagnostics, checks);
  }
  checks.searxFiles = { ok: true, missing: [] };

  const versionCheck = await runPython(runCommand, pythonExePath, ['-V'], repoRoot);
  checks.pythonVersion = versionCheck;
  if (!versionCheck.ok) {
    diagnostics.push(diagnostic('python_broken', 'error', 'Python did not return a version.'));
    return result('python_broken', diagnostics, checks);
  }

  const stdlibCode = [
    'import json, socket, ssl, sqlite3, sys',
    'print(json.dumps({"ok": True, "version": sys.version.split()[0]}))',
  ].join('; ');
  const stdlibCheck = await runPython(runCommand, pythonExePath, ['-c', stdlibCode], repoRoot);
  checks.pythonStdlib = parseJsonCheck(stdlibCheck);
  if (!checks.pythonStdlib.ok) {
    diagnostics.push(diagnostic('python_broken', 'error', 'Python standard runtime imports failed.'));
    return result('python_broken', diagnostics, checks);
  }

  const importCode = [
    'import json, sys, traceback',
    `sys.path.insert(0, ${JSON.stringify(searxngRoot)})`,
    'try:',
    '    import searx',
    '    print(json.dumps({"ok": True, "file": getattr(searx, "__file__", "")}))',
    'except ModuleNotFoundError as error:',
    '    print(json.dumps({"ok": False, "error_type": "ModuleNotFoundError", "missing": error.name, "message": str(error)}))',
    '    sys.exit(13)',
    'except Exception as error:',
    '    print(json.dumps({"ok": False, "error_type": type(error).__name__, "message": str(error), "traceback": traceback.format_exc(limit=3)}))',
    '    sys.exit(14)',
  ].join('\n');

  const searxImport = await runPython(runCommand, pythonExePath, ['-c', importCode], repoRoot);
  checks.searxImport = parseJsonCheck(searxImport);
  if (!checks.searxImport.ok) {
    const missing = checks.searxImport.missing || '';
    if (checks.searxImport.error_type === 'ModuleNotFoundError' && missing && missing !== 'searx') {
      diagnostics.push(diagnostic('dependency_missing', 'error', `Python dependency is missing: ${missing}.`));
      return result('dependency_missing', diagnostics, checks);
    }
    diagnostics.push(diagnostic('searx_import_failed', 'error', 'SearXNG import failed.'));
    return result('searx_import_failed', diagnostics, checks);
  }

  return result('runtime_ready', diagnostics, checks);
}

async function runPython(runCommand, pythonExePath, args, cwd) {
  const completed = await runCommand({
    file: pythonExePath,
    args,
    cwd,
    timeoutMs: defaultTimeoutMs,
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
    ok: status === 'runtime_ready',
    status,
    diagnostics,
    checks,
  };
}

function diagnostic(code, severity, message) {
  return { code, severity, message };
}

function formatSummary(smokecheck) {
  const diagnostics = smokecheck.diagnostics.map((item) => `${item.severity}:${item.code}`).join(', ');
  return `web search runtime smokecheck: ${smokecheck.status}; ok=${smokecheck.ok}; diagnostics=${diagnostics || 'none'}`;
}

if (isMain()) {
  const smokecheck = await smokecheckRuntime();
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
