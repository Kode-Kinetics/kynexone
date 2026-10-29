'use strict';
// query-string 7 retains Navigation 6's named CJS API. The patched decoder is ESM.
// Only adapt that import; never backport the decoder or skip an unreviewed layout.
const fs = require('node:fs');
const path = require('node:path');
const { createHash } = require('node:crypto');

const ORIGINAL_SHA256 = 'caa3f2c8b45dfe1e91db22ae10743af68de8d96f26515132bb52485ec0f037fa';
const PATCHED_SHA256 = '5b35cf4e94dc1fb5863003c8ae61d329522666c5fd56965dd2eb228674f436ce';
const ORIGINAL_IMPORT = "const decodeComponent = require('decode-uri-component');";
const PATCHED_IMPORT = "const decodeComponentModule = require('decode-uri-component');\nconst decodeComponent = typeof decodeComponentModule === 'function' ? decodeComponentModule : decodeComponentModule.default;";
const sha256 = source => createHash('sha256').update(source).digest('hex');

function applyPatch(mobileRoot) {
  const queryRoot = path.join(mobileRoot, 'node_modules', 'query-string');
  const version = JSON.parse(fs.readFileSync(path.join(queryRoot, 'package.json'), 'utf8')).version;
  if (version !== '7.1.3') throw new Error('Unreviewed query-string version; expected 7.1.3');
  const decoderEntry = require.resolve('decode-uri-component', { paths: [queryRoot] });
  const decoderVersion = JSON.parse(fs.readFileSync(path.join(path.dirname(decoderEntry), 'package.json'), 'utf8')).version;
  if (decoderVersion !== '0.5.1') throw new Error('Unreviewed decoder version; expected patched 0.5.1');

  const index = path.join(queryRoot, 'index.js');
  const source = fs.readFileSync(index, 'utf8');
  const hash = sha256(source);
  const changed = hash === ORIGINAL_SHA256;
  if (!changed && hash !== PATCHED_SHA256) throw new Error('Unreviewed query-string source hash; refusing to patch');
  if (changed) {
    const patched = source.replace(ORIGINAL_IMPORT, PATCHED_IMPORT);
    if (sha256(patched) !== PATCHED_SHA256) throw new Error('Unexpected query-string import layout');
    fs.writeFileSync(index, patched);
  }
  const query = require(index);
  const parsed = query.parse('name=%D8%B3%D9%84%D8%A7%D9%85&plus=a+b&percent=%25&bad=%E0%A4%A');
  if (typeof query.stringify !== 'function' || parsed.name !== 'سلام' || parsed.plus !== 'a b' || parsed.percent !== '%' || parsed.bad !== '%E0%A4%A') {
    throw new Error('Navigation query-string decoder interop smoke check failed');
  }
  return { changed };
}

module.exports = { applyPatch };
if (require.main === module) {
  try {
    const { changed } = applyPatch(path.resolve(__dirname, '..'));
    console.log(`query-string 7.1.3 decoder interop ${changed ? 'applied' : 'verified'} (decode-uri-component 0.5.1)`);
  } catch (error) {
    console.error(error.message);
    process.exitCode = 1;
  }
}
