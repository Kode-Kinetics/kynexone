import { expect, test } from '@playwright/test';
import { tenantHrConfigRequestBody, type TenantHrConfigDto } from '../src/api/tenantHrConfig';

/**
 * Establishment panel -> PUT /api/tenant-hr-config. The eleven retired switches are no longer part of the contract, and
 * the mode-change reason must travel under the name the server reads (EstablishmentModeChangeReason): it used to be sent
 * as `reason`, which the server ignored, so a mode change was always refused for want of a reason.
 */
const fetched = {
  establishmentEnforcementMode: 'Enforced', engagementMinCohortSize: 5, engagementFreeTextRetentionMonths: 24, updatedAtUtc: '2026-10-25T00:00:00Z',
} as TenantHrConfigDto;

test('the body carries the mode and the reason under the server field names, and nothing retired', () => {
  expect(tenantHrConfigRequestBody({ ...fetched, establishmentEnforcementMode: 'Advisory' }, 'pilot')).toEqual({
    establishmentEnforcementMode: 'Advisory', establishmentModeChangeReason: 'pilot',
  });
  expect(tenantHrConfigRequestBody(fetched)).toEqual({ establishmentEnforcementMode: 'Enforced' });
});

test('an old payload that still carries the retired switches does not leak them into the request', () => {
  const old = { ...fetched, useDeptHeadApproval: true, requireGradeForApprovalPolicy: false } as unknown as TenantHrConfigDto;
  expect(Object.keys(tenantHrConfigRequestBody(old))).toEqual(['establishmentEnforcementMode']);
});
