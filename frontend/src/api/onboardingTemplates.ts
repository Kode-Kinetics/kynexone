import client from './client';

export type OwnerRole = 'HR' | 'IT' | 'Manager' | 'Payroll' | 'Finance' | 'Admin' | 'Gov' | 'Employee';
export const OWNER_ROLES: readonly OwnerRole[] = ['HR', 'IT', 'Manager', 'Payroll', 'Finance', 'Admin', 'Gov', 'Employee'];
export type NationalityCondition = 'any' | 'saudi' | 'gcc' | 'expat';
export const NATIONALITY_CONDITIONS: readonly NationalityCondition[] = ['any', 'saudi', 'gcc', 'expat'];
export const LINK_SECTIONS = ['person', 'emergency', 'arabicName', 'id', 'bank', 'documents'] as const;

export interface ChecklistTask {
  id?: string;
  title: string;
  titleAr?: string | null;
  description?: string | null;
  descriptionAr?: string | null;
  ownerRole: OwnerRole;
  dueOffsetDays: number;
  blocksActivation: boolean;
  condition: { nationalityClass: NationalityCondition };
  linkSection?: string | null;
  isStatutory: boolean;
  evidenceRequired: boolean;
  order?: number;
}
export interface ChecklistSummary { id: string; companyId: string | null; name: string; nameAr?: string | null; isDefault: boolean; isActive: boolean; isDeleted: boolean; taskCount: number }
export interface ChecklistTemplate { id: string; companyId: string | null; name: string; nameAr?: string | null; isDefault: boolean; isActive: boolean; isDeleted: boolean; tasks: ChecklistTask[] }
export interface ChecklistTemplateInput { name: string; nameAr?: string | null; companyId: string; isDefault: boolean; tasks: ChecklistTask[] }
export interface StarterTemplate { key: string; name: string; nameAr: string; tasks: ChecklistTask[] }
export interface PreviewTask { templateTaskId: string; title: string; titleAr?: string | null; ownerRole: OwnerRole; dueDate: string | null; dueWhenStartSet: boolean; blocksActivation: boolean }
export interface Preview { nationalityClass: string; blocksActivationCount: number; tasks: PreviewTask[] }

export interface HireTemplateDefaults { departmentId?: string; employmentType?: string; contractType?: string; probationDays?: number; workLocation?: string; managerRule?: 'none' | 'department-head' }
export interface HireTemplate {
  id: string; companyId: string; name: string; nameAr?: string | null;
  matchDesignationId?: string | null; matchGradeId?: string | null; matchBranchId?: string | null; checklistId?: string | null;
  essCompletionSections: string[]; defaults: HireTemplateDefaults; isActive: boolean; isDeleted: boolean;
  /** Tasks and blocking tasks in the template's checklist, before the hire's nationality filter. */
  checklistTaskCount?: number; checklistBlockingCount?: number;
}
export interface HireTemplateInput {
  name: string; nameAr?: string | null; companyId: string; matchDesignationId?: string | null; matchGradeId?: string | null; matchBranchId?: string | null;
  checklistId?: string | null; essCompletionSections: string[]; defaults: HireTemplateDefaults; isActive: boolean;
}
export interface RemoveOutcome { outcome: 'deleted' | 'turnedOff'; used: number }

export interface WorklistItem {
  taskId: string; employee: { id: number; code: string; name: string; startDate: string };
  title: string; titleAr?: string | null; ownerRole: OwnerRole; dueDate: string | null; overdue: boolean; blocksActivation: boolean;
  status: string; linkSection?: string | null; evidenceRequired: boolean; evidenceDocumentId?: string | null;
}

const base = '/api/onboarding';
export const onboardingTemplatesApi = {
  checklists: {
    list: (companyId?: string, includeDeleted = false) => client.get<ChecklistSummary[]>(`${base}/checklist-templates`, { params: { companyId, includeDeleted } }).then(r => r.data),
    get: (id: string) => client.get<ChecklistTemplate>(`${base}/checklist-templates/${id}`).then(r => r.data),
    create: (body: ChecklistTemplateInput) => client.post<ChecklistTemplate>(`${base}/checklist-templates`, body).then(r => r.data),
    update: (id: string, body: ChecklistTemplateInput) => client.put<ChecklistTemplate>(`${base}/checklist-templates/${id}`, body).then(r => r.data),
    remove: (id: string) => client.delete<RemoveOutcome>(`${base}/checklist-templates/${id}`).then(r => r.data),
    restore: (id: string) => client.post<ChecklistTemplate>(`${base}/checklist-templates/${id}/restore`).then(r => r.data),
    copyTo: (id: string, companyId: string) => client.post<ChecklistTemplate>(`${base}/checklist-templates/${id}/copy-to/${companyId}`).then(r => r.data),
    preview: (id: string, nationalityClass: string, startDate?: string) => client.post<Preview>(`${base}/checklist-templates/${id}/preview`, { nationalityClass, startDate }).then(r => r.data),
    starters: () => client.get<StarterTemplate[]>(`${base}/checklist-templates/starters`).then(r => r.data),
    copyStarter: (key: string, companyId: string) => client.post<ChecklistTemplate>(`${base}/checklist-templates/starters/${key}/copy-to/${companyId}`).then(r => r.data),
  },
  hire: {
    list: (companyId?: string, includeDeleted = false) => client.get<HireTemplate[]>(`${base}/hire-templates`, { params: { companyId, includeDeleted } }).then(r => r.data),
    create: (body: HireTemplateInput) => client.post<HireTemplate>(`${base}/hire-templates`, body).then(r => r.data),
    update: (id: string, body: HireTemplateInput) => client.put<HireTemplate>(`${base}/hire-templates/${id}`, body).then(r => r.data),
    remove: (id: string) => client.delete<RemoveOutcome>(`${base}/hire-templates/${id}`).then(r => r.data),
    restore: (id: string) => client.post<HireTemplate>(`${base}/hire-templates/${id}/restore`).then(r => r.data),
    copyTo: (id: string, companyId: string) => client.post<{ template: HireTemplate; dropped: string[] }>(`${base}/hire-templates/${id}/copy-to/${companyId}`).then(r => r.data),
    checklistPreview: (id: string, nationality?: string, startDate?: string) =>
      client.get<{ tasks: PreviewTask[]; blockingCount: number }>(`${base}/hire-templates/${id}/checklist-preview`, { params: { nationality, startDate } }).then(r => r.data),
    resolve: (p: { companyId: string; designationId?: string; gradeId?: string; branchId?: string }) =>
      client.get<{ candidates: HireTemplate[]; suggestedId: string | null }>(`${base}/hire-templates/resolve`, { params: p }).then(r => r.data),
  },
  worklist: (p: { ownerRole?: string; status?: string; overdue?: boolean; companyId?: string; page?: number; pageSize?: number } = {}) =>
    client.get<{ total: number; items: WorklistItem[] }>(`${base}/worklist`, { params: p }).then(r => r.data),
  updateTask: (id: string, body: { status?: string; note?: string; dueDate?: string; dueOverrideReason?: string; evidenceDocumentId?: string }) =>
    client.patch(`${base}/tasks/${id}`, body).then(r => r.data),
};
