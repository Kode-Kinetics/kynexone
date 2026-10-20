export interface PolicyOnboardingTaskDraft {
  id: string; taskTitle: string; taskDescription: string; category: string;
  assignedToName: string | null; dueOffsetDays: number | null; isMandatory: boolean | null; condition: string | null;
  sourceQuote: string; sourceStart: number; sourceLength: number;
  documentId: string; contentSha256: string; sourceName: string;
}
export interface PolicyTemplateTaskForm {
  taskTitle: string; taskDescription: string; category: string; assignedToName: string;
  dueOffsetDays: number | ''; orderIndex: number; isMandatory: boolean | null;
  applicabilityConfirmed: boolean; overwriteConfirmed: boolean; checklistId: string;
}
export interface PolicyFormDraftScope { tenantId: string; userId: string; companyId: string }
export interface PolicyFormDraftBundle extends PolicyFormDraftScope {
  companyName: string; accountType: string;
  sources: { documentId: string; contentSha256: string; name: string }[];
  tasks: PolicyOnboardingTaskDraft[];
  edits?: Record<string, PolicyTemplateTaskForm>;
  saved?: Record<string, { checklistId: string; taskId: string; taskTitle: string }>;
  selectedTaskId?: string;
}
type StoredPolicyFormDraft = PolicyFormDraftBundle & { edits: NonNullable<PolicyFormDraftBundle['edits']>; saved: NonNullable<PolicyFormDraftBundle['saved']> };
type StorageReader = Pick<Storage, 'getItem'>;
const key = (scope: PolicyFormDraftScope) => `kynexone-policy-form-draft:v1:${encodeURIComponent(scope.tenantId)}:${encodeURIComponent(scope.userId)}:${encodeURIComponent(scope.companyId)}`;
const identity = (bundle: PolicyFormDraftBundle) => JSON.stringify([bundle.sources, bundle.tasks]);
function validBundle(bundle: PolicyFormDraftBundle): boolean {
  return !!bundle && [bundle.tenantId, bundle.userId, bundle.companyId, bundle.companyName, bundle.accountType].every(value => typeof value === 'string' && value.length > 0)
    && Array.isArray(bundle.sources) && Array.isArray(bundle.tasks) && bundle.sources.length <= 50 && bundle.tasks.length <= 1000
    && bundle.tasks.every(task => !!task && typeof task.id === 'string' && typeof task.taskTitle === 'string' && typeof task.taskDescription === 'string'
      && ['General', 'Document', 'IT', 'Training', 'Policy', 'Access'].includes(task.category)
      && (task.assignedToName === null || typeof task.assignedToName === 'string') && (task.condition === null || typeof task.condition === 'string')
      && (task.dueOffsetDays === null || Number.isInteger(task.dueOffsetDays)) && (task.isMandatory === null || typeof task.isMandatory === 'boolean')
      && typeof task.sourceQuote === 'string' && task.sourceQuote.length > 0 && Number.isInteger(task.sourceStart) && task.sourceStart >= 0 && Number.isInteger(task.sourceLength) && task.sourceLength > 0
      && bundle.sources.some(source => source.documentId === task.documentId && source.contentSha256 === task.contentSha256));
}
export function readPolicyFormDraft(storage: StorageReader, scope: PolicyFormDraftScope): StoredPolicyFormDraft | null {
  try {
    const bundle = JSON.parse(storage.getItem(key(scope)) ?? 'null') as PolicyFormDraftBundle;
    if (!validBundle(bundle) || bundle.tenantId !== scope.tenantId || bundle.userId !== scope.userId || bundle.companyId !== scope.companyId) return null;
    return { ...bundle, edits: bundle.edits ?? {}, saved: bundle.saved ?? {} };
  } catch { return null; }
}
export function savePolicyFormDraft(storage: StorageReader & Pick<Storage, 'setItem'>, bundle: PolicyFormDraftBundle, options?: { replace?: boolean }): StoredPolicyFormDraft {
  if (!validBundle(bundle)) throw new Error('Policy task draft source or scope is invalid. Extract the policy again.');
  const previous = readPolicyFormDraft(storage, bundle);
  const same = previous && identity(previous) === identity(bundle);
  if (previous && !same && !options?.replace && Object.keys(previous.edits).some(id => !previous.saved[id])) throw new Error('Existing policy form drafts have unsaved edits. Confirm replacement before opening a changed policy draft.');
  const next: StoredPolicyFormDraft = { ...bundle, edits: { ...(same ? previous.edits : {}), ...bundle.edits }, saved: bundle.saved ?? (same ? previous.saved : {}), selectedTaskId: bundle.selectedTaskId ?? (same ? previous.selectedTaskId : undefined) };
  storage.setItem(key(bundle), JSON.stringify(next));
  return next;
}
export function clearPolicyFormDraft(storage: Pick<Storage, 'removeItem'>, scope: PolicyFormDraftScope): void { storage.removeItem(key(scope)); }

/** Storage failure must never discard an edit already visible in an existing form. */
export function retainPolicyFormDraft(storage: StorageReader & Pick<Storage, 'setItem'>, bundle: PolicyFormDraftBundle): { bundle: PolicyFormDraftBundle; persisted: boolean; error?: string } {
  try { return { bundle: savePolicyFormDraft(storage, bundle), persisted: true }; }
  catch { return { bundle, persisted: false, error: 'The local policy draft could not be saved. Keep this page open and retry.' }; }
}

export function policyTaskDescription(description: string, condition: string | null): string {
  return condition && !description.includes(condition) ? `${description}\n\nPolicy condition: ${condition}` : description;
}
export function policyTaskForm(task: PolicyOnboardingTaskDraft, index: number, existing?: Partial<PolicyTemplateTaskForm>): PolicyTemplateTaskForm {
  return { taskTitle: task.taskTitle, taskDescription: policyTaskDescription(task.taskDescription, task.condition), category: task.category,
    assignedToName: task.assignedToName ?? existing?.assignedToName ?? '', dueOffsetDays: task.dueOffsetDays ?? existing?.dueOffsetDays ?? '',
    isMandatory: task.isMandatory ?? existing?.isMandatory ?? null, orderIndex: existing?.orderIndex ?? index + 1,
    applicabilityConfirmed: false, overwriteConfirmed: false, checklistId: existing?.checklistId ?? '' };
}
export function policyTaskProblem(form: PolicyTemplateTaskForm, task: PolicyOnboardingTaskDraft, matchesExisting: boolean): string | null {
  if (!form.taskTitle.trim()) return 'Task title is required.';
  if (form.dueOffsetDays === '' || !Number.isInteger(form.dueOffsetDays)) return 'Enter the deadline as a whole number of days relative to the task start date. Unknown deadlines cannot be saved as zero.';
  if (form.isMandatory === null) return 'Choose whether this task is mandatory or optional.';
  if (task.condition && !form.applicabilityConfirmed) return 'Confirm the policy condition applies to the selected checklist. Conditions are not enforced automatically.';
  if (matchesExisting && !form.overwriteConfirmed) return 'Review and confirm the existing task update before saving.';
  return null;
}
