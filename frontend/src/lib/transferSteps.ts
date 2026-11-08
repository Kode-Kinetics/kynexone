/**
 * An employee transfer waits at one named step (current manager, new manager, HR). The server says which step, who holds it
 * and whether the signed-in user may decide it now; this file only words that and picks the endpoint that approves it.
 */
export type TransferStep = 'CurrentManager' | 'NewManager' | 'Hr';

export interface EmployeeTransferRow {
  id: string;
  employeeId: number;
  currentBranch: string;
  currentDepartment: string;
  currentDesignation: string;
  newBranch: string;
  newDepartment: string;
  newDesignation: string;
  newManagerEmployeeId?: number | null;
  effectiveDate: string;
  reason: string;
  status: string;
  createdAtUtc: string;
}

export interface EmployeeTransferView {
  transfer: EmployeeTransferRow;
  employeeName: string;
  employeeCode: string;
  currentManagerName?: string | null;
  newManagerName?: string | null;
  /** Null once the transfer is applied. */
  awaitingStep: TransferStep | null;
  /** The manager who holds the step; null when HR holds it or nobody can be named. */
  awaitingName?: string | null;
  canDecide: boolean;
}

type Translate = (key: string, vars?: Record<string, string | number>) => string;

const APPROVE_SEGMENT: Record<TransferStep, string> = {
  CurrentManager: 'approve-current-manager',
  NewManager: 'approve-new-manager',
  Hr: 'approve-hr',
};

export const transferApprovePath = (id: string, step: TransferStep) => `/api/employees/transfers/${id}/${APPROVE_SEGMENT[step]}`;

/** "Waiting for the current manager: Cora", "Waiting for HR approval", or "Applied". */
export function transferHolderLine(view: EmployeeTransferView, t: Translate): string {
  switch (view.awaitingStep) {
    case 'CurrentManager':
      return view.awaitingName ? t('Waiting for the current manager: {name}', { name: view.awaitingName }) : t('Waiting for the current manager (HR can decide it)');
    case 'NewManager':
      return view.awaitingName ? t('Waiting for the new manager: {name}', { name: view.awaitingName }) : t('Waiting for the new manager (HR can decide it)');
    case 'Hr':
      return t('Waiting for HR approval');
    default:
      return view.transfer.status === 'ApprovedApplied' ? t('Applied') : view.transfer.status;
  }
}

/** Who moves where, e.g. "Sam Subject: Finance". */
export function transferTitle(view: EmployeeTransferView, t: Translate): string {
  const target = view.transfer.newDepartment || view.transfer.newBranch || view.transfer.newDesignation || t('Transfer request');
  return `${view.employeeName}: ${target}`;
}

/** The confirmation names who moves where and which step is being approved, as one sentence per step so Arabic can reorder it. */
export function transferConfirmMessage(view: EmployeeTransferView, t: Translate): string {
  const vars = {
    employee: view.employeeName,
    target: view.transfer.newDepartment || view.transfer.newBranch || view.transfer.newDesignation || t('Transfer request'),
  };
  switch (view.awaitingStep) {
    case 'CurrentManager': return t('{employee} moves to {target}. You are approving as the current manager.', vars);
    case 'NewManager': return t('{employee} moves to {target}. You are approving as the new manager.', vars);
    default: return t('{employee} moves to {target}. You are approving as HR.', vars);
  }
}
