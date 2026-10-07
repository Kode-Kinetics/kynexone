import type { SecuritySetting } from '../api/identity';

export type PasswordPolicy = Pick<SecuritySetting,
  'passwordMinLength' | 'passwordRequireUppercase' | 'passwordRequireLowercase'
  | 'passwordRequireDigit' | 'passwordRequireSpecial'>;

export function evaluatePasswordRequirements(password: string, policy: PasswordPolicy) {
  // Match AccessManagementService's Unicode-scalar validation, including its
  // 10-character floor. Spaces are not special characters; punctuation/symbols are.
  const minimumLength = Math.max(10, policy.passwordMinLength);
  const requirements = [
    { id: 'length', label: `At least ${minimumLength} characters`, met: Array.from(password).length >= minimumLength },
    ...(policy.passwordRequireUppercase ? [{ id: 'uppercase', label: 'One uppercase letter', met: /\p{Lu}/u.test(password) }] : []),
    ...(policy.passwordRequireLowercase ? [{ id: 'lowercase', label: 'One lowercase letter', met: /\p{Ll}/u.test(password) }] : []),
    ...(policy.passwordRequireDigit ? [{ id: 'number', label: 'One number', met: /\p{Nd}/u.test(password) }] : []),
    ...(policy.passwordRequireSpecial ? [{ id: 'special', label: 'One special character (e.g. !@#$)', met: /[\p{P}\p{S}]/u.test(password) }] : []),
  ];
  const hasInvalidCharacters = /[\p{Cc}\p{Cf}\p{Cs}]/u.test(password);
  return {
    requirements,
    hasInvalidCharacters,
    valid: requirements.every(requirement => requirement.met) && !hasInvalidCharacters,
  };
}
