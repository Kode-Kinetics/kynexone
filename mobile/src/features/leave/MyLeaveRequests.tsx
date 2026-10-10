import React, { useCallback, useEffect, useMemo, useState } from 'react';
import { RefreshControl, ScrollView, StyleSheet, Text, View } from 'react-native';
import { useTranslation } from 'react-i18next';
import {
  BottomSheet,
  LiquidButton,
  SectionHeader,
  SkeletonCard,
  StateCard,
  StatusPill,
  Surface,
} from '@/components/ui';
import { useTheme } from '@/theme/ThemeProvider';
import { useFormat } from '@/utils/format';
import { haptics } from '@/utils/haptics';
import { leaveRequestsApi, type LeaveTypeOption, type MyLeaveRequest, type NextApprover } from '@/api/leaveRequests';
import { leaveActionFor, leaveGroupFor, leaveStatusTone, type LeaveAction } from './leaveLogic';
import { Notice, TextArea } from './LeavePayParts';
import { useLeavePayText } from './useLeavePayText';

interface Props {
  reloadKey: number;
  sent: { request: MyLeaveRequest; typeName: string } | null;
  onDismissSent: () => void;
  onRequestNew: () => void;
}

type Group = 'inProgress' | 'upcoming' | 'past';
const GROUPS: Group[] = ['inProgress', 'upcoming', 'past'];

/** My leave requests: what is still moving first, with who it is waiting for and the one action allowed. */
export function MyLeaveRequests({ reloadKey, sent, onDismissSent, onRequestNew }: Props) {
  const { theme } = useTheme();
  const { t } = useTranslation();
  const f = useFormat();
  const text = useLeavePayText();
  const today = f.businessDate() ?? '';

  const [items, setItems] = useState<MyLeaveRequest[]>([]);
  const [types, setTypes] = useState<LeaveTypeOption[]>([]);
  const [approvers, setApprovers] = useState<Record<string, NextApprover | null>>({});
  const [loading, setLoading] = useState(true);
  const [refreshing, setRefreshing] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  const [target, setTarget] = useState<{ request: MyLeaveRequest; action: LeaveAction } | null>(null);
  const [note, setNote] = useState('');
  const [acting, setActing] = useState(false);
  const [actionError, setActionError] = useState<string | null>(null);

  const load = useCallback(
    async (refresh = false) => {
      if (refresh) setRefreshing(true);
      else setLoading(true);
      setError(null);
      try {
        const [mine, typeList] = await Promise.all([
          leaveRequestsApi.getMine(),
          leaveRequestsApi.getTypes().catch(() => [] as LeaveTypeOption[]),
        ]);
        setItems(mine);
        setTypes(typeList);
        // Who each pending request is waiting for — one small read per pending request.
        const pending = mine.filter((r) => r.kind === 'pending');
        const results = await Promise.allSettled(pending.map((r) => leaveRequestsApi.getNextApprover(r.id)));
        const next: Record<string, NextApprover | null> = {};
        pending.forEach((r, i) => {
          const result = results[i];
          next[r.id] = result.status === 'fulfilled' ? result.value : null;
        });
        setApprovers(next);
      } catch (e) {
        setError(text.error(e));
      } finally {
        setLoading(false);
        setRefreshing(false);
      }
    },
    [text],
  );

  useEffect(() => {
    void load();
  }, [load, reloadKey]);

  const grouped = useMemo(() => {
    const out: Record<Group, MyLeaveRequest[]> = { inProgress: [], upcoming: [], past: [] };
    for (const r of items) out[leaveGroupFor(r.kind, r.endDate, today)].push(r);
    // Coming up reads soonest first; the rest stay newest first, as the server sends them.
    out.upcoming.sort((a, b) => a.startDate.localeCompare(b.startDate));
    return out;
  }, [items, today]);

  const typeName = (r: MyLeaveRequest) => {
    const type = types.find((x) => x.id === r.leaveTypeId);
    return type ? text.name(type.nameEn, type.nameAr) : r.leaveTypeName;
  };

  const waitingFor = (a: NextApprover | null | undefined) => {
    if (!a) return null;
    const who = a.kind === 'named' ? a.name : a.kind === 'manager'
      ? t('leavePay.leave.list.waitingForManager')
      : a.kind === 'hr'
        ? t('leavePay.leave.list.waitingForHr')
        : t('leavePay.leave.list.waitingForUnknown');
    return t('leavePay.leave.list.waitingFor', { who });
  };

  const openAction = (request: MyLeaveRequest, action: LeaveAction) => {
    setTarget({ request, action });
    setNote('');
    setActionError(null);
  };

  const confirmAction = async () => {
    if (!target) return;
    setActing(true);
    setActionError(null);
    try {
      if (target.action === 'withdraw') await leaveRequestsApi.withdraw(target.request.id, note);
      else await leaveRequestsApi.requestCancellation(target.request.id, note);
      haptics.success();
      setNotice(target.action === 'withdraw' ? t('leavePay.leave.actions.withdrawn') : t('leavePay.leave.actions.cancelSent'));
      setTarget(null);
      void load(true);
    } catch (e) {
      haptics.error();
      setActionError(text.error(e));
    } finally {
      setActing(false);
    }
  };

  const body = () => {
    if (loading) {
      return (
        <View style={styles.list}>
          <SkeletonCard lines={2} />
          <SkeletonCard lines={2} />
          <SkeletonCard lines={2} />
        </View>
      );
    }
    if (error) {
      return <StateCard variant="error" body={error} onRetry={() => void load()} />;
    }
    if (items.length === 0) {
      return (
        <StateCard
          variant="empty"
          title={t('leavePay.leave.list.emptyTitle')}
          body={t('leavePay.leave.list.emptyBody')}
          action={{ label: t('leavePay.leave.list.emptyAction'), onPress: onRequestNew }}
        />
      );
    }
    return GROUPS.filter((g) => grouped[g].length > 0).map((g) => (
      <View key={g} style={styles.section}>
        <SectionHeader title={t(`leavePay.leave.list.${g}`)} />
        <View style={styles.list}>
          {grouped[g].map((r) => (
            <RequestCard
              key={r.id}
              request={r}
              typeName={typeName(r)}
              waitingFor={r.kind === 'pending' ? waitingFor(approvers[r.id]) : null}
              action={leaveActionFor(r.kind, r.startDate, today)}
              onAction={(action) => openAction(r, action)}
            />
          ))}
        </View>
      </View>
    ));
  };

  return (
    <>
      <ScrollView
        contentContainerStyle={styles.content}
        refreshControl={<RefreshControl refreshing={refreshing} onRefresh={() => void load(true)} tintColor={theme.colors.accent} />}
        showsVerticalScrollIndicator={false}
      >
        {sent ? (
          <Notice
            testID="leave-sent"
            tone="success"
            title={t('leavePay.leave.sent.title')}
            body={
              sent.request.isHalfDay
                ? t('leavePay.leave.sent.bodyHalf', { type: sent.typeName, dates: f.date(sent.request.startDate, 'medium') })
                : t('leavePay.leave.sent.body', {
                    type: sent.typeName,
                    dates: text.dateRange(sent.request.startDate, sent.request.endDate),
                    days: text.days(sent.request.totalDays),
                  })
            }
            onDismiss={onDismissSent}
          />
        ) : null}
        {notice ? <Notice tone="success" body={notice} onDismiss={() => setNotice(null)} /> : null}
        {body()}
      </ScrollView>

      <BottomSheet
        visible={Boolean(target)}
        onClose={() => (acting ? undefined : setTarget(null))}
        title={target?.action === 'withdraw' ? t('leavePay.leave.actions.withdrawTitle') : t('leavePay.leave.actions.cancelTitle')}
        subtitle={target ? `${typeName(target.request)} · ${text.dateRange(target.request.startDate, target.request.endDate)}` : undefined}
        actions={
          <>
            <LiquidButton
              testID="leave-action-confirm"
              variant="danger"
              label={target?.action === 'withdraw' ? t('leavePay.leave.actions.confirmWithdraw') : t('leavePay.leave.actions.confirmCancel')}
              onPress={() => void confirmAction()}
              loading={acting}
            />
            <LiquidButton variant="secondary" label={t('leavePay.leave.actions.keep')} onPress={() => setTarget(null)} disabled={acting} />
          </>
        }
      >
        <Text style={[theme.typography.subheadline, { color: theme.colors.textSecondary }]}>
          {target?.action === 'withdraw' ? t('leavePay.leave.actions.withdrawBody') : t('leavePay.leave.actions.cancelBody')}
        </Text>
        <TextArea
          label={t('leavePay.leave.actions.noteLabel')}
          optional
          value={note}
          onChangeText={setNote}
          placeholder={t('leavePay.leave.actions.notePlaceholder')}
        />
        {actionError ? <Notice tone="danger" body={actionError} /> : null}
      </BottomSheet>
    </>
  );
}

function RequestCard({
  request,
  typeName,
  waitingFor,
  action,
  onAction,
}: {
  request: MyLeaveRequest;
  typeName: string;
  waitingFor: string | null;
  action: LeaveAction | null;
  onAction: (action: LeaveAction) => void;
}) {
  const { theme } = useTheme();
  const { t } = useTranslation();
  const f = useFormat();
  const text = useLeavePayText();
  const status = t(`leavePay.leave.status.${request.kind}`);
  const dates = text.dateRange(request.startDate, request.endDate);
  const decided = request.decidedAt && (request.kind === 'approved' || request.kind === 'rejected');

  return (
    <Surface level={1} contentStyle={styles.card} testID={`leave-request-${request.id}`}>
      <View accessible accessibilityLabel={t('leavePay.leave.list.a11yRow', { type: typeName, dates, status })} style={styles.cardBody}>
        <View style={styles.cardHead}>
          <Text style={[theme.typography.headline, styles.flex, { color: theme.colors.text }]}>{typeName}</Text>
          <StatusPill tone={leaveStatusTone(request.kind)} label={status} />
        </View>
        <Text style={[theme.typography.subheadline, { color: theme.colors.text }]}>{dates}</Text>
        {request.totalDays > 0 ? (
          <Text style={[theme.typography.footnote, { color: theme.colors.textSecondary }]}>
            {t('leavePay.leave.list.counted', { days: text.days(request.totalDays) })}
          </Text>
        ) : null}
        {waitingFor ? (
          <Text style={[theme.typography.footnote, { color: theme.colors.textSecondary }]}>{waitingFor}</Text>
        ) : null}
        {decided ? (
          <Text style={[theme.typography.footnote, { color: theme.colors.textMuted }]}>
            {t('leavePay.leave.list.decidedOn', { date: f.date(request.decidedAt, 'medium') })}
          </Text>
        ) : null}
        {request.kind === 'rejected' && request.rejectionReason ? (
          <Text style={[theme.typography.footnote, { color: theme.colors.textSecondary }]}>
            {t('leavePay.leave.list.rejectionReason', { reason: request.rejectionReason })}
          </Text>
        ) : null}
        {(request.kind === 'cancelled' || request.kind === 'withdrawn') && request.cancellationReason ? (
          <Text style={[theme.typography.footnote, { color: theme.colors.textSecondary }]}>
            {t('leavePay.leave.list.cancellationReason', { reason: request.cancellationReason })}
          </Text>
        ) : null}
      </View>
      {action ? (
        <LiquidButton
          testID={`leave-${action}-${request.id}`}
          variant="secondary"
          size="compact"
          label={action === 'withdraw' ? t('leavePay.leave.actions.withdraw') : t('leavePay.leave.actions.requestCancel')}
          onPress={() => onAction(action)}
          style={styles.cardAction}
        />
      ) : null}
    </Surface>
  );
}

const styles = StyleSheet.create({
  content: { paddingHorizontal: 16, paddingTop: 4, paddingBottom: 28, gap: 14 },
  section: { gap: 2 },
  list: { gap: 10 },
  card: { padding: 14, gap: 12 },
  cardBody: { gap: 4 },
  cardHead: { flexDirection: 'row', alignItems: 'flex-start', gap: 10, marginBottom: 2 },
  cardAction: { alignSelf: 'flex-start' },
  flex: { flex: 1 },
});
