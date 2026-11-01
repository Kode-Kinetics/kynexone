'use client';

import { Ban, CheckCircle2, Circle, KeyRound } from 'lucide-react';
import type { ReactNode } from 'react';
import type { EmployeeAccessState } from '../../api/employeeAccess';
import { useLocale } from '../../contexts/LocaleContext';
import { deriveThreeStates, type AccessTone } from '../../lib/employeeAccess';
import { StatusChip } from '../StatusChip';

const ICON = 'h-3 w-3 shrink-0';
const TONE_ICON: Record<string, ReactNode> = {
  circle: <Circle className={ICON} aria-hidden="true" />,
  ok: <CheckCircle2 className={ICON} aria-hidden="true" />,
  stop: <Ban className={ICON} aria-hidden="true" />,
  key: <KeyRound className={ICON} aria-hidden="true" />,
};

/**
 * The list's access column: the same three answers as the profile, compact. The list knows the
 * employment status and the merged access state only, so it never claims "Awaiting pay approval"
 * (that needs the readiness check on the profile). Reset sign-in is per person, never bulk.
 */
export function ThreeStateCell({ status, accessState, loginEnabled, passwordConfigured, workEmail, onReset }: {
  status: string;
  accessState?: EmployeeAccessState;
  /** Independent facts from the API; absent on older APIs (the cell then derives from accessState). */
  loginEnabled?: boolean;
  passwordConfigured?: boolean;
  workEmail?: string | null;
  /** Present only for a holder of employees.access.reset. */
  onReset?(): void;
}) {
  const { t } = useLocale();
  if (!accessState) return <span className="text-xs text-slate-500 dark:text-slate-400">-</span>;
  const states = deriveThreeStates({ status, workEmail }, { state: accessState, loginEnabled, passwordConfigured, workEmail }, null);
  const rows: Array<{ label: string; text: string; tone: AccessTone; icon: ReactNode }> = [
    states.employee === 'active'
      ? { label: 'Employee active', text: 'Active', tone: 'emerald', icon: TONE_ICON.ok }
      : { label: 'Employee active', text: 'Not active yet', tone: 'slate', icon: TONE_ICON.circle },
    states.login === 'login_enabled' ? { label: 'Login enabled', text: 'Login enabled', tone: 'emerald', icon: TONE_ICON.ok }
      : states.login === 'access_stopped' ? { label: 'Login enabled', text: 'Access stopped', tone: 'rose', icon: TONE_ICON.stop }
        : { label: 'Login enabled', text: 'No login yet', tone: 'slate', icon: TONE_ICON.circle },
    states.password === 'password_set' ? { label: 'Password configured', text: 'Password set', tone: 'emerald', icon: TONE_ICON.ok }
      : states.password === 'code_given' ? { label: 'Password configured', text: 'Code given, not used', tone: 'blue', icon: TONE_ICON.key }
        : states.password === 'unknown' ? { label: 'Password configured', text: 'Password status unknown', tone: 'slate', icon: TONE_ICON.circle }
          : { label: 'Password configured', text: 'Not set up', tone: 'slate', icon: TONE_ICON.circle },
  ];
  return (
    <div className="flex flex-col items-start gap-1" data-testid="three-state-cell">
      {states.needsWorkEmail && <span className="text-xs italic text-slate-500 dark:text-slate-400">{t('Needs work email first')}</span>}
      <ul className="m-0 flex list-none flex-col items-start gap-1 p-0">
        {rows.map((row) => (
          <li key={row.label}>
            <span className="sr-only">{t(row.label)}: </span>
            <StatusChip label={t(row.text)} tone={row.tone} icon={row.icon} />
          </li>
        ))}
      </ul>
      {onReset && states.password === 'password_set' && !states.newCodeLive && states.login !== 'access_stopped' && (
        <button type="button" onClick={onReset} className="btn-secondary min-h-11 px-2 text-xs md:min-h-7">{t('Reset sign-in')}</button>
      )}
    </div>
  );
}
