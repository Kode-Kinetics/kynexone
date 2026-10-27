import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';

const app = JSON.parse(fs.readFileSync(new URL('../app.json', import.meta.url), 'utf8')).expo;

test('no motion purpose string while motion activity is disabled', () => {
  const location = app.plugins.find((p: unknown) => Array.isArray(p) && p[0] === 'expo-location');
  assert.equal(location[1].isAndroidMotionActivityEnabled, false);
  assert.equal(location[1].motionUsagePermission, false);
  assert.equal('NSMotionUsageDescription' in app.ios.infoPlist, false);
});

test('the iOS privacy manifest declares required-reason APIs, no tracking, and linked app-function data', () => {
  const m = app.ios.privacyManifests;
  assert.equal(m.NSPrivacyTracking, false);
  const categories = m.NSPrivacyAccessedAPITypes.map((a: any) => a.NSPrivacyAccessedAPIType);
  assert.ok(categories.includes('NSPrivacyAccessedAPICategoryUserDefaults'));
  for (const api of m.NSPrivacyAccessedAPITypes) assert.ok(api.NSPrivacyAccessedAPITypeReasons.length > 0);
  for (const d of m.NSPrivacyCollectedDataTypes) {
    assert.equal(d.NSPrivacyCollectedDataTypeTracking, false);
    assert.deepEqual(d.NSPrivacyCollectedDataTypePurposes, ['NSPrivacyCollectedDataTypePurposeAppFunctionality']);
  }
});

test('the API host is not hard-coded in app source', () => {
  const config = fs.readFileSync(new URL('../src/config/index.ts', import.meta.url), 'utf8');
  assert.ok(!/onrender\.com/.test(config));
});
