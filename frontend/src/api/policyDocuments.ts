import client from './client';

export interface PolicyDocument {
  id: string;
  originalName: string;
  mimeType: string;
  fileSizeBytes: number;
  status: 'Processing' | 'Ready' | 'Failed';
  chunkCount: number;
  errorMessage?: string;
  createdAtUtc: string;
  companyId?: string | null;
  publicationStatus: 'Draft' | 'Published' | 'Withdrawn';
  effectiveFromUtc?: string | null;
  effectiveToUtc?: string | null;
  contentSha256: string;
}
export interface PolicyCitation {
  documentId: string;
  chunkIndex: number;
  source: string;
  excerpt: string;
  versionHash: string;
}
export interface PolicyAskResponse {
  answer: string;
  sources: string[];
  isGrounded: boolean;
  citations?: PolicyCitation[];
}
export interface PolicyText { documentId: string; text: string; contentSha256: string; }
export interface PublishPolicyRequest { companyId: string; effectiveFromUtc: string; effectiveToUtc?: string | null; contentSha256: string; }

export const policyDocumentsApi = {
  list: () => client.get<PolicyDocument[]>('/api/ai/policy/documents').then(r => r.data),
  employeeList: () => client.get<PolicyDocument[]>('/api/ai/policy/employee/documents').then(r => r.data),
  text: (id: string, signal?: AbortSignal) => client.get<PolicyText>(`/api/ai/policy/documents/${id}/text`, { signal }).then(r => r.data),
  upload: (file: File, signal?: AbortSignal) => {
    const form = new FormData();
    form.append('file', file);
    return client.post<PolicyDocument>('/api/ai/policy/documents/upload', form, {
      headers: { 'Content-Type': 'multipart/form-data' }, signal,
    }).then(r => r.data);
  },
  publish: (id: string, body: PublishPolicyRequest) => client.post<PolicyDocument>(`/api/ai/policy/documents/${id}/publish`, body).then(r => r.data),
  withdraw: (id: string) => client.post<PolicyDocument>(`/api/ai/policy/documents/${id}/withdraw`, {}).then(r => r.data),
  delete: (id: string) => client.delete(`/api/ai/policy/documents/${id}`),
  ask: (question: string, signal?: AbortSignal) =>
    client.post<PolicyAskResponse>('/api/ai/policy/ask', { question }, { signal }).then(r => r.data),
  employeeAsk: (question: string, signal?: AbortSignal) =>
    client.post<PolicyAskResponse>('/api/ai/policy/employee/ask', { question }, { signal }).then(r => r.data),
};
