import type { CompanyProfile, SetupDraft } from '../api/setupAssistant';

/** Resolve audit/extraction scope only; never retarget setup creation or apply. */
export function singleCompanyPolicyId(accountType: string | undefined, legalEntityName: string, companies: ReadonlyArray<{ id: string; legalNameEn: string; isActive: boolean; approvalStatus: string }>, selectedCompanyId?: string | null): string | undefined {
  const name = legalEntityName.trim().toLowerCase();
  if (accountType !== 'SingleCompany') return undefined;
  if (selectedCompanyId) return companies.find(company => company.id === selectedCompanyId && company.isActive && company.approvalStatus === 'Active')?.id;
  if (!name) return undefined;
  const matches = companies.filter(company => company.isActive && company.approvalStatus === 'Active' && company.legalNameEn.trim().toLowerCase() === name);
  return matches.length === 1 ? matches[0].id : undefined;
}

export interface SavedSetupPreview {
  draft: SetupDraft;
  companyId: string | null;
  profileFingerprint: string;
  generatedAt: string;
  engine: string;
  notes: string[];
}

export interface SavedSetupDraft {
  version: 1;
  savedAt: string;
  step: number;
  profile: CompanyProfile;
  reviewedPreview?: SavedSetupPreview;
}

export function setupProfileFingerprint(profile: CompanyProfile): string {
  const configuration: Record<string, unknown> = { ...profile.configuration };
  delete configuration.policyExtraction;
  delete configuration.policyIntakeText;
  delete configuration.policyReview;
  const ordered = (value: unknown): unknown => Array.isArray(value) ? value.map(ordered)
    : value && typeof value === 'object' ? Object.fromEntries(Object.entries(value).sort(([a], [b]) => a.localeCompare(b)).map(([key, item]) => [key, ordered(item)])) : value;
  return JSON.stringify(ordered({ ...profile, configuration }));
}

export function canReviewSavedPreview(preview: SavedSetupPreview | undefined | null, profile: CompanyProfile, companyId: string | null): preview is SavedSetupPreview {
  return !!preview && preview.companyId === companyId && preview.profileFingerprint === setupProfileFingerprint(profile);
}

/** Legacy checkpoints store raw JSON. Compare values, not serialization order, on resume. */
export function matchesPolicyExtractionInputs(signature: string, profile: CompanyProfile): boolean {
  try {
    const saved = JSON.parse(signature);
    if (!saved?.profile || typeof saved.profile !== 'object' || Array.isArray(saved.profile)
      || !saved.value || typeof saved.value !== 'object' || Array.isArray(saved.value)) return false;
    const normalize = (input: CompanyProfile) => ({ ...input, countryCode: typeof input.countryCode === 'string' ? setupCountryCode(input.countryCode) : input.countryCode });
    return setupProfileFingerprint(normalize({ ...saved.profile, configuration: saved.value })) === setupProfileFingerprint(normalize(profile));
  } catch { return false; }
}

export function setupDraftStorageKey(tenantId: string, userId: string, companyId: string) {
  return `kynexone-setup-draft:v1:${encodeURIComponent(tenantId)}:${encodeURIComponent(userId)}:${encodeURIComponent(companyId)}`;
}

/** Company records also use ISO3; selects and setup preview use ISO2. */
export function setupCountryCode(value: string) {
  const code = value.trim().toUpperCase();
  const iso3: Record<string, string> = { SAU: 'SA', ARE: 'AE', QAT: 'QA', KWT: 'KW', BHR: 'BH', OMN: 'OM', EGY: 'EG', IND: 'IN', GBR: 'GB', USA: 'US' };
  return iso3[code] ?? code;
}

export function readSetupDraft(storage: Pick<Storage, 'getItem'>, key: string): SavedSetupDraft | null {
  try {
    const saved = JSON.parse(storage.getItem(key) ?? 'null') as SavedSetupDraft | null;
    if (!saved || saved.version !== 1 || !Number.isInteger(saved.step) || saved.step < 0 || saved.step > 4
      || typeof saved.savedAt !== 'string' || !saved.profile || typeof saved.profile !== 'object') return null;
    const p = saved.profile;
    if (['countryCode', 'industry', 'companySize', 'currencyCode'].some(field => typeof p[field as keyof CompanyProfile] !== 'string')
      || !p.sections || typeof p.sections !== 'object' || !p.configuration || typeof p.configuration !== 'object') return null;
    const configuration = p.configuration as unknown as Record<string, unknown>;
    for (const field of ['attendanceMethods', 'overtimeModes', 'grades', 'leavePolicies', 'benefitPlans', 'policySources', 'policyFieldSources'] as const) {
      if (configuration[field] != null && !Array.isArray(configuration[field])) return null;
    }
    const preview = saved.reviewedPreview;
    if (preview && (!preview.draft || typeof preview.profileFingerprint !== 'string' || typeof preview.generatedAt !== 'string'
      || typeof preview.engine !== 'string' || !Array.isArray(preview.notes)
      || !['branches', 'departments', 'costCenters', 'designations', 'grades', 'gradePayComponents', 'leaveTypes', 'shifts', 'payComponents', 'statutoryRules', 'leavePolicies'].every(field => Array.isArray(preview.draft[field as keyof SetupDraft]))))
      delete saved.reviewedPreview;
    return saved;
  } catch { return null; }
}

export function saveSetupDraft(storage: Pick<Storage, 'setItem'>, key: string, profile: CompanyProfile, step: number, reviewedPreview?: SavedSetupPreview) {
  // Persist edited previews, but restoring them requires an explicit review action and matching inputs.
  const saved: SavedSetupDraft = { version: 1, savedAt: new Date().toISOString(), profile, step, reviewedPreview };
  storage.setItem(key, JSON.stringify(saved));
  return saved;
}
