import { test, expect } from '@playwright/test';
import {
  READINESS_UNKNOWN_LABEL, READINESS_UNKNOWN_SHORT,
  formatReadinessPercent, readinessCaption, readinessToneClass,
} from '../src/lib/complianceDisplay';

// The reported defect: a tenant with no employees showed "100% Readiness" with a full green bar
// and "0 ready / 0 blocked", directly above "Company GOSI employer ID is not set".

test('a readiness with nothing to measure never renders as 100%', () => {
  expect(formatReadinessPercent(null)).toBe(READINESS_UNKNOWN_SHORT);
  expect(formatReadinessPercent(null)).not.toContain('100');
  expect(formatReadinessPercent(undefined)).not.toContain('100');
});

test('a readiness with nothing to measure does not render as 0% either', () => {
  // 0% claims "everything is failing", which is as untrue as "everything is ready".
  expect(formatReadinessPercent(null)).not.toContain('0');
});

test('a real readiness still renders as a percentage', () => {
  expect(formatReadinessPercent(0)).toBe('0%');
  expect(formatReadinessPercent(66.7)).toBe('66.7%');
  expect(formatReadinessPercent(100)).toBe('100%');
});

test('the caption explains itself instead of showing a ready/blocked tally of zero', () => {
  expect(readinessCaption(null, '0 ready / 0 blocked')).toBe(READINESS_UNKNOWN_LABEL);
  expect(readinessCaption(42, '3 ready / 4 blocked')).toBe('3 ready / 4 blocked');
});

test('an undefined readiness is coloured neutral, never green', () => {
  const tone = readinessToneClass(null);
  expect(tone).toContain('slate');
  expect(tone).not.toContain('emerald');
});

test('a real readiness keeps its traffic-light colours', () => {
  expect(readinessToneClass(95)).toContain('emerald');
  expect(readinessToneClass(70)).toContain('amber');
  expect(readinessToneClass(10)).toContain('rose');
});
