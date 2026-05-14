import { execFile } from 'node:child_process';
import { readFile, stat } from 'node:fs/promises';
import path from 'node:path';
import { promisify } from 'node:util';
import { fileURLToPath, pathToFileURL } from 'node:url';

const execFileAsync = promisify(execFile);
const thisFile = fileURLToPath(import.meta.url);
const defaultRepoRoot = path.resolve(path.dirname(thisFile), '..', '..', '..');
const defaultTimeoutMs = 10000;

const importNameByPackage = new Map([
  ['babel', 'babel'],
  ['certifi', 'certifi'],
  ['flask', 'flask'],
  ['flask-babel', 'flask_babel'],
  ['httpx', 'httpx'],
  ['httpx-socks', 'httpx_socks'],
  ['isodate', 'isodate'],
  ['jinja2', 'jinja2'],
  ['lxml', 'lxml'],
  ['markdown-it-py', 'markdown_it'],
  ['msgspec', 'msgspec'],
  ['pygments', 'pygments'],
  ['python-dateutil', 'dateutil'],
  ['pyyaml', 'yaml'],
  ['sniffio', 'sniffio'],
  ['typer', 'typer'],
  ['typing-extensions', 'typing_extensions'],
  ['valkey', 'valkey'],
  ['whitenoise', 'whitenoise'],
]);

export async function inventoryRuntimeDependencies(options = {}) {
  const repoRoot = path.resolve(options.repoRoot || defaultRepoRoot);
  const pythonExePath = options.pythonExePath || path.join(repoRoot, 'runtime', 'python', 'python.exe');
  const requirementsPath = options.requirementsPath
    || path.join(repoRoot, 'integrations', 'searxng', 'vendor', 'searxng', 'requirements.txt');
  const sitePaths = options.sitePaths || [
    path.join(repoRoot, 'runtime', 'python', 'Lib', 'site-packages'),
    path.join(repoRoot, 'runtime', 'python', 'site-packages'),
  ];
  const runCommand = options.runCommand || runCommandDefault;
  const diagnostics = [];
  const checks = {
    pythonVersion: null,
    importProbe: null,
  };

  if (!(await exists(pythonExePath))) {
    diagnostics.push(diagnostic('python_missing', 'error', 'runtime/python/python.exe is absent.'));
    return result('python_missing', diagnostics, [], checks, requirementsPath);
  }

  const requirementsText = await readOptional(requirementsPath);
  if (requirementsText === null) {
    diagnostics.push(diagnostic('requirements_missing', 'error', 'SearXNG requirements.txt is absent.'));
    return result('requirements_missing', diagnostics, [], checks, requirementsPath);
  }

  const dependencies = parseRequirements(requirementsText);
  const inferred = dependencies.filter((dependency) => dependency.importNameInferred);
  if (inferred.length > 0) {
    diagnostics.push(diagnostic('import_name_inferred', 'warning', 'Some dependency import names were inferred.'));
  }

  const versionCheck = await runPython(runCommand, pythonExePath, ['-V'], repoRoot);
  checks.pythonVersion = versionCheck;
  if (!versionCheck.ok) {
    diagnostics.push(diagnostic('python_broken', 'error', 'Python did not return a version.'));
    return result('python_broken', diagnostics, dependencies, checks, requirementsPath);
  }

  const importProbe = await runPython(
    runCommand,
    pythonExePath,
    ['-c', buildImportProbeCode(dependencies, sitePaths)],
    repoRoot,
  );
  checks.importProbe = parseJsonCheck(importProbe);
  if (!checks.importProbe.ok) {
    diagnostics.push(diagnostic('python_broken', 'error', 'Python dependency probe failed.'));
    return result('python_broken', diagnostics, dependencies, checks, requirementsPath);
  }

  const packages = checks.importProbe.packages || {};
  const inspectedDependencies = dependencies.map((dependency) => {
    const inspected = packages[dependency.normalizedName] || {};
    return {
      ...dependency,
      present: inspected.present === true,
      installedVersion: inspected.version || '',
      origin: inspected.origin || '',
    };
  });
  const missing = inspectedDependencies.filter((dependency) => !dependency.present);

  if (missing.length > 0) {
    const label = missing.length === 1 ? 'dependency is' : 'dependencies are';
    diagnostics.push(diagnostic('dependencies_missing', 'error', `${missing.length} Python runtime ${label} missing.`));
    return result('dependencies_missing', diagnostics, inspectedDependencies, checks, requirementsPath);
  }

  return result('dependencies_ready', diagnostics, inspectedDependencies, checks, requirementsPath);
}

export function parseRequirements(text) {
  return text
    .split(/\r?\n/)
    .map(parseRequirementLine)
    .filter(Boolean);
}

export function parseRequirementLine(line) {
  const trimmed = String(line || '').trim();
  if (!trimmed || trimmed.startsWith('#') || trimmed.startsWith('-')) {
    return null;
  }

  const withoutComment = trimmed.replace(/\s+#.*$/, '').trim();
  const withoutMarker = withoutComment.split(';')[0].trim();
  const match = withoutMarker.match(/^([A-Za-z0-9_.-]+)(?:\[([^\]]+)\])?\s*(.*)$/);
  if (!match) {
    return null;
  }

  const rawName = match[1];
  const normalizedName = normalizePackageName(rawName);
  const importName = importNameByPackage.get(normalizedName) || normalizedName.replace(/-/g, '_');

  return {
    raw: trimmed,
    name: rawName,
    normalizedName,
    extras: match[2] || '',
    specifier: match[3] || '',
    importName,
    importNameInferred: !importNameByPackage.has(normalizedName),
    present: false,
    installedVersion: '',
    origin: '',
  };
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

function buildImportProbeCode(dependencies, sitePaths) {
  return [
    'import importlib.metadata, importlib.util, json, sys',
    `dependencies = ${JSON.stringify(dependencies.map((dependency) => ({
      normalizedName: dependency.normalizedName,
      importName: dependency.importName,
    })))}`,
    `site_paths = ${JSON.stringify(sitePaths)}`,
    'for site_path in reversed(site_paths):',
    '    if site_path and site_path not in sys.path:',
    '        sys.path.insert(0, site_path)',
    'packages = {}',
    'for dependency in dependencies:',
    '    normalized_name = dependency["normalizedName"]',
    '    import_name = dependency["importName"]',
    '    spec = importlib.util.find_spec(import_name)',
    '    version = ""',
    '    if spec is not None:',
    '        try:',
    '            version = importlib.metadata.version(normalized_name)',
    '        except Exception:',
    '            version = ""',
    '    packages[normalized_name] = {',
    '        "present": spec is not None,',
    '        "version": version,',
    '        "origin": getattr(spec, "origin", "") if spec is not None else "",',
    '    }',
    'print(json.dumps({"ok": True, "packages": packages}))',
  ].join('\n');
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

async function readOptional(filePath) {
  try {
    return await readFile(filePath, 'utf8');
  } catch (error) {
    if (error && error.code === 'ENOENT') return null;
    throw error;
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

function result(status, diagnostics, dependencies, checks, requirementsPath) {
  const missing = dependencies.filter((dependency) => dependency.present !== true);
  return {
    ok: status === 'dependencies_ready',
    status,
    diagnostics,
    requirementsPath,
    summary: {
      declared: dependencies.length,
      present: dependencies.length - missing.length,
      missing: missing.length,
      inferredImportNames: dependencies.filter((dependency) => dependency.importNameInferred).length,
    },
    dependencies,
    checks,
  };
}

function diagnostic(code, severity, message) {
  return { code, severity, message };
}

function normalizePackageName(name) {
  return String(name || '').trim().toLowerCase().replace(/[_.]+/g, '-');
}

function formatSummary(inventory) {
  const diagnostics = inventory.diagnostics.map((item) => `${item.severity}:${item.code}`).join(', ');
  return [
    `web search dependency inventory: ${inventory.status}`,
    `ok=${inventory.ok}`,
    `declared=${inventory.summary.declared}`,
    `present=${inventory.summary.present}`,
    `missing=${inventory.summary.missing}`,
    `diagnostics=${diagnostics || 'none'}`,
  ].join('; ');
}

if (isMain()) {
  const inventory = await inventoryRuntimeDependencies();
  if (process.argv.includes('--json')) {
    console.log(JSON.stringify(inventory, null, 2));
  } else {
    console.log(formatSummary(inventory));
    for (const dependency of inventory.dependencies.filter((item) => !item.present)) {
      console.log(`- ${dependency.name}: import ${dependency.importName}`);
    }
  }
  if (!inventory.ok) {
    process.exitCode = 1;
  }
}

function isMain() {
  if (!process.argv[1]) return false;
  return import.meta.url === pathToFileURL(process.argv[1]).href;
}
