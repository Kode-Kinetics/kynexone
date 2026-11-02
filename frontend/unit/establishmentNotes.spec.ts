import { expect, test } from '@playwright/test';
import { NOTE_MAX_LENGTH, levelNoteEditable, noteChange, noteCounter } from '../src/lib/establishmentNotes';

test('the limit is the server limit', () => {
  expect(NOTE_MAX_LENGTH).toBe(500);
});

test('an untouched or unchanged note is not sent (undefined = server keeps it)', () => {
  expect(noteChange(null, undefined)).toBeUndefined();
  expect(noteChange('Hiring freeze', undefined)).toBeUndefined();
  expect(noteChange('Hiring freeze', 'Hiring freeze')).toBeUndefined();
  expect(noteChange('Hiring freeze', '  Hiring freeze  ')).toBeUndefined();
  expect(noteChange(null, '')).toBeUndefined();
  expect(noteChange(null, '   ')).toBeUndefined();
});

test('a changed note is sent trimmed; clearing a saved note sends an empty string', () => {
  expect(noteChange(null, '  Q3 expansion ')).toBe('Q3 expansion');
  expect(noteChange('Old', 'New')).toBe('New');
  expect(noteChange('Old', '')).toBe('');
  expect(noteChange('Old', '   ')).toBe('');
});

test('markup is plain text and passes through untouched', () => {
  expect(noteChange(null, '<b>x</b>')).toBe('<b>x</b>');
});

test('a level note needs a budgeted headcount; 0 (frozen) counts', () => {
  expect(levelNoteEditable(null)).toBe(false);
  expect(levelNoteEditable(0)).toBe(true);
  expect(levelNoteEditable(4)).toBe(true);
});

test('the counter shows trimmed-free raw length against the limit', () => {
  expect(noteCounter('')).toBe('0/500');
  expect(noteCounter('abc')).toBe('3/500');
});
