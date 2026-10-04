import { expect, test } from '@playwright/test';
import { evaluatePasswordRequirements, type PasswordPolicy } from '../src/lib/passwordRequirements';

const policy: PasswordPolicy = {
  passwordMinLength: 12,
  passwordRequireUppercase: true,
  passwordRequireLowercase: true,
  passwordRequireDigit: true,
  passwordRequireSpecial: true,
};

test('requirements update individually and revert when characters are removed', () => {
  const met = (password: string) => evaluatePasswordRequirements(password, policy).requirements.filter(rule => rule.met).map(rule => rule.id);
  expect(met('')).toEqual([]);
  expect(met('A')).toEqual(['uppercase']);
  expect(met('Ab')).toEqual(['uppercase', 'lowercase']);
  expect(met('Ab1')).toEqual(['uppercase', 'lowercase', 'number']);
  expect(met('Ab1!')).toEqual(['uppercase', 'lowercase', 'number', 'special']);
  expect(evaluatePasswordRequirements('Ab1!abcdefgh', policy).valid).toBe(true);
  expect(evaluatePasswordRequirements('Ab1abcdefgh', policy).valid).toBe(false);
  expect(met('Ab1abcdefgh')).not.toContain('special');
});

test('honors tenant flags, configured length, and the server minimum floor', () => {
  const relaxed = { ...policy, passwordMinLength: 6, passwordRequireUppercase: false, passwordRequireLowercase: false, passwordRequireDigit: false, passwordRequireSpecial: false };
  expect(evaluatePasswordRequirements('aaaaaaaaa', relaxed).requirements).toEqual([{ id: 'length', label: 'At least 10 characters', met: false }]);
  expect(evaluatePasswordRequirements('aaaaaaaaaa', relaxed).valid).toBe(true);
  expect(evaluatePasswordRequirements('Ab1!abcdefgh', { ...policy, passwordMinLength: 14 }).valid).toBe(false);
});

test('matches server Unicode categories and scalar length, not UTF-16 length', () => {
  expect(evaluatePasswordRequirements('Éé١!abcdefgh', policy).valid).toBe(true);
  expect(evaluatePasswordRequirements('Ab1!abcde😀', policy).requirements[0].met).toBe(false);
  expect(evaluatePasswordRequirements('Ab1abcdefgh ', policy).requirements.find(rule => rule.id === 'special')?.met).toBe(false);
  expect(evaluatePasswordRequirements('Ab1abcdefgh😀', policy).valid).toBe(true);
});

test('does not claim success for control, hidden format, or lone surrogate characters', () => {
  for (const character of ['\n', '\u200b', '\ud800']) {
    const result = evaluatePasswordRequirements(`Ab1!abcdefgh${character}`, policy);
    expect(result.requirements.every(rule => rule.met)).toBe(true);
    expect(result.hasInvalidCharacters).toBe(true);
    expect(result.valid).toBe(false);
  }
});
