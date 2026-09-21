import client from './client';

// ── Wire types (mirror Infrastructure/Compliance/NitaqatContracts.cs) ─────────

export interface NitaqatRefusal {
  reason: string;
  message: string;
  remedy: string;
}

export interface NitaqatWeightLine {
  classification: string;
  countBasis: string;
  category: string;
  heads: number;
  numeratorWeightEach: number;
  denominatorWeightEach: number;
  numeratorTotal: number;
  denominatorTotal: number;
  sourceNote: string;
  isVerified: boolean;
}

export interface NitaqatBandStep {
  band: string;
  requiredPercent: number;
  percentGap: number;
  saudiHiresRequired: number | null;
  infeasible: string | null;
}

export interface NitaqatScenario {
  saudiHiresToNextBand: number | null;
  nextBand: string | null;
  expatHiresBeforeDowngrade: number | null;
  bandBelow: string | null;
  saudiLeaversBeforeDowngrade: number | null;
}

export interface NitaqatStanding {
  companyId: string;
  companyName: string;
  asOf: string;
  activityCode: string;
  activityNameEn: string;
  activityNameAr: string;
  sizeTierCode: string;
  sizeTierNameEn: string;
  sizeTierRank: number;
  saudiWeighted: number;
  totalWeighted: number;
  achievedPercent: number;
  rawSaudiHeadcount: number;
  rawTotalHeadcount: number;
  band: string;
  bandRank: number;
  restrictsServices: boolean;
  consequenceSummary: string;
  currentBandFloorPercent: number;
  nextBandUp: NitaqatBandStep | null;
  bandBelow: NitaqatBandStep | null;
  scenario: NitaqatScenario;
  breakdown: NitaqatWeightLine[];
  allInputsVerified: boolean;
  unverifiedInputs: string[];
  qiwaReportedBand: string | null;
  qiwaReportedOn: string | null;
  disagreesWithQiwa: boolean;
  /**
   * Which regime produced the floors: 'nitaqat_mutawar_curve' (the one in force since
   * 1 December 2021) or 'size_tier_grid' (pre-2021, or a manually loaded override).
   * Reported because the two are not equivalent and the reader deserves to know which
   * question was answered.
   */
  bandingMethod: string;
  bandingMethodNote: string;
}

export const NITAQAT_CURVE_METHOD = 'nitaqat_mutawar_curve';

/** Coverage of one economic activity's band floors, for the setup screen. */
export interface NitaqatActivityCoverage {
  activityCode: string;
  activityNameEn: string;
  activityGroup: string;
  sizeTiersCovered: number;
  sizeTiersTotal: number;
  isComplete: boolean;
  anyVerified: boolean;
  allVerified: boolean;
  effectiveFrom: string | null;
  sourceNote: string;
}

export interface NitaqatGridCoverage {
  activitiesTotal: number;
  activitiesWithAnyGrid: number;
  activitiesWithCompleteGrid: number;
  activitiesFullyVerified: number;
  /** Non-null whenever the grid is missing or unverified. Rendered verbatim. */
  configurationRequiredNotice: string | null;
  activities: NitaqatActivityCoverage[];
  sizeTierCodes: string[];
}

export interface NitaqatStandingResponse {
  ok: boolean;
  standing: NitaqatStanding | null;
  refusal: NitaqatRefusal | null;
}

export interface NitaqatTrendPoint {
  asOfDate: string;
  achievedPercent: number;
  saudiWeighted: number;
  totalWeighted: number;
  band: string;
  bandRank: number;
}

export interface NitaqatTrendResponse {
  ok: boolean;
  companyId: string;
  points: NitaqatTrendPoint[];
  changePercentagePoints: number | null;
  direction: string | null;
  projectedBandWarning: string | null;
  refusal: NitaqatRefusal | null;
}

export interface NitaqatHireImpact {
  ok: boolean;
  nationality: string | null;
  classification: string | null;
  count: number;
  currentPercent: number;
  currentBand: string;
  projectedPercent: number;
  projectedBand: string;
  bandChanges: boolean;
  bandImproves: boolean;
  summary: string;
  refusal: NitaqatRefusal | null;
}

export interface NitaqatActivity {
  code: string;
  nameEn: string;
  nameAr: string;
  activityGroup: string;
  isVerified: boolean;
  sourceNote: string;
}

// ── Band presentation ─────────────────────────────────────────────────────────

/**
 * Yellow was abolished in the 2021 balanced-Nitaqat revision and is deliberately
 * absent. Low Green is amber rather than green because it RESTRICTS services —
 * colouring it green would be the visual equivalent of the old single 0.35.
 */
export const BAND_STYLE: Record<string, { label: string; chip: string; bar: string }> = {
  Platinum: {
    label: 'Platinum',
    chip: 'bg-slate-200 text-slate-800 dark:bg-slate-300/20 dark:text-slate-100',
    bar: 'bg-slate-500 dark:bg-slate-300',
  },
  HighGreen: {
    label: 'High Green',
    chip: 'bg-emerald-100 text-emerald-700 dark:bg-emerald-500/20 dark:text-emerald-400',
    bar: 'bg-emerald-500',
  },
  MediumGreen: {
    label: 'Medium Green',
    chip: 'bg-emerald-100 text-emerald-700 dark:bg-emerald-500/20 dark:text-emerald-400',
    bar: 'bg-emerald-400',
  },
  LowGreen: {
    label: 'Low Green',
    chip: 'bg-amber-100 text-amber-700 dark:bg-amber-500/20 dark:text-amber-400',
    bar: 'bg-amber-500',
  },
  Red: {
    label: 'Red',
    chip: 'bg-rose-100 text-rose-700 dark:bg-rose-500/20 dark:text-rose-400',
    bar: 'bg-rose-500',
  },
};

export const BAND_ORDER = ['Red', 'LowGreen', 'MediumGreen', 'HighGreen', 'Platinum'];

export const bandLabel = (band: string | null | undefined) =>
  (band && BAND_STYLE[band]?.label) || band || '—';

export const bandStyle = (band: string | null | undefined) =>
  (band && BAND_STYLE[band]) || BAND_STYLE.Red;

// ── API ───────────────────────────────────────────────────────────────────────

export const nitaqatApi = {
  standing: (companyId?: string) =>
    client
      .get<NitaqatStandingResponse>('/api/saudi-compliance/nitaqat', {
        params: companyId ? { companyId } : undefined,
      })
      .then((r) => r.data),

  trend: (days = 180, companyId?: string) =>
    client
      .get<NitaqatTrendResponse>('/api/saudi-compliance/nitaqat/trend', {
        params: { days, ...(companyId ? { companyId } : {}) },
      })
      .then((r) => r.data),

  hireImpact: (nationality: string, count: number, companyId?: string) =>
    client
      .get<NitaqatHireImpact>('/api/saudi-compliance/nitaqat/hire-impact', {
        params: { nationality, count, ...(companyId ? { companyId } : {}) },
      })
      .then((r) => r.data),

  activities: () =>
    client.get<NitaqatActivity[]>('/api/saudi-compliance/nitaqat/activities').then((r) => r.data),

  /** What this tenant can actually band today, and the notice to show when it cannot. */
  gridCoverage: () =>
    client.get<NitaqatGridCoverage>('/api/saudi-compliance/nitaqat/grid').then((r) => r.data),

  saveProfile: (body: {
    companyId: string;
    activityCode: string;
    mhrsdEstablishmentNumber?: string;
    labourOfficeCode?: string;
    qiwaReportedBand?: string;
    qiwaReportedOn?: string | null;
  }) => client.put('/api/saudi-compliance/nitaqat/profile', body).then((r) => r.data),
};
