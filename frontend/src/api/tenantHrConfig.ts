import client from './client';

export type EstablishmentEnforcementMode = 'Off' | 'Advisory' | 'Enforced';

/**
 * Tenant HR configuration (api/tenant-hr-config).
 *
 * IMPORTANT: the PUT replaces ALL fields (same trap class as Render env vars) —
 * always round-trip the full GET payload and change only the field you mean to.
 */
export interface TenantHrConfigDto {
  useDeptHeadApproval: boolean;
  useHrFinalApproval: boolean;
  useSupervisorBeforeManager: boolean;
  allowDottedLineApproval: boolean;
  autoCreateDeptOnImport: boolean;
  autoCreateDesignationOnImport: boolean;
  requireImportPreviewBeforeCommit: boolean;
  allowCrossDeptManager: boolean;
  allowCrossLocationManager: boolean;
  requireCostCenterForPayroll: boolean;
  requireGradeForApprovalPolicy: boolean;
  /** Off | Advisory | Enforced — default Enforced (deploy-safe: absent budget rows enforce nothing). */
  establishmentEnforcementMode: EstablishmentEnforcementMode;
}

const FIELDS: (keyof TenantHrConfigDto)[] = [
  'useDeptHeadApproval', 'useHrFinalApproval', 'useSupervisorBeforeManager', 'allowDottedLineApproval',
  'autoCreateDeptOnImport', 'autoCreateDesignationOnImport', 'requireImportPreviewBeforeCommit',
  'allowCrossDeptManager', 'allowCrossLocationManager', 'requireCostCenterForPayroll',
  'requireGradeForApprovalPolicy', 'establishmentEnforcementMode',
];

export const tenantHrConfigApi = {
  get: () =>
    client.get<TenantHrConfigDto>('/api/tenant-hr-config').then(r => r.data),

  /**
   * Sends the complete request body (PUT replaces all fields — pass a full, freshly
   * fetched config). `reason` is mandatory server-side when the establishment
   * enforcement mode changes (the change is audited with actor + before/after).
   */
  update: (config: TenantHrConfigDto, reason?: string) => {
    const body: Record<string, unknown> = {};
    for (const f of FIELDS) body[f] = config[f];
    if (reason) body.reason = reason;
    return client.put<TenantHrConfigDto>('/api/tenant-hr-config', body).then(r => r.data);
  },
};
