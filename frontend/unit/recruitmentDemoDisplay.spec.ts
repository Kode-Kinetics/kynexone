import { test, expect } from '@playwright/test';
import { applicationStageChanges, offerRevisionLabel } from '../src/lib/recruitmentJourney';
import { LOCALE_DICTS } from '../src/i18n/translations';
import { enumLabel } from '../src/i18n/enumLabel';
import { createFormatter } from '../src/lib/format';

const ev = (id: string, eventType: string, stage: string) => ({ id, eventType, stage });

test.describe('application timeline: from and to stage', () => {
  test('each stage move names the stage it left and the stage it reached', () => {
    const changes = applicationStageChanges([
      ev('1', 'ApplicationCreated', 'Applied'),
      ev('2', 'StageAdvanced', 'Screening'),
      ev('3', 'StageAdvanced', 'Assessment'),
      ev('4', 'NoteAdded', 'Assessment'),
      ev('5', 'StageAdvanced', 'Interview'),
      ev('6', 'StageAdvanced', 'Offer'),
    ]);
    expect(changes.get('2')).toEqual({ from: 'Applied', to: 'Screening' });
    expect(changes.get('3')).toEqual({ from: 'Screening', to: 'Assessment' });
    expect(changes.get('5')).toEqual({ from: 'Assessment', to: 'Interview' });
    expect(changes.get('6')).toEqual({ from: 'Interview', to: 'Offer' });
    expect(changes.has('1')).toBe(false);
    expect(changes.has('4')).toBe(false);
  });

  test('an event with no stage is left alone and an unchanged stage is not a move', () => {
    const changes = applicationStageChanges([ev('1', 'StageAdvanced', ''), ev('2', 'StageAdvanced', 'Screening'), ev('3', 'StageAdvanced', 'Screening')]);
    expect(changes.has('1')).toBe(false);
    expect(changes.get('2')).toEqual({ from: 'Applied', to: 'Screening' });
    expect(changes.has('3')).toBe(false);
  });
});

test('an offer that predates managed revisions is "original", not "Legacy"', () => {
  expect(offerRevisionLabel(null)).toBe('Original');
  expect(offerRevisionLabel(undefined)).toBe('Original');
  expect(offerRevisionLabel(2)).toBe('2');
});

test('stage and timeline-event names have Arabic', () => {
  const t = (key: string) => LOCALE_DICTS.ar[key] ?? key;
  expect(enumLabel(t, 'RecruitmentStage', 'Screening')).not.toBe('Screening');
  expect(enumLabel(t, 'RecruitmentEvent', 'StageAdvanced')).not.toMatch(/[A-Za-z]/);
  for (const key of ['Timeline', 'Interviews', 'Offer']) expect(LOCALE_DICTS.ar[key], key).toBeTruthy();
});

test('stored UTC timestamps read as dates, in the viewer\'s language', () => {
  const iso = '2026-11-19T04:43:06.1954Z';
  const en = createFormatter({ locale: 'en', timeZone: 'Asia/Riyadh' });
  const ar = createFormatter({ locale: 'ar', timeZone: 'Asia/Riyadh' });
  expect(en.dateTime(iso)).not.toContain('2026-11-19T');
  expect(en.dateTime(iso)).toContain('19 Nov 2026');
  expect(ar.plain.dateTime(iso)).not.toContain('T04');
});
