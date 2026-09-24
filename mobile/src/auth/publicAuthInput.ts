export function normalizeWorkspace(value: string | null | undefined): string {
  return (value ?? '').trim().toLowerCase();
}

export function requireWorkspace(value: string | null | undefined): string {
  const workspace = normalizeWorkspace(value);
  if (!workspace) throw new Error('Workspace is required.');
  return workspace;
}

export function normalizeEmail(value: string): string {
  return value.trim();
}

export function publicLoginInput(email: string, password: string, workspace: string) {
  const normalizedEmail = normalizeEmail(email);
  if (!normalizedEmail) throw new Error('Work email is required.');
  return {
    email: normalizedEmail,
    // Passwords are credentials, not identifiers. Preserve every code unit.
    password,
    tenantSlug: requireWorkspace(workspace),
  };
}

export function publicResetInput(resetToken: string, newPassword: string, workspace: string) {
  if (!resetToken) throw new Error('Reset token is required.');
  return {
    resetToken,
    newPassword,
    tenantSlug: requireWorkspace(workspace),
  };
}

export function publicInvitationInput(invitationToken: string, newPassword: string, workspace: string) {
  if (!invitationToken) throw new Error('Invitation token is required.');
  return {
    invitationToken,
    newPassword,
    tenantSlug: requireWorkspace(workspace),
  };
}
