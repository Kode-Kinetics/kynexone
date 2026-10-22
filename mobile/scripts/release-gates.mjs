import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import ts from 'typescript';
import { loadEffectiveConfig } from './updatesConfig.mjs';

export function inspectSource(file, source) {
  const failures = [];
  const ast = ts.createSourceFile(file, source, ts.ScriptTarget.Latest, true, ts.ScriptKind.TSX);
  function inDevelopmentBranch(node) {
    for (let child = node, parent = node.parent; parent; child = parent, parent = parent.parent) {
      if (ts.isIfStatement(parent) && parent.expression.getText(ast) === '__DEV__' && parent.thenStatement === child) return true;
    }
    return false;
  }
  function visit(node) {
    if (ts.isCallExpression(node) && ts.isPropertyAccessExpression(node.expression)
      && node.expression.expression.getText(ast) === 'console'
      && node.arguments.some((arg) => !ts.isStringLiteral(arg) && !ts.isNoSubstitutionTemplateLiteral(arg))
      && !inDevelopmentBranch(node)) failures.push(file + ': dynamic production console payload');
    if (ts.isPropertyAccessExpression(node) && ['documentDirectory', 'document'].includes(node.name.text)
      && /FileSystem|Paths/.test(node.expression.getText(ast))) failures.push(file + ': persistent file destination requires security review');
    ts.forEachChild(node, visit);
  }
  visit(ast);
  return failures;
}

export function checkReleaseSources(root) {
  const failures = [];
  function walk(directory) {
    for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
      const filename = path.join(directory, entry.name);
      if (entry.isDirectory()) { if (filename !== path.join(root, 'src/dev')) walk(filename); }
      else if (/\.tsx?$/.test(filename)) failures.push(...inspectSource(path.relative(root, filename), fs.readFileSync(filename, 'utf8')));
    }
  }
  walk(path.join(root, 'src'));
  return failures;
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const root = path.resolve(fileURLToPath(new URL('..', import.meta.url)));
  const failures = checkReleaseSources(root);
  const sourceOnly = process.argv.includes('--source-only');
  if (!sourceOnly) {
    try {
      const eas = JSON.parse(fs.readFileSync(path.join(root, 'eas.json'), 'utf8'));
      const config = loadEffectiveConfig(root, { ...process.env, ...eas.build.production.env,
        EXPO_NO_DOTENV: '1', EAS_BUILD_PROFILE: 'production', NODE_ENV: 'production' });
      if (!config.updates?.codeSigningCertificate) failures.push('Signed OTA certificate is required.');
      if (config.plugins.some((plugin) => String(plugin).includes('withUITests'))) failures.push('UI test plugin is forbidden in production.');
      const privacy = config.ios?.privacyManifests;
      if (privacy?.NSPrivacyTracking !== false || !privacy.NSPrivacyCollectedDataTypes?.length) failures.push('Privacy declarations are missing.');
      if (!config.plugins.includes('./plugins/withReleaseSecurity')) failures.push('Native security plugin is missing.');
    } catch (error) { failures.push(error.message); }
  }
  for (const failure of failures) console.error('FAIL: ' + failure);
  if (failures.length) process.exitCode = 1;
  else if (sourceOnly) console.log('Source release scan passed.');
  else console.log('Static release gates passed. Complete the device, archive, privacy and dependency checklist before distribution.');
}
