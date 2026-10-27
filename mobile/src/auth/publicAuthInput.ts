export function normalizeEmail(value: string): string {
  return value.trim();
}

/** Email and password only: an installation holds one organisation, so there is no company ID to send. */
export function publicLoginInput(email: string, password: string) {
  const normalizedEmail = normalizeEmail(email);
  if (!normalizedEmail) throw new Error('Work email is required.');
  return {
    email: normalizedEmail,
    // Passwords are credentials, not identifiers. Preserve every code unit.
    password,
  };
}

export function publicResetInput(resetToken: string, newPassword: string) {
  if (!resetToken) throw new Error('Reset token is required.');
  return { resetToken, newPassword };
}

export function publicInvitationInput(invitationToken: string, newPassword: string) {
  if (!invitationToken) throw new Error('Invitation token is required.');
  return { invitationToken, newPassword };
}
