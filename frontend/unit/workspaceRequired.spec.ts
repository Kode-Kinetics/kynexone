import { expect, test } from '@playwright/test';
import { isWorkspaceRequired } from '../src/lib/publicAuth';

/**
 * "The server needs the company ID" (lib/publicAuth.isWorkspaceRequired), in both API generations:
 * this build's 400 `{ code: 'workspace_required' }`, and the previous API's validation problem for a
 * missing required TenantSlug, which is what the sign-in page meets while the frontend deploys ahead
 * of the backend.
 */

const axiosError = (status: number | undefined, data?: unknown) => ({ response: status === undefined ? undefined : { status, data } });

/** Exactly what ASP.NET Core answers for `[param: RequiredWorkspace] string TenantSlug` left out. */
const oldApiProblem = (key: string) => ({
  type: 'https://tools.ietf.org/html/rfc9110#section-15.5.1',
  title: 'One or more validation errors occurred.',
  status: 400,
  errors: { [key]: ['Workspace is required.'] },
  traceId: '00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-00',
});

test('the current API: 400 workspace_required', () => {
  expect(isWorkspaceRequired(axiosError(400, { code: 'workspace_required' }))).toBe(true);
});

test('the previous API: a validation problem naming TenantSlug, in any casing or path form', () => {
  for (const key of ['TenantSlug', 'tenantSlug', 'tenantslug', 'TENANTSLUG', '$.tenantSlug', 'request.TenantSlug', ' TenantSlug ']) {
    expect(isWorkspaceRequired(axiosError(400, oldApiProblem(key))), key).toBe(true);
  }
  // Other fields failing alongside it still mean the company ID is missing.
  expect(isWorkspaceRequired(axiosError(400, { errors: { Email: ['bad'], TenantSlug: ['Workspace is required.'] } }))).toBe(true);
});

test('anything else is not a request for the company ID', () => {
  expect(isWorkspaceRequired(axiosError(400, oldApiProblem('Email')))).toBe(false);
  expect(isWorkspaceRequired(axiosError(400, oldApiProblem('TenantSlugHint')))).toBe(false);
  expect(isWorkspaceRequired(axiosError(400, oldApiProblem('notTenantSlug')))).toBe(false);
  expect(isWorkspaceRequired(axiosError(400, { code: 'code_invalid' }))).toBe(false);
  expect(isWorkspaceRequired(axiosError(400, { errors: ['TenantSlug'] }))).toBe(false);
  expect(isWorkspaceRequired(axiosError(400, 'workspace_required'))).toBe(false);
  expect(isWorkspaceRequired(axiosError(400))).toBe(false);
  expect(isWorkspaceRequired(axiosError(401, { code: 'workspace_required' }))).toBe(false);
  expect(isWorkspaceRequired(axiosError(422, oldApiProblem('TenantSlug')))).toBe(false);
  expect(isWorkspaceRequired(axiosError(undefined))).toBe(false);
  expect(isWorkspaceRequired(null)).toBe(false);
  expect(isWorkspaceRequired(new Error('Network Error'))).toBe(false);
});
