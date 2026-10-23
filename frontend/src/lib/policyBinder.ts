import type { PolicyDraftSource, PolicyExtractionProposal, SetupConfiguration } from '../api/setupAssistant';
import type { PolicyOnboardingTaskDraft } from './policyFormDrafts';

/** Chunk retries must not multiply tasks; independent document versions stay distinguishable. */
export function appendOnboardingTaskDrafts(existing: PolicyOnboardingTaskDraft[], incoming: PolicyOnboardingTaskDraft[]): PolicyOnboardingTaskDraft[] {
  const key = (task: PolicyOnboardingTaskDraft) => JSON.stringify([task.documentId, task.contentSha256, task.id]);
  const seen = new Set(existing.map(key));
  return [...existing, ...incoming.filter(task => { const id = key(task); if (seen.has(id)) return false; seen.add(id); return true; })];
}

export interface SourcedPolicyProposal extends PolicyExtractionProposal {
  documentId: string;
  contentSha256: string;
  sourceName: string;
}

export function policySources(value: SetupConfiguration): PolicyDraftSource[] {
  if (value.policySources) return value.policySources;
  return value.policyDocumentId && value.policySourceHash
    ? [{ documentId: value.policyDocumentId, contentSha256: value.policySourceHash, name: 'Policy document' }]
    : [];
}

/** Scope is part of a leave rule's identity: grade-specific rules must not overwrite each other. */
export function policyItemKey(field: string, value: unknown): string {
  if (value === null || typeof value !== 'object') return JSON.stringify(value);
  const item = value as Record<string, unknown>;
  if (field === 'leavePolicies') return JSON.stringify([item.leaveTypeCode, item.gradeCode ?? '', item.departmentCode ?? '',
    // Employment type is a list: "Full-Time,Part-Time" and "part-time, full-time" are one scope (backend LeavePolicyScope).
    String(item.employmentType ?? '').split(',').map(s => s.trim().toLowerCase()).filter(Boolean).sort().join(',')].map(part => String(part ?? '').trim().toLowerCase()));
  return String(item.code ?? item.name ?? JSON.stringify(value)).trim().toLowerCase();
}

function stable(value: unknown): string {
  if (Array.isArray(value)) return `[${value.map(stable).join(',')}]`;
  if (value && typeof value === 'object') return `{${Object.entries(value).sort(([a], [b]) => a.localeCompare(b)).map(([key, item]) => `${JSON.stringify(key)}:${stable(item)}`).join(',')}}`;
  return JSON.stringify(value);
}

export function proposalKey(proposal: PolicyExtractionProposal): string {
  return `${proposal.target}.${proposal.field}.${Array.isArray(proposal.value) ? policyItemKey(proposal.field, proposal.value[0]) : ''}`;
}

export function proposalsConflict(first: PolicyExtractionProposal, second: PolicyExtractionProposal): boolean {
  if (proposalKey(first) === proposalKey(second)) return stable(first.value) !== stable(second.value);
  return first.target === second.target && first.field === 'overtimeModes' && second.field === 'overtimeModes'
    && Array.isArray(first.value) && Array.isArray(second.value)
    && (first.value.includes('NotApplicable') || second.value.includes('NotApplicable'));
}

/** Keep each supporting passage; repeat delivery of the same passage/value is idempotent. */
export function appendPolicyProposals(existing: SourcedPolicyProposal[], incoming: SourcedPolicyProposal[]): SourcedPolicyProposal[] {
  const next = [...existing];
  const fingerprint = (item: SourcedPolicyProposal) => JSON.stringify([proposalKey(item), stable(item.value), item.documentId, item.contentSha256, item.sourceStart, item.sourceLength]);
  const seen = new Map(existing.map((item, index) => [fingerprint(item), index]));
  for (const proposal of incoming) {
    const rows = Array.isArray(proposal.value) ? proposal.value.map(item => ({ ...proposal, value: [item] })) : [proposal];
    for (const row of rows) {
      const key = fingerprint(row);
      const existingIndex = seen.get(key);
      if (existingIndex === undefined) { seen.set(key, next.length); next.push(row); }
      else if (row.userCompleted && !next[existingIndex].userCompleted) next[existingIndex] = { ...next[existingIndex], userCompleted: true };
    }
  }
  return next;
}

/** Accepting one reviewed item updates that item only, never replaces an entire collection. */
export function mergePolicyValue(field: string, current: unknown, proposed: unknown): unknown {
  if (Array.isArray(proposed)) {
    if (field === 'overtimeModes' && proposed.includes('NotApplicable')) return ['NotApplicable'];
    const merged = Array.isArray(current) ? current.filter(item => field !== 'overtimeModes' || item !== 'NotApplicable') : [];
    for (const item of proposed) {
      const index = merged.findIndex(old => policyItemKey(field, old) === policyItemKey(field, item));
      if (index < 0) merged.push(item); else merged[index] = item;
    }
    return merged;
  }
  if (proposed && typeof proposed === 'object' && current && typeof current === 'object' && !Array.isArray(current)) return { ...current, ...proposed };
  return proposed;
}
