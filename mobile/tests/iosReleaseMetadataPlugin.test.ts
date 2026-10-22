import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import fs from 'node:fs';
import path from 'node:path';
import test from 'node:test';

const root = path.resolve(import.meta.dirname, '..');
const require = createRequire(import.meta.url);

test('production config applies the iOS release metadata plugin', () => {
  const app = JSON.parse(fs.readFileSync(path.join(root, 'app.json'), 'utf8')).expo;
  assert.ok(app.plugins.includes('./plugins/withIOSReleaseMetadata'));
});

test('iOS release metadata resolves versions and push entitlement through build settings', () => {
  const { applyInfoPlist, applyEntitlements, applyBuildSettings } = require('../plugins/withIOSReleaseMetadata');
  assert.deepEqual(applyInfoPlist({ CFBundleShortVersionString: '1.0.0', CFBundleVersion: '13' }), {
    CFBundleShortVersionString: '$(MARKETING_VERSION)',
    CFBundleVersion: '$(CURRENT_PROJECT_VERSION)',
  });
  assert.deepEqual(applyEntitlements({ 'aps-environment': 'development' }), {
    'aps-environment': '$(APS_ENVIRONMENT)',
  });
  assert.deepEqual(applyBuildSettings({ OTHER_SETTING: 'YES' }, 'Release'), {
    OTHER_SETTING: 'YES',
    APS_ENVIRONMENT: 'production',
  });
  assert.deepEqual(applyBuildSettings({ OTHER_SETTING: 'YES' }, 'Debug'), {
    OTHER_SETTING: 'YES',
    APS_ENVIRONMENT: 'development',
  });
});
