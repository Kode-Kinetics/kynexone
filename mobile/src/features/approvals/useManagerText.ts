// Turns the manager model (approvalModel / teamModel) into translated, locale-formatted words.
// One place, so Home, Approvals and Team say the same thing the same way.

import { useMemo } from 'react';
import { useTranslation } from 'react-i18next';
import { useFormat } from '@/utils/format';
import {
  hoursAndMinutes,
  roleKey,
  type ApprovalKind,
  type DecisionOutcome,
  type InboxItem,
  type QueueTarget,
} from './approvalModel';
import type { TeamBucket } from './teamModel';

export function useManagerText() {
  const { t } = useTranslation();
  const f = useFormat();

  return useMemo(() => {
    // Keys built from model enums (kind, role, bucket) are checked by the strings parity test.
    const tx = t as unknown as (key: string, options?: Record<string, unknown>) => string;
    const kind = (k: ApprovalKind) => tx(`manager.kind.${k}`);

    const days = (n: number | null | undefined): string => {
      if (n === null || n === undefined || !Number.isFinite(n)) return f.number(null);
      const count = f.number(n);
      if (!Number.isInteger(n)) return t('manager.days.fraction', { n: count });
      if (n === 1) return t('manager.days.one', { n: count });
      if (n === 2) return t('manager.days.two', { n: count });
      if (n >= 3 && n <= 10) return t('manager.days.few', { n: count });
      return t('manager.days.many', { n: count });
    };

    const duration = (minutes: number | null | undefined): string => {
      const hm = hoursAndMinutes(minutes ?? null);
      if (!hm) return f.number(null);
      if (hm.hours && hm.minutes) return t('manager.duration.hm', { h: f.number(hm.hours), m: f.number(hm.minutes) });
      if (hm.hours) return t('manager.duration.h', { h: f.number(hm.hours) });
      return t('manager.duration.m', { m: f.number(hm.minutes) });
    };

    const target = (q: QueueTarget | null): string | null => {
      if (!q) return null;
      if (q.type === 'person') return q.name;
      const key = roleKey(q.name);
      return key ? tx(`manager.role.${key}`) : q.name;
    };

    /** "28 Oct 2026" or "19 – 20 Oct 2026" style range from calendar dates. */
    const dateRange = (start: string | null, end: string | null): string => {
      if (!start) return f.date(null);
      if (!end || end === start) return f.date(start, 'medium');
      return t('manager.range', { from: f.date(start, 'dayMonth'), to: f.date(end, 'medium') });
    };

    const requester = (item: Pick<InboxItem, 'requesterName'>) => item.requesterName ?? t('manager.someone');
    const subject = (item: Pick<InboxItem, 'subject' | 'kind'>) => item.subject ?? kind(item.kind);

    const outcome = (o: DecisionOutcome, item: InboxItem): string => {
      const vars = { subject: subject(item), name: requester(item) };
      switch (o.kind) {
        case 'approved': return t('manager.approvals.outcome.approved', vars);
        case 'rejected': return t('manager.approvals.outcome.rejected', vars);
        case 'returned': return t('manager.approvals.outcome.returned', vars);
        case 'movedOn': {
          const next = target(o.next);
          return next
            ? t('manager.approvals.outcome.movedOn', { ...vars, next })
            : t('manager.approvals.outcome.movedOnUnknown', vars);
        }
        default: return t('manager.approvals.outcome.recorded', vars);
      }
    };

    const bucket = (b: TeamBucket) => tx(`manager.status.${b}`);

    return { tx, kind, days, duration, target, dateRange, requester, subject, outcome, bucket };
  }, [f, t]);
}

export type ManagerText = ReturnType<typeof useManagerText>;

/** Icon per approval kind (non-directional, never mirrored). */
export const KIND_ICON: Record<ApprovalKind, 'airplane-outline' | 'time-outline' | 'create-outline' | 'card-outline' | 'cash-outline' | 'document-text-outline' | 'briefcase-outline' | 'star-outline' | 'person-outline' | 'document-outline'> = {
  LEAVE: 'airplane-outline',
  OVERTIME: 'time-outline',
  ATTENDANCE_CORRECTION: 'create-outline',
  LOAN: 'card-outline',
  ADVANCE: 'cash-outline',
  PAYROLL: 'document-text-outline',
  RECRUITMENT: 'briefcase-outline',
  APPRAISAL: 'star-outline',
  EMPLOYEE_CHANGE: 'person-outline',
  OTHER: 'document-outline',
};
