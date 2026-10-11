import client from './client';

// ─────────────────────────────────────────────────────────────────────────────
// Engagement surveys (opt-in `engagement` flag). HR's side lives under /api/engagement, the respondent's under
// /api/ess/surveys. Every number on results comes back already suppressed by the server's CohortSuppression: a
// hidden cell has visible=false and null values, and the client never computes a metric itself.
// No response id or session token ever appears in a URL; the token travels in the request body only.
// ─────────────────────────────────────────────────────────────────────────────

export type SurveyMode = 'Identified' | 'Confidential' | 'Anonymous';
export type SurveyStatus = 'Draft' | 'Scheduled' | 'Open' | 'Closed' | 'Cancelled';
export type TemplateStatus = 'Draft' | 'Published' | 'Retired';
export type QuestionType = 'likert5' | 'enps' | 'single' | 'multi' | 'text';
export type ActionStatus = 'Open' | 'InProgress' | 'Done' | 'Cancelled';
export type Recurrence = 'None' | 'Monthly' | 'Quarterly' | 'SemiAnnual' | 'Annual';
export type Dimension = 'company' | 'department' | 'designation' | 'grade' | 'tenure';

export interface Driver { key: string; labelEn: string; labelAr: string }
export interface Option { key: string; labelEn: string; labelAr: string }
export interface Question {
  key: string; type: QuestionType; textEn: string; textAr: string; required: boolean; driver?: string | null; options?: Option[] | null;
}
export interface TemplateContent { drivers: Driver[]; questions: Question[] }

export interface Template {
  id: string; familyId: string; versionNumber: number; status: TemplateStatus; companyId: string | null;
  nameEn: string; nameAr: string; descriptionEn: string; descriptionAr: string; content: TemplateContent;
  publishedAtUtc: string | null; updatedAtUtc: string; version: number;
}

export interface Audience {
  companyIds?: string[]; departmentIds?: string[]; designationIds?: string[]; gradeIds?: string[]; excludeJoinedWithinDays?: number;
}

export interface Survey {
  id: string; templateId: string; titleEn: string; titleAr: string; mode: SurveyMode; status: SurveyStatus; companyId: string | null;
  opensAtUtc: string; closesAtUtc: string; recurrence: Recurrence; seriesId: string; cycleNumber: number; minCohortSize: number;
  reminderOffsetsDays: number[]; invitationCount: number; submitted: number | null; launchedAtUtc: string | null;
  closedAtUtc: string | null; version: number; commentDimension: CommentDimension;
}

/** The one attribute a survey's comments may be grouped by (never combined with another filter). */
export type CommentDimension = 'none' | Dimension;

export interface SurveyInput {
  templateId: string; titleEn: string; titleAr: string; mode: SurveyMode; companyId: string | null; audience: Audience;
  opensAtUtc: string; closesAtUtc: string; recurrence: Recurrence; minCohortSize: number | null; reminderOffsetsDays: number[];
  commentDimension: CommentDimension; version?: number;
}

export interface OptionItem { value: string; labelEn: string; labelAr: string }
export interface EngagementOptions {
  companies: OptionItem[]; departments: OptionItem[]; designations: OptionItem[]; grades: OptionItem[];
  gradeBands: OptionItem[]; tenureBands: OptionItem[];
  modes: { value: SurveyMode; noticeEn: string; noticeAr: string }[];
}

export interface Settings { minCohortSize: number; minCohortFloor: number; freeTextRetentionMonths: number }

export interface Participation { visible: boolean; invited: number | null; submitted: number | null; responseRatePct: number | null; definition: string }
export interface Distribution { key: string; labelEn: string; labelAr: string; count: number; pct: number }
export interface Enps { score: number; promotersPct: number; passivesPct: number; detractorsPct: number; answered: number }
export interface ItemResult {
  key: string; type: QuestionType; textEn: string; textAr: string; driver: string | null; visible: boolean;
  suppressionReason: string | null; answered: number | null; mean: number | null; favourablePct: number | null;
  enps: Enps | null; distribution: Distribution[]; commentCount: number | null;
}
export interface DriverResult {
  key: string; labelEn: string; labelAr: string; visible: boolean; suppressionReason: string | null;
  respondents: number | null; mean: number | null; favourablePct: number | null;
}
export interface Cell {
  value: string; label: string; visible: boolean; suppressionReason: string | null; respondents: number | null;
  favourablePct: number | null; enps: number | null; driverFavourablePct: Record<string, number | null>;
}
export interface Results {
  surveyId: string; titleEn: string; titleAr: string; mode: SurveyMode; status: SurveyStatus; cycleNumber: number; minCohortSize: number;
  participation: Participation; filters: { dimension: Dimension; value: string; label: string }[];
  visible: boolean; suppressionReason: string | null; respondents: number | null; favourablePct: number | null; enps: Enps | null;
  drivers: DriverResult[]; items: ItemResult[]; breakdown: { dimension: Dimension; cells: Cell[] } | null;
  suppressedMessage: string; generatedAtUtc: string;
  /** Confidential/Anonymous: false until the survey closes — then only the whole-survey response rate is given. */
  resultsAvailable: boolean; resultsAvailableFromUtc: string | null; commentDimension: CommentDimension;
  /** The reader's scope ('team' for a line manager, 'company' for a company-level reader; null = the whole survey).
   *  It counts as one of the two dimensions a view may combine. */
  scopeDimension: 'team' | 'company' | null;
}
export interface TrendPoint {
  surveyId: string; cycleNumber: number; opensAtUtc: string; status: SurveyStatus; visible: boolean; respondents: number | null;
  responseRatePct: number | null; favourablePct: number | null; enps: number | null;
}
export interface Comments { visible: boolean; suppressionReason: string | null; minCohortSize: number; message: string | null; comments: string[] }

export interface ProgressEntry { atUtc: string; byName: string; status: ActionStatus; note: string }
export interface Action {
  id: string; surveyId: string | null; surveyTitle: string; sourceKind: 'Overall' | 'Driver' | 'Item' | 'Cohort'; sourceRef: string;
  title: string; description: string; ownerEmployeeId: number; ownerName: string; dueOn: string; status: ActionStatus; overdue: boolean;
  completedAtUtc: string | null; progress: ProgressEntry[]; version: number;
}
export interface ActionInput {
  surveyId: string | null; sourceKind: Action['sourceKind']; sourceRef: string; title: string; description: string;
  ownerEmployeeId: number; dueOn: string; version?: number;
}

export type Filters = Partial<Record<Dimension, string>> & { breakdown?: Dimension; question?: string };

const qs = (filters: Filters) => {
  const params = new URLSearchParams();
  for (const [k, v] of Object.entries(filters)) if (v) params.set(k, v);
  const s = params.toString();
  return s ? `?${s}` : '';
};

export const engagementApi = {
  settings: () => client.get<Settings>('/api/engagement/settings').then((r) => r.data),
  updateSettings: (minCohortSize: number, freeTextRetentionMonths: number) =>
    client.put<Settings>('/api/engagement/settings', { minCohortSize, freeTextRetentionMonths }).then((r) => r.data),
  options: () => client.get<EngagementOptions>('/api/engagement/options').then((r) => r.data),

  templates: () => client.get<Template[]>('/api/engagement/templates').then((r) => r.data),
  createTemplate: (body: Omit<Template, 'id' | 'familyId' | 'versionNumber' | 'status' | 'publishedAtUtc' | 'updatedAtUtc' | 'version' | 'content'> & TemplateContent) =>
    client.post<Template>('/api/engagement/templates', body).then((r) => r.data),
  updateTemplate: (id: string, body: Record<string, unknown>) => client.put<Template>(`/api/engagement/templates/${id}`, body).then((r) => r.data),
  publishTemplate: (id: string, version: number) => client.post<Template>(`/api/engagement/templates/${id}/publish`, { version }).then((r) => r.data),
  newTemplateVersion: (id: string) => client.post<Template>(`/api/engagement/templates/${id}/versions`).then((r) => r.data),

  surveys: () => client.get<Survey[]>('/api/engagement/surveys').then((r) => r.data),
  survey: (id: string) => client.get<{ survey: Survey; template: Template; audience: Audience; privacyNoticeEn: string; privacyNoticeAr: string }>(`/api/engagement/surveys/${id}`).then((r) => r.data),
  audiencePreview: (companyId: string | null, audience: Audience, opensAtUtc: string) =>
    client.post<{ eligible: number }>('/api/engagement/surveys/audience-preview', { companyId, audience, opensAtUtc }).then((r) => r.data),
  createSurvey: (body: SurveyInput) => client.post<Survey>('/api/engagement/surveys', body).then((r) => r.data),
  updateSurvey: (id: string, body: SurveyInput) => client.put<Survey>(`/api/engagement/surveys/${id}`, body).then((r) => r.data),
  launch: (id: string, version: number) => client.post<Survey>(`/api/engagement/surveys/${id}/launch`, { version }).then((r) => r.data),
  close: (id: string, version: number) => client.post<Survey>(`/api/engagement/surveys/${id}/close`, { version }).then((r) => r.data),
  cancel: (id: string, version: number) => client.post<Survey>(`/api/engagement/surveys/${id}/cancel`, { version }).then((r) => r.data),
  remove: (id: string) => client.delete(`/api/engagement/surveys/${id}`).then(() => undefined),
  nonRespondents: (id: string) =>
    client.get<{ employeeId: number; fullName: string; employeeCode: string; remindersSent: number }[]>(`/api/engagement/surveys/${id}/non-respondents`).then((r) => r.data),

  results: (id: string, filters: Filters) => client.get<Results>(`/api/engagement/surveys/${id}/results${qs(filters)}`).then((r) => r.data),
  trend: (id: string) => client.get<TrendPoint[]>(`/api/engagement/surveys/${id}/trend`).then((r) => r.data),
  comments: (id: string, question: string, filters: Filters) =>
    client.get<Comments>(`/api/engagement/surveys/${id}/comments${qs({ ...filters, breakdown: undefined, question } as Filters)}`).then((r) => r.data),
  exportResults: (id: string, format: 'csv' | 'xlsx', filters: Filters, includeComments: boolean) =>
    client.post<Blob>(`/api/engagement/surveys/${id}/export`, { format, filters, includeComments }, { responseType: 'blob' }).then((r) => r.data),

  actions: (surveyId?: string) =>
    client.get<Action[]>(`/api/engagement/actions${surveyId ? `?surveyId=${surveyId}` : ''}`).then((r) => r.data),
  owners: (search: string) =>
    client.get<{ id: number; name: string; code: string }[]>(`/api/engagement/actions/owners?search=${encodeURIComponent(search)}`).then((r) => r.data),
  createAction: (body: ActionInput) => client.post<Action>('/api/engagement/actions', body).then((r) => r.data),
  updateAction: (id: string, body: ActionInput) => client.put<Action>(`/api/engagement/actions/${id}`, body).then((r) => r.data),
  progressAction: (id: string, version: number, status: ActionStatus, note: string) =>
    client.post<Action>(`/api/engagement/actions/${id}/progress`, { version, status, note }).then((r) => r.data),
};

// ── Respondent (Self-Service) ───────────────────────────────────────────────

export interface MySurvey {
  surveyId: string; titleEn: string; titleAr: string; mode: SurveyMode; closesAtUtc: string;
  status: 'NotStarted' | 'Draft' | 'Submitted' | 'Closed'; submittedOn: string | null; hasDraft: boolean;
}
export interface AnswerInput { questionKey: string; value?: number | null; choices?: string[] | null; text?: string | null }
export interface SurveySession {
  surveyId: string; token: string; titleEn: string; titleAr: string; mode: SurveyMode; minCohortSize: number;
  privacyNoticeEn: string; privacyNoticeAr: string; closesAtUtc: string; content: TemplateContent; draft: AnswerInput[];
}

export const essSurveysApi = {
  mine: () => client.get<MySurvey[]>('/api/ess/surveys').then((r) => r.data),
  open: (surveyId: string) => client.post<SurveySession>('/api/ess/surveys/open', { surveyId }).then((r) => r.data),
  saveDraft: (surveyId: string, token: string, answers: AnswerInput[]) =>
    client.put<{ savedOn: string }>('/api/ess/surveys/draft', { surveyId, token, answers }).then((r) => r.data),
  submit: (surveyId: string, token: string, answers: AnswerInput[]) =>
    client.post<{ submittedOn: string }>('/api/ess/surveys/submit', { surveyId, token, answers }).then((r) => r.data),
  myActions: () => client.get<Action[]>('/api/ess/surveys/actions').then((r) => r.data),
  progress: (actionId: string, version: number, status: ActionStatus, note: string) =>
    client.post<Action>('/api/ess/surveys/actions/progress', { actionId, version, status, note }).then((r) => r.data),
};

/** The machine reason on a refusal: 'survey_already_submitted', 'survey_closed', 'feature_not_enabled', … */
export function engagementRefusal(err: unknown): string | null {
  const data = (err as { response?: { data?: { error?: unknown } } })?.response?.data;
  return typeof data?.error === 'string' ? data.error : null;
}
