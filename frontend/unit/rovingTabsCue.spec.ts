import { expect, test } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';

// Source contract: the scroll cue on RovingTabList is opt-in (default off), so no screen changes unless it asks, and it
// watches the tab list itself (a language switch or a late font changes the tabs' widths, not the container's).
const read = (...p: string[]) => fs.readFileSync(path.resolve(__dirname, '..', ...p), 'utf8');

test('RovingTabList: scrollCue is a prop that defaults to false and the default path keeps the original single wrapper', () => {
  const src = read('src/components/ui/RovingTabs.tsx');
  expect(src).toMatch(/scrollCue\?: boolean/);
  expect(src).toMatch(/scrollCue = false/);
  expect(src).toMatch(/if \(!scrollCue\) return strip;/);
});

test('RovingTabList: the cue observes the inner tab list as well as the scroller', () => {
  const src = read('src/components/ui/RovingTabs.tsx');
  expect(src).toMatch(/observer\?\.observe\(tablist\.current\)/);
});

test('only the Attendance workspace turns the scroll cue on', () => {
  const dir = path.resolve(__dirname, '../src');
  const users: string[] = [];
  const walk = (d: string) => {
    for (const e of fs.readdirSync(d, { withFileTypes: true })) {
      const full = path.join(d, e.name);
      if (e.isDirectory()) walk(full);
      else if (/\.tsx$/.test(e.name) && (fs.readFileSync(full, 'utf8').match(/<RovingTabList\b[\s\S]*?\n\s*\/>/g) ?? []).some((tag) => /\bscrollCue\b/.test(tag))) users.push(e.name);
    }
  };
  walk(dir);
  expect(users).toEqual(['AttendancePage.tsx']);
});
