import test from 'node:test';
import assert from 'node:assert/strict';
import { createFormatter, parseDateInput, toZonedWallClock } from '../src/utils/formatCore.ts';

const en = createFormatter({ language: 'en' });
const ar = createFormatter({ language: 'ar' });

test('defaults to the Riyadh time zone', () => {
  assert.equal(en.timeZone, 'Asia/Riyadh');
  assert.equal(createFormatter({ language: 'en', timeZone: 'Not/AZone' }).timeZone, 'Asia/Riyadh');
});

test('a UTC instant is shown on the Riyadh clock, whatever the device zone', () => {
  // 06:30Z is 09:30 in Riyadh (UTC+3).
  const shown = en.time('2026-10-10T06:30:00Z');
  assert.match(shown, /^9:30\s?AM$/i);
  // An offset-less API timestamp is UTC.
  assert.match(en.time('2026-10-10T06:30:00'), /^9:30\s?AM$/i);
});

test('the Riyadh calendar day advances before UTC midnight', () => {
  assert.equal(en.businessDate('2026-09-18T22:30:00Z'), '2026-09-19');
  assert.match(en.date('2026-09-18T22:30:00Z', 'long'), /19 September 2026|September 19, 2026/);
});

test('date-only values are calendar dates and never shift', () => {
  assert.match(en.date('2026-10-10', 'long'), /10 October 2026|October 10, 2026/);
  assert.equal(en.time('2026-10-10'), '—');
});

test('wall-clock times are shown as written', () => {
  assert.match(en.time('09:05:00'), /^9:05\s?AM$/i);
  assert.match(en.time('17:30'), /^5:30\s?PM$/i);
});

test('SAR money formats in English and Arabic', () => {
  const e = en.money(12500);
  assert.match(e, /SAR/);
  assert.match(e, /12,500\.00/);
  const a = ar.money(12500);
  assert.match(a, /ر\.س/);
  assert.match(a, /12,500\.00|12٬500٫00/);
  const arabic = createFormatter({ language: 'ar', arabicDigits: true }).money(12500);
  assert.match(arabic, /[٠-٩]/);
});

test('missing or invalid values render as a dash, never a guess', () => {
  assert.equal(en.money(null), '—');
  assert.equal(en.money(Number.NaN), '—');
  assert.equal(en.date(undefined), '—');
  assert.equal(en.date('not a date'), '—');
  assert.equal(parseDateInput(''), null);
});

test('Hijri secondary date is non-empty in both languages', () => {
  const h1 = en.hijri('2026-10-10');
  const h2 = ar.hijri('2026-10-10');
  assert.ok(h1.length > 0, 'english hijri');
  assert.ok(h2.length > 0, 'arabic hijri');
  assert.match(h1, /1448/);
  assert.match(h2, /1448/);
});

test('toZonedWallClock carries the Riyadh wall clock into local fields', () => {
  const wall = toZonedWallClock(new Date('2026-10-10T22:15:00Z'), 'Asia/Riyadh');
  assert.equal(wall.getDate(), 11);
  assert.equal(wall.getHours(), 1);
  assert.equal(wall.getMinutes(), 15);
});
