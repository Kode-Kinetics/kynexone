import test from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { readFileSync, writeFileSync, mkdirSync, mkdtempSync, rmSync, symlinkSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { tmpdir } from 'node:os';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';

const require = createRequire(import.meta.url);
const mobileRoot = fileURLToPath(new URL('..', import.meta.url));
const queryRoot = dirname(require.resolve('query-string'));
const decoderRoot = dirname(require.resolve('decode-uri-component', { paths: [queryRoot] }));
const getStateFromPath = require('@react-navigation/core/lib/commonjs/getStateFromPath').default;
const getPathFromState = require('@react-navigation/core/lib/commonjs/getPathFromState').default;
const options = { screens: { Employee: 'employee/:id' } };

// This tests the parser actually used by Navigation 6, not a replacement parser API.
test('Navigation 6 resolves the patched decoder without changing its public API', () => {
  assert.match(JSON.parse(readFileSync(join(dirname(queryRoot), '@react-navigation/core/package.json'), 'utf8')).version, /^6\./);
  assert.equal(JSON.parse(readFileSync(join(queryRoot, 'package.json'), 'utf8')).version, '7.1.3');
  assert.equal(JSON.parse(readFileSync(join(decoderRoot, 'package.json'), 'utf8')).version, '0.5.1');
  const query = require('query-string');
  assert.equal(typeof query.parse, 'function');
  assert.equal(typeof query.stringify, 'function');
});

test('actual Navigation 6 deep links preserve Arabic, plus, percent and malformed encodings', () => {
  for (const name of ['محمد عبدالله', 'a+b % value', '\uFEFFname', '%E0%A4%A', '%', '%C2']) {
    const state = getStateFromPath(`employee/42?name=${encodeURIComponent(name)}&empty=&flag`, options);
    assert.equal(state.routes[0].params.id, '42');
    assert.equal(state.routes[0].params.name, name);
    assert.equal(state.routes[0].params.empty, '');
    assert.equal(state.routes[0].params.flag, null);
    const roundTrip = getStateFromPath(getPathFromState(state, options), options);
    // Navigation 6 itself stringifies null route parameters as the literal 'null'.
    assert.deepEqual(roundTrip.routes[0].params, { ...state.routes[0].params, flag: 'null' });
  }
  assert.equal(getStateFromPath('employee/42?name=a+b', options).routes[0].params.name, 'a b');
  const query = require('query-string');
  assert.deepEqual([...query.parse('tag=a&tag=b').tag], ['a', 'b']);
  assert.equal(query.parse('broken=%E0%A4%A').broken, '%E0%A4%A');
  assert.equal(query.parse('bom=%FE%FF').bom, '\uFFFD\uFFFD');
  assert.equal(query.parse('truncated=%C2').truncated, '\uFFFD');
  const raw = getStateFromPath('employee/42?broken=%E0%A4%A&bom=%FE%FF&truncated=%C2', options).routes[0].params;
  assert.equal(raw.broken, '%E0%A4%A');
  assert.equal(raw.bom, '\uFFFD\uFFFD');
  assert.equal(raw.truncated, '\uFFFD');
});

test('malformed percent input through the Navigation public parser completes within a bounded child process', () => {
  const payload = Array.from({ length: 40_000 }, (_, i) => `%${(i % 256).toString(16).padStart(2, '0')}%${((i >> 8) % 256).toString(16).padStart(2, '0')}%FF`).join('x');
  const code = `const fs=require('node:fs'); const parse=require('@react-navigation/core/lib/commonjs/getStateFromPath').default; const input=fs.readFileSync(0,'utf8'); const state=parse('employee/42?value='+input,{screens:{Employee:'employee/:id'}}); if(typeof state.routes[0].params.value!=='string')process.exit(2);`;
  const result = spawnSync(process.execPath, ['-e', code], { cwd: mobileRoot, input: payload, timeout: 5_000, encoding: 'utf8' });
  assert.equal(result.error, undefined, result.error?.message);
  assert.equal(result.status, 0, result.stderr);
});

function fixture(): { root: string; index: string } {
  const root = mkdtempSync(join(tmpdir(), 'kynexone-decoder-'));
  const nodeModules = join(root, 'node_modules');
  const query = join(nodeModules, 'query-string');
  mkdirSync(query, { recursive: true });
  writeFileSync(join(query, 'package.json'), JSON.stringify({ name: 'query-string', version: '7.1.3', main: 'index.js' }));
  let source = readFileSync(join(queryRoot, 'index.js'), 'utf8');
  source = source.replace("const decodeComponentModule = require('decode-uri-component');\nconst decodeComponent = typeof decodeComponentModule === 'function' ? decodeComponentModule : decodeComponentModule.default;", "const decodeComponent = require('decode-uri-component');");
  writeFileSync(join(query, 'index.js'), source);
  for (const dependency of ['strict-uri-encode', 'split-on-first', 'filter-obj', 'decode-uri-component']) {
    const entry = require.resolve(dependency, { paths: [queryRoot] });
    symlinkSync(dirname(entry), join(nodeModules, dependency), 'dir');
  }
  return { root, index: join(query, 'index.js') };
}

test('install-time interop patch is exact, idempotent and rejects source drift before writing', () => {
  const { applyPatch } = require('../scripts/patch-query-string.cjs');
  const sample = fixture();
  try {
    assert.equal(applyPatch(sample.root).changed, true);
    const patched = readFileSync(sample.index, 'utf8');
    assert.equal(applyPatch(sample.root).changed, false);
    assert.equal(readFileSync(sample.index, 'utf8'), patched);
    writeFileSync(sample.index, patched + '\n// unexpected source drift\n');
    const drift = readFileSync(sample.index, 'utf8');
    assert.throws(() => applyPatch(sample.root), /hash|source|layout/i);
    assert.equal(readFileSync(sample.index, 'utf8'), drift);
  } finally { rmSync(sample.root, { recursive: true, force: true }); }
});

test('install-time patch fails closed for an unreviewed query-string version', () => {
  const { applyPatch } = require('../scripts/patch-query-string.cjs');
  const sample = fixture();
  try {
    writeFileSync(join(sample.root, 'node_modules/query-string/package.json'), JSON.stringify({ name: 'query-string', version: '9.5.1' }));
    assert.throws(() => applyPatch(sample.root), /version|7\.1\.3/i);
  } finally { rmSync(sample.root, { recursive: true, force: true }); }
});

test('install-time patch rejects a missing or unreviewed decoder before source mutation', () => {
  const { applyPatch } = require('../scripts/patch-query-string.cjs');
  for (const version of [null, '0.5.0']) {
    const sample = fixture();
    try {
      const before = readFileSync(sample.index, 'utf8');
      const decoder = join(sample.root, 'node_modules/decode-uri-component');
      rmSync(decoder);
      if (version) {
        mkdirSync(decoder);
        writeFileSync(join(decoder, 'package.json'), JSON.stringify({ name: 'decode-uri-component', version, main: 'index.js' }));
        writeFileSync(join(decoder, 'index.js'), 'module.exports = value => value;');
      }
      assert.throws(() => applyPatch(sample.root), /Cannot find module|decoder version/);
      assert.equal(readFileSync(sample.index, 'utf8'), before);
    } finally { rmSync(sample.root, { recursive: true, force: true }); }
  }
});
