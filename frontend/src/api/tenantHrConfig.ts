import client from './client';

export type EstablishmentEnforcementMode = 'Off' | 'Advisory' | 'Enforced';

/**
 * Tenant HR settings (api/tenant-hr-config): the establishment enforcement mode and the engagement survey settings.
 * The eleven approval-routing, import, hierarchy and payroll switches that used to be here were never read by anything
 * and are retired (2026-10-25); the server ignores them if an old client still sends them.
 */
export interface TenantHrConfigDto {
  /** Off | Advisory | Enforced — default Enforced (deploy-safe: absent budget rows enforce nothing). */
  establishmentEnforcementMode: EstablishmentEnforcementMode;
  engagementMinCohortSize: number;
  engagementFreeTextRetentionMonths: number;
  updatedAtUtc: string;
}

/**
 * The PUT body. Omitted fields keep their stored value, so only the mode (and, when it changes, the reason) is sent.
 * The reason travels as `establishmentModeChangeReason`, the name the server reads.
 */
export function tenantHrConfigRequestBody(config: Pick<TenantHrConfigDto, 'establishmentEnforcementMode'>, reason?: string): Record<string, unknown> {
  const body: Record<string, unknown> = { establishmentEnforcementMode: config.establishmentEnforcementMode };
  if (reason) body.establishmentModeChangeReason = reason;
  return body;
}

export const tenantHrConfigApi = {
  get: () =>
    client.get<TenantHrConfigDto>('/api/tenant-hr-config').then(r => r.data),

  /** `reason` is mandatory server-side when the establishment enforcement mode changes (audited with actor + before/after). */
  update: (config: TenantHrConfigDto, reason?: string) =>
    client.put<TenantHrConfigDto>('/api/tenant-hr-config', tenantHrConfigRequestBody(config, reason)).then(r => r.data),
};
