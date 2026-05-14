import { readdir, readFile, stat } from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

const thisFile = fileURLToPath(import.meta.url);
const defaultRepoRoot = path.resolve(path.dirname(thisFile), '..', '..', '..');

export const requiredAllowlistAnchors = [
  'runtime/python/**',
  'integrations/searxng/vendor/searxng/**',
  'integrations/searxng/config/**',
  'integrations/searxng/client/**',
  'integrations/searxng/launcher/**',
];

export const requiredDenylistPatterns = [
  '__pycache__/',
  '.git/',
  'node_modules/',
  'tests/',
  'build/',
  'dist/',
  '*.whl',
];

const maxBundleBytes = 300 * 1024 * 1024;
const maxSingleFileBytes = 100 * 1024 * 1024;

export async function auditWebSearchBundle(options = {}) {
  const repoRoot = path.resolve(options.repoRoot || defaultRepoRoot);
  const allowlistPath = options.allowlistPath || path.join(repoRoot, 'integrations', 'searxng', 'packaging', 'runtime-allowlist.txt');
  const denylistPath = options.denylistPath || path.join(repoRoot, 'integrations', 'searxng', 'packaging', 'runtime-denylist.txt');
  const manifestPath = options.manifestPath || path.join(repoRoot, 'integrations', 'searxng', 'packaging', 'runtime-manifest.json');
  const pythonExePath = options.pythonExePath || path.join(repoRoot, 'runtime', 'python', 'python.exe');
  const searxngRoot = options.searxngRoot || path.join(repoRoot, 'integrations', 'searxng', 'vendor', 'searxng');
  const bundleRoots = options.bundleRoots || [
    path.join(repoRoot, 'runtime', 'python'),
    path.join(repoRoot, 'integrations', 'searxng', 'vendor'),
  ];

  const allowlist = await readPatternFile(allowlistPath);
  const denylist = await readPatternFile(denylistPath);
  const diagnostics = [];
  const policyViolations = [];
  const bundleViolations = [];

  for (const anchor of requiredAllowlistAnchors) {
    if (!allowlist.includes(anchor)) {
      policyViolations.push(`runtime allowlist missing ${anchor}`);
    }
  }

  for (const pattern of requiredDenylistPatterns) {
    if (!denylist.includes(pattern)) {
      policyViolations.push(`runtime denylist missing ${pattern}`);
    }
  }

  const pythonPresent = await exists(pythonExePath);
  const searxngPresent = await hasEntries(searxngRoot);
  const manifestPresent = await exists(manifestPath);
  const bundlePresent = pythonPresent || searxngPresent || manifestPresent;

  if (!pythonPresent) {
    diagnostics.push(diagnostic('python_absent', 'info', 'runtime/python/python.exe is absent.'));
  }
  if (!searxngPresent) {
    diagnostics.push(diagnostic('searxng_absent', 'info', 'integrations/searxng/vendor/searxng is absent or empty.'));
  }
  if (!manifestPresent) {
    diagnostics.push(diagnostic('manifest_absent', 'info', 'runtime-manifest.json is absent.'));
  }

  if (manifestPresent) {
    await validateManifest(manifestPath, bundleViolations, diagnostics);
  }

  const deniedMatchers = denylist.map((pattern) => ({
    pattern,
    matches: createMatcher(pattern),
  }));

  let scannedEntries = 0;
  let totalBytes = 0;

  for (const root of bundleRoots) {
    if (!(await exists(root))) continue;

    for await (const entry of walk(root)) {
      scannedEntries += 1;
      const relative = toPosix(path.relative(repoRoot, entry.path));
      const matched = deniedMatchers.find(({ matches }) => matches(relative, entry));
      if (matched) {
        bundleViolations.push(`${relative} matches ${matched.pattern}`);
      }

      if (!entry.isDirectory) {
        totalBytes += entry.size;
        if (entry.size > maxSingleFileBytes) {
          bundleViolations.push(`${relative} is larger than ${maxSingleFileBytes} bytes`);
        }
      }
    }
  }

  if (totalBytes > maxBundleBytes) {
    bundleViolations.push(`bundle size ${totalBytes} exceeds ${maxBundleBytes} bytes`);
  }

  let status = 'bundle_absent';
  if (policyViolations.length > 0 || bundleViolations.length > 0) {
    status = 'bundle_non_compliant';
  } else if (bundlePresent && (!pythonPresent || !searxngPresent || !manifestPresent)) {
    status = 'bundle_incomplete';
  } else if (bundlePresent) {
    status = 'bundle_ready';
  }

  const ok = policyViolations.length === 0 && bundleViolations.length === 0;
  if (policyViolations.length > 0) {
    diagnostics.push(diagnostic('policy_non_compliant', 'error', 'Packaging policy files are incomplete.'));
  }
  if (bundleViolations.length > 0) {
    diagnostics.push(diagnostic('bundle_non_compliant', 'error', 'Runtime bundle contains denied or abnormal entries.'));
  }

  return {
    ok,
    status,
    diagnostics,
    policy: {
      allowlist,
      denylist,
      violations: policyViolations,
    },
    bundle: {
      present: bundlePresent,
      pythonPresent,
      searxngPresent,
      manifestPresent,
      scannedEntries,
      totalBytes,
      violations: bundleViolations,
    },
  };
}

async function validateManifest(manifestPath, bundleViolations, diagnostics) {
  try {
    const parsed = JSON.parse(await readFile(manifestPath, 'utf8'));
    if (parsed.manifest_version !== 1) {
      bundleViolations.push('runtime-manifest.json must use manifest_version 1');
    }
    if (!parsed.name || !parsed.bundle_version || !parsed.runtime) {
      bundleViolations.push('runtime-manifest.json is missing required runtime metadata');
    }
  } catch (error) {
    bundleViolations.push('runtime-manifest.json is not valid JSON');
    diagnostics.push(diagnostic('manifest_invalid', 'error', error.message));
  }
}

async function readPatternFile(filePath) {
  const text = await readFile(filePath, 'utf8');
  return text
    .split(/\r?\n/)
    .map((line) => line.trim())
    .filter((line) => line.length > 0 && !line.startsWith('#'))
    .map(toPosix);
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

async function hasEntries(directoryPath) {
  try {
    const entries = await readdir(directoryPath);
    return entries.length > 0;
  } catch (error) {
    if (error && error.code === 'ENOENT') return false;
    throw error;
  }
}

async function* walk(root) {
  const entries = await readdir(root, { withFileTypes: true });
  for (const entry of entries) {
    const fullPath = path.join(root, entry.name);
    const entryStat = await stat(fullPath);
    yield {
      path: fullPath,
      isDirectory: entry.isDirectory(),
      size: entry.isDirectory() ? 0 : entryStat.size,
    };
    if (entry.isDirectory()) {
      yield* walk(fullPath);
    }
  }
}

function createMatcher(rawPattern) {
  const pattern = toPosix(rawPattern).toLowerCase();

  if (pattern.endsWith('/**')) {
    const prefix = pattern.slice(0, -2);
    return (relativePath) => relativePath.toLowerCase().startsWith(prefix);
  }

  if (pattern.includes('*')) {
    const isDirectoryPattern = pattern.endsWith('/');
    const source = globToRegExp(isDirectoryPattern ? pattern.slice(0, -1) : pattern);
    if (isDirectoryPattern) {
      const exactDirectoryRegex = new RegExp(`(^|/)${source}$`, 'i');
      const descendantRegex = new RegExp(`(^|/)${source}/`, 'i');
      return (relativePath, entry = {}) => {
        const value = relativePath.toLowerCase();
        return descendantRegex.test(value) || (entry.isDirectory === true && exactDirectoryRegex.test(value));
      };
    }
    const regex = new RegExp(`(^|/)${source}$`, 'i');
    return (relativePath) => regex.test(relativePath);
  }

  if (pattern.endsWith('/')) {
    const directory = pattern.slice(0, -1);
    return (relativePath, entry = {}) => {
      const value = relativePath.toLowerCase();
      return value.startsWith(`${directory}/`)
        || value.includes(`/${directory}/`)
        || (entry.isDirectory === true && (value === directory || value.endsWith(`/${directory}`)));
    };
  }

  return (relativePath) => {
    const value = relativePath.toLowerCase();
    return value === pattern || value.endsWith(`/${pattern}`);
  };
}

function globToRegExp(pattern) {
  return pattern
    .replace(/[.+?^${}()|[\]\\]/g, '\\$&')
    .replace(/\*/g, '[^/]*');
}

function diagnostic(code, severity, message) {
  return { code, severity, message };
}

function toPosix(value) {
  return String(value || '').replace(/\\/g, '/');
}

function formatSummary(result) {
  const diagnostics = result.diagnostics.map((item) => `${item.severity}:${item.code}`).join(', ');
  return `web search bundle audit: ${result.status}; ok=${result.ok}; diagnostics=${diagnostics || 'none'}`;
}

if (isMain()) {
  const result = await auditWebSearchBundle();
  if (process.argv.includes('--json')) {
    console.log(JSON.stringify(result, null, 2));
  } else {
    console.log(formatSummary(result));
    for (const violation of [...result.policy.violations, ...result.bundle.violations]) {
      console.log(`- ${violation}`);
    }
  }
  if (!result.ok) {
    process.exitCode = 1;
  }
}

function isMain() {
  if (!process.argv[1]) return false;
  return import.meta.url === pathToFileURL(process.argv[1]).href;
}
