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

/** `{ tenantSlug }` only when one was given: without it the server finds the company from the email. */
export function optionalWorkspace(value: string | null | undefined): { tenantSlug?: string } {
  const workspace = normalizeWorkspace(value);
  return workspace ? { tenantSlug: workspace } : {};
}

/**
 * The company ID is OPTIONAL: the server resolves the company from the email's domain and answers
 * 400 `{ code: 'workspace_required' }` only when it cannot, and the sign-in screen then asks for it.
 */
export function publicLoginInput(email: string, password: string, workspace?: string | null) {
  const normalizedEmail = normalizeEmail(email);
  if (!normalizedEmail) throw new Error('Work email is required.');
  return {
    email: normalizedEmail,
    // Passwords are credentials, not identifiers. Preserve every code unit.
    password,
    ...optionalWorkspace(workspace),
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
