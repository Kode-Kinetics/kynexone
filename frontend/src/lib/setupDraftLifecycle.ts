import type { CompanyProfile } from '../api/setupAssistant';

export function setupProfileFingerprint(profile: CompanyProfile): string {
  const configuration: Record<string, unknown> = { ...profile.configuration };
  delete configuration.policyExtraction;
  delete configuration.policyIntakeText;
  delete configuration.policyReview;
  const ordered = (value: unknown): unknown => Array.isArray(value) ? value.map(ordered)
    : value && typeof value === 'object' ? Object.fromEntries(Object.entries(value).sort(([a], [b]) => a.localeCompare(b)).map(([key, item]) => [key, ordered(item)])) : value;
  return JSON.stringify(ordered({ ...profile, configuration }));
}

/** Legacy checkpoints store raw JSON. Compare values, not serialization order, on resume. */
export function matchesPolicyExtractionInputs(signature: string, profile: CompanyProfile): boolean {
  try {
    const saved = JSON.parse(signature);
    if (!saved?.profile || typeof saved.profile !== 'object' || Array.isArray(saved.profile)
      || !saved.value || typeof saved.value !== 'object' || Array.isArray(saved.value)) return false;
    // Checkpoints written before the profile dropped approvalModel / strictEntityScope still carry them.
    const normalize = (input: CompanyProfile) => {
      const rest = { ...input } as CompanyProfile & { approvalModel?: unknown; strictEntityScope?: unknown };
      delete rest.approvalModel;
      delete rest.strictEntityScope;
      return { ...rest, countryCode: typeof rest.countryCode === 'string' ? setupCountryCode(rest.countryCode) : rest.countryCode } as CompanyProfile;
    };
    return setupProfileFingerprint(normalize({ ...saved.profile, configuration: saved.value })) === setupProfileFingerprint(normalize(profile));
  } catch { return false; }
}

/** Company records also use ISO3; selects and the policy-intake profile use ISO2. */
export function setupCountryCode(value: string) {
  const code = value.trim().toUpperCase();
  const iso3: Record<string, string> = { SAU: 'SA', ARE: 'AE', QAT: 'QA', KWT: 'KW', BHR: 'BH', OMN: 'OM', EGY: 'EG', IND: 'IN', GBR: 'GB', USA: 'US' };
  return iso3[code] ?? code;
}
