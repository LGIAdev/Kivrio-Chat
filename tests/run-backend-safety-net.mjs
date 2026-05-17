import {
  compileCSharpBackendTest,
  ensureBackendTempDir,
  listKivrioProcesses,
  runExecutable,
  runNodeTest,
} from './backend-test-utils.mjs';

const csharpTests = [
  {
    name: 'persistence',
    main: 'KivrioChatPersistenceTests.Program',
    source: 'tests/ServerPersistenceTest.cs',
    outputName: 'ServerPersistenceTest.exe',
  },
  {
    name: 'upload-limits',
    main: 'KivrioChatTests.Program',
    source: 'tests/ServerUploadLimitsTest.cs',
    outputName: 'ServerUploadLimitsTest.exe',
  },
  {
    name: 'security',
    main: 'KivrioChatSecurityTests.Program',
    source: 'tests/ServerSecurityTest.cs',
    outputName: 'ServerSecurityTest.exe',
  },
  {
    name: 'pdf-extraction',
    main: 'KivrioChatPdfTests.Program',
    source: 'tests/ServerPdfExtractionTest.cs',
    outputName: 'ServerPdfExtractionTest.exe',
  },
];

const nodeTests = [
  'tests/error-ux.test.mjs',
  'tests/auth-reconnect-ui.test.mjs',
  'tests/sidebar-search.test.mjs',
  'tests/ollama-abort.test.mjs',
  'tests/web-search-api.test.mjs',
  'tests/web-search-prompt-injection.test.mjs',
  'tests/web-search-sources-store.test.mjs',
  'tests/uploads-pdf-prepare.test.mjs',
];

const startProcesses = await safeListProcesses();
const startPids = new Set(startProcesses.map((item) => Number(item.Id)));

try {
  await ensureBackendTempDir();

  for (const test of csharpTests) {
    await runStep(`compile ${test.name}`, async () => {
      test.executablePath = await compileCSharpBackendTest(test);
    });
    await runStep(`run ${test.name}`, async () => {
      await runExecutable(test.executablePath);
    });
  }

  for (const testPath of nodeTests) {
    await runStep(`run ${testPath}`, async () => {
      await runNodeTest(testPath);
    });
  }

  const endProcesses = await safeListProcesses();
  const leaked = endProcesses.filter((item) => !startPids.has(Number(item.Id)));
  if (leaked.length) {
    throw new Error(`Processus Kivrio laisse actif apres tests: ${formatProcesses(leaked)}`);
  }

  console.log('backend safety net passed');
} catch (error) {
  console.error('');
  console.error('backend safety net failed');
  if (error?.stdout) console.error(String(error.stdout).trim());
  if (error?.stderr) console.error(String(error.stderr).trim());
  console.error(error?.stack || error?.message || String(error));
  process.exitCode = 1;
}

async function runStep(label, fn) {
  process.stdout.write(`[backend] ${label} ... `);
  await fn();
  console.log('OK');
}

async function safeListProcesses() {
  try {
    return await listKivrioProcesses();
  } catch (_) {
    return [];
  }
}

function formatProcesses(processes) {
  return processes
    .map((item) => `${item.ProcessName || 'process'}#${item.Id}`)
    .join(', ');
}
