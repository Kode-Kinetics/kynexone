import type { PolicyDraftSource, PolicyExtractionIssue, PolicyExtractionProposal, PolicyRequirement, PolicyReviewDecision, PolicyReviewDisposition, PolicyReviewItem, PolicyReviewRecord, PolicyReviewSource } from '../api/setupAssistant';

const dispositions: Record<PolicyReviewItem['kind'], PolicyReviewDisposition[]> = {
  supported: ['Reviewed', 'NeedsClarification', 'Deferred'],
  incomplete: ['NeedsClarification', 'Deferred', 'Answered', 'NotApplicable'],
  conflict: ['NeedsClarification', 'Deferred', 'Answered', 'NotApplicable'],
  unsupported: ['ManualProcess', 'ProductGap', 'NeedsClarification', 'Deferred', 'Answered', 'NotApplicable'],
  outOfScope: ['NotApplicable', 'NeedsClarification', 'Deferred'],
};
export const reviewDispositions = (item: PolicyReviewItem) => dispositions[item.kind];

/** Review IDs and unsaved decisions are company-bound; ordinary form edits are not scope changes. */
export function checkpointMatchesReviewCompany(setupSignature: string, companyId?: string): boolean {
  try {
    const parsed = JSON.parse(setupSignature);
    if (!parsed?.profile || typeof parsed.profile !== 'object' || Array.isArray(parsed.profile)) return false;
    const previousCompanyId = parsed.profile.companyId;
    if (previousCompanyId != null && typeof previousCompanyId !== 'string') return false;
    return (previousCompanyId ?? null) === (companyId ?? null);
  } catch { return false; }
}

export function validReviewDecision(item: PolicyReviewItem, decision: PolicyReviewDecision): { valid: boolean; message?: string } {
  if (!decision.disposition || !reviewDispositions(item).includes(decision.disposition)) return { valid: false, message: 'Choose a review disposition.' };
  if (decision.disposition !== 'Reviewed' && !decision.comment.trim()) return { valid: false, message: 'Explain this review decision in a comment.' };
  if (['NeedsClarification', 'Deferred', 'ManualProcess', 'ProductGap'].includes(decision.disposition) && !decision.owner?.trim()) return { valid: false, message: 'Assign a follow-up owner for this decision.' };
  if (decision.disposition === 'Answered' && (!decision.approvalReference?.trim() || !item.sources.some(source => source.sourceQuote.trim()))) return { valid: false, message: 'An approved answer needs an approval reference and a source passage.' };
  if (decision.comment.length > 4000 || (decision.owner?.length ?? 0) > 200 || (decision.approvalReference?.length ?? 0) > 1000) return { valid: false, message: 'The review comment or reference exceeds its limit.' };
  return { valid: true };
}

/** A review record does not change a proposal's data or activate an operational workflow. */
export function blockingReviewItems(items: PolicyReviewItem[], _decisions: Record<string, PolicyReviewDecision>, proposal: Pick<PolicyExtractionProposal, 'target' | 'field' | 'userCompleted'>): PolicyReviewItem[] {
  return items.filter(item => item.target === proposal.target && item.field === proposal.field
    && ['conflict', 'incomplete', 'unsupported'].includes(item.kind)
    && !(item.kind === 'incomplete' && proposal.userCompleted));
}

export function currentReviewDecisions(history: PolicyReviewRecord[], sources: PolicyDraftSource[], companyId: string): Record<string, PolicyReviewRecord> {
  const current: Record<string, PolicyReviewRecord> = {};
  for (const entry of [...history].sort((a, b) => b.reviewedAtUtc.localeCompare(a.reviewedAtUtc))) {
    if (entry.companyId === companyId && sources.some(source => source.documentId === entry.documentId && source.contentSha256 === entry.sourceHash) && !current[entry.itemKey]) current[entry.itemKey] = entry;
  }
  return current;
}

export function deduplicateCoverageNotes<T extends { section: string; kind: string; message: string }>(notes: T[]): (T & { occurrences: number })[] {
  const result = new Map<string, T & { occurrences: number }>();
  for (const note of notes) {
    const clean = { ...note, section: note.section.trim(), message: note.message.trim() };
    const key = JSON.stringify([clean.section.toLowerCase(), clean.kind, clean.message.replace(/\s+/g, ' ').toLowerCase()]);
    const found = result.get(key);
    if (found) found.occurrences++; else result.set(key, { ...clean, occurrences: 1 });
  }
  return [...result.values()];
}

export async function reviewItemKey(companyId: string | undefined, item: Omit<PolicyReviewItem, 'id'>, proposedValue?: unknown): Promise<string> {
  const identity = JSON.stringify([companyId ?? '', item.itemType, item.kind, item.section, item.summary, item.target ?? '', item.field ?? '', item.sourceClassification ?? '', proposedValue, item.sources.map(source => [source.documentId, source.contentSha256, source.sourceStart, source.sourceLength, source.sourceQuote])]);
  const digest = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(identity));
  return [...new Uint8Array(digest)].map(byte => byte.toString(16).padStart(2, '0')).join('');
}

export async function extractionReviewItems(companyId: string | undefined, source: PolicyDraftSource, issues: PolicyExtractionIssue[], requirements: PolicyRequirement[]): Promise<PolicyReviewItem[]> {
  const evidence = (item: { sourceQuote?: string | null; sourceStart?: number | null; sourceLength?: number | null }): PolicyReviewSource => ({ documentId: source.documentId, contentSha256: source.contentSha256, sourceName: source.name, sourceQuote: item.sourceQuote ?? '', sourceStart: item.sourceStart ?? 0, sourceLength: item.sourceLength ?? 0 });
  const items: Omit<PolicyReviewItem, 'id'>[] = [];
  for (const issue of issues) {
    if (issue.kind === 'info' || !issue.sourceQuote || issue.sourceStart == null || !issue.sourceLength) continue;
    const kind = issue.code === 'policy_conflict' ? 'conflict' : issue.code === 'policy_incomplete' || issue.kind === 'missing' ? 'incomplete' : 'unsupported';
    items.push({ itemType: 'issue', kind, section: issue.section, summary: issue.message, target: issue.target ?? undefined, field: issue.field ?? undefined, sources: [evidence(issue)] });
  }
  for (const requirement of requirements) items.push({ itemType: 'requirement', kind: 'unsupported', section: requirement.section, summary: requirement.summary, target: requirement.target ?? undefined, field: requirement.field ?? undefined, capability: requirement.capability, sourceClassification: requirement.sourceClassification, sources: [evidence(requirement)] });
  return Promise.all(items.map(async item => ({ ...item, id: await reviewItemKey(companyId, item) })));
}
