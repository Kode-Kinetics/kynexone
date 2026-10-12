import client from './client';

export const COMPLETION_FIELDS = [
  { key: 'preferredName', label: 'Preferred name', maxLength: 120 },
  { key: 'personalEmail', label: 'Personal email', maxLength: 180 },
  { key: 'phone', label: 'Mobile number', maxLength: 60 },
  { key: 'maritalStatus', label: 'Marital status', maxLength: 60 },
  { key: 'emergencyContactName', label: 'Emergency contact name', maxLength: 180 },
  { key: 'emergencyContactPhone', label: 'Emergency contact phone', maxLength: 60 },
] as const;
export type CompletionKey = typeof COMPLETION_FIELDS[number]['key'];
export interface EmployeeCompletion {
  requested: boolean;
  status: 'NotRequested' | 'Open' | 'PendingHR' | 'Approved' | 'Rejected';
  selfServiceAvailable: boolean;
  requestId?: string;
  changes?: Partial<Record<CompletionKey, string>>;
  profile?: Partial<Record<CompletionKey, string>>;
  reviewNote?: string;
}
export const employeeCompletionApi = {
  get: (employeeId: number) => client.get<EmployeeCompletion>(`/api/employee-completion/${employeeId}`).then(r => r.data),
  request: (employeeId: number) => client.post<EmployeeCompletion>(`/api/employee-completion/${employeeId}`).then(r => r.data),
  my: () => client.get<EmployeeCompletion>('/api/ess/employee-completion').then(r => r.data),
  submit: (changes: Partial<Record<CompletionKey, string>>) => client.post<EmployeeCompletion>('/api/ess/employee-completion/profile', { changes }).then(r => r.data),
  decide: (id: string, decision: 'approve' | 'reject', notes: string) => client.post(`/api/ess/profile-change-requests/${id}/${decision}`, { notes }),
  upload: (form: FormData) => client.post('/api/ess/documents', form),
};
