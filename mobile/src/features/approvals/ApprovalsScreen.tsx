import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { FlatList, RefreshControl, ScrollView, StyleSheet, Text, TextInput, View } from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import { useFocusEffect } from '@react-navigation/native';
import { useTranslation } from 'react-i18next';
import { approvalsApi, type EmployeeChangeField } from '@/api/services';
import { useAuthStore } from '@/auth/authStore';
import { hasEffectivePermission } from '@/auth/accessPolicy';
import { useTheme } from '@/theme/ThemeProvider';
import { useFormat } from '@/utils/format';
import { haptics } from '@/utils/haptics';
import {
  BottomSheet,
  LiquidBackdrop,
  LiquidButton,
  MotionPressable,
  ScreenHero,
  SegmentedControl,
  SkeletonCard,
  StateCard,
  StatusPill,
  Surface,
} from '@/components/ui';
import {
  summarizeInbox,
  type ApprovalKind,
  type BalanceFacts,
  type DecisionOutcome,
  type InboxItem,
  type LeaveFacts,
  type MyDecision,
} from './approvalModel';
import type { OutEntry } from './teamModel';
import { KIND_ICON, useManagerText } from './useManagerText';

interface Props {
  navigation: any;
  route?: { params?: { filterType?: ApprovalKind } };
}

type Tab = 'waiting' | 'decided';
type Feedback = { tone: 'success' | 'danger' | 'info'; text: string };
type Translate = (key: string, options?: Record<string, unknown>) => string;

/** Decision errors in plain words. Server wording is English-only, so Arabic gets the translated generic line. */
function decisionErrorText(error: any, t: Translate, language: string): { text: string; refresh: boolean } {
  const status = error?.response?.status;
  if (status === 409) return { text: t('manager.approvals.errors.conflict'), refresh: true };
  if (status === 403) return { text: t('manager.approvals.errors.forbidden'), refresh: false };
  if (!error?.response) return { text: t('manager.approvals.errors.network'), refresh: false };
  const message = error?.response?.data?.message;
  if (typeof message === 'string' && message.trim() && !language.startsWith('ar')) {
    return { text: t('manager.approvals.errors.server', { message: message.trim() }), refresh: false };
  }
  return { text: t('manager.approvals.errors.generic'), refresh: false };
}

export default function ApprovalsScreen({ route }: Props) {
  const { theme } = useTheme();
  const { t, i18n } = useTranslation();
  const text = useManagerText();
  const user = useAuthStore((state) => state.user);
  const auditor = user?.accessMode === 'ReadOnlyAuditor';
  const canDecideAny = !auditor && hasEffectivePermission(user, 'approvals.decide');

  const [tab, setTab] = useState<Tab>('waiting');
  const [items, setItems] = useState<InboxItem[]>([]);
  const [unavailable, setUnavailable] = useState<('workflow' | 'overtime')[]>([]);
  const [leaveFacts, setLeaveFacts] = useState<Record<string, LeaveFacts | null>>({});
  const [loading, setLoading] = useState(true);
  const [loadFailed, setLoadFailed] = useState(false);
  const [refreshing, setRefreshing] = useState(false);
  const [filter, setFilter] = useState<ApprovalKind | 'ALL'>(route?.params?.filterType ?? 'ALL');
  const [selected, setSelected] = useState<InboxItem | null>(null);
  const [feedback, setFeedback] = useState<Feedback | null>(null);
  const loadedOnce = useRef(false);

  const [history, setHistory] = useState<MyDecision[] | null>(null);
  const [historyFailed, setHistoryFailed] = useState(false);
  const [historyLoading, setHistoryLoading] = useState(false);

  useEffect(() => {
    if (route?.params?.filterType) {
      setFilter(route.params.filterType);
      setTab('waiting');
    }
  }, [route?.params?.filterType]);

  const loadInbox = useCallback(async (silent = false) => {
    if (!silent) setLoading(true);
    try {
      const inbox = await approvalsApi.getInbox();
      setItems(inbox.items);
      setUnavailable(inbox.unavailable);
      setLoadFailed(false);
      loadedOnce.current = true;
      // Dates and days for leave cards (real request data; a card without them simply omits the line).
      const leaveIds = inbox.items
        .filter((i) => i.kind === 'LEAVE' && i.entityId)
        .map((i) => i.entityId as string)
        .slice(0, 25);
      const settled = await Promise.allSettled(leaveIds.map((id) => approvalsApi.getLeaveFacts(id)));
      setLeaveFacts((current) => {
        const next = { ...current };
        settled.forEach((r, i) => {
          if (r.status === 'fulfilled') next[leaveIds[i]] = r.value;
        });
        return next;
      });
    } catch {
      setLoadFailed(true);
    } finally {
      setLoading(false);
      setRefreshing(false);
    }
  }, []);

  const userId = user?.id;
  const loadHistory = useCallback(async () => {
    if (!userId) return;
    setHistoryLoading(true);
    try {
      setHistory(await approvalsApi.getMyDecisions(userId));
      setHistoryFailed(false);
    } catch {
      setHistoryFailed(true);
    } finally {
      setHistoryLoading(false);
      setRefreshing(false);
    }
  }, [userId]);

  // Fresh on every visit: a decision made elsewhere (web, another approver) must not linger here.
  useFocusEffect(
    useCallback(() => {
      void loadInbox(loadedOnce.current);
    }, [loadInbox]),
  );

  useEffect(() => {
    if (tab === 'decided' && history === null && !historyLoading && !historyFailed) void loadHistory();
  }, [history, historyFailed, historyLoading, loadHistory, tab]);

  const summary = useMemo(() => summarizeInbox(items), [items]);
  const kinds = useMemo(
    () => (Object.keys(summary.byKind) as ApprovalKind[]).sort((a, b) => (summary.byKind[b] ?? 0) - (summary.byKind[a] ?? 0)),
    [summary.byKind],
  );
  const visible = filter === 'ALL' ? items : items.filter((i) => i.kind === filter);

  const onDecided = useCallback((item: InboxItem, outcome: DecisionOutcome) => {
    haptics.success();
    setSelected(null);
    setItems((current) => current.filter((i) => i.key !== item.key));
    setFeedback({ tone: outcome.kind === 'rejected' ? 'info' : 'success', text: text.outcome(outcome, item) });
    setHistory(null); // the decided list changed
    setHistoryFailed(false);
    void loadInbox(true);
  }, [loadInbox, text]);

  const onConflict = useCallback((message: string) => {
    setSelected(null);
    setFeedback({ tone: 'danger', text: message });
    void loadInbox(true);
  }, [loadInbox]);

  const refresh = () => {
    setRefreshing(true);
    if (tab === 'waiting') void loadInbox(true);
    else void loadHistory();
  };

  const header = (
    <>
      <ScreenHero
        title={t('manager.approvals.title')}
        subtitle={
          loading && !items.length
            ? undefined
            : summary.total
              ? t('manager.approvals.subtitleWaiting', { n: summary.total })
              : t('manager.approvals.subtitleClear')
        }
      />
      <View style={styles.section}>
        <SegmentedControl<Tab>
          segments={[
            { key: 'waiting', label: t('manager.approvals.tabWaiting', { n: summary.total }) },
            { key: 'decided', label: t('manager.approvals.tabDecided') },
          ]}
          value={tab}
          onChange={setTab}
        />
      </View>

      {!canDecideAny ? (
        <Surface style={styles.section} level={0} contentStyle={styles.notice}>
          <Ionicons accessible={false} name="eye-outline" size={20} color={theme.colors.textSecondary} />
          <View style={styles.flex}>
            <Text style={[theme.typography.headline, { color: theme.colors.text }]}>{t('manager.approvals.readOnlyTitle')}</Text>
            <Text style={[theme.typography.footnote, { color: theme.colors.textSecondary }]}>
              {auditor ? t('manager.approvals.readOnlyAudit') : t('manager.approvals.readOnlyRole')}
            </Text>
          </View>
        </Surface>
      ) : null}

      {feedback ? (
        <View style={styles.section}>
          <FeedbackBanner feedback={feedback} onDismiss={() => setFeedback(null)} />
        </View>
      ) : null}

      {tab === 'waiting' && unavailable.length ? (
        <View style={styles.section}>
          {unavailable.map((source) => (
            <Text
              key={source}
              accessibilityLiveRegion="polite"
              style={[theme.typography.footnote, styles.partial, { color: theme.colors.status.warning.fg }]}
            >
              {source === 'overtime' ? t('manager.approvals.partialOvertime') : t('manager.approvals.partialWorkflow')}
            </Text>
          ))}
        </View>
      ) : null}

      {tab === 'waiting' && kinds.length > 1 ? (
        <ScrollView horizontal showsHorizontalScrollIndicator={false} contentContainerStyle={styles.chips} style={styles.chipRail}>
          {(['ALL', ...kinds] as (ApprovalKind | 'ALL')[]).map((k) => {
            const on = filter === k;
            const label = k === 'ALL' ? t('manager.approvals.filterAll') : text.kind(k);
            const count = k === 'ALL' ? summary.total : summary.byKind[k] ?? 0;
            return (
              <MotionPressable
                key={k}
                haptic="selection"
                accessibilityRole="button"
                accessibilityState={{ selected: on }}
                onPress={() => setFilter(k)}
                contentStyle={[
                  styles.chip,
                  {
                    backgroundColor: on ? theme.colors.primaryFill : theme.colors.card,
                    borderColor: on ? theme.colors.primaryFill : theme.colors.hairline,
                  },
                ]}
              >
                <Text style={[theme.typography.footnote, styles.chipText, { color: on ? theme.colors.onFill : theme.colors.textSecondary }]}>
                  {t('manager.approvals.filterCount', { label, n: count })}
                </Text>
              </MotionPressable>
            );
          })}
        </ScrollView>
      ) : null}
    </>
  );

  if (tab === 'decided') {
    return (
      <View style={[styles.root, { backgroundColor: theme.colors.canvas }]}>
        <LiquidBackdrop subtle />
        <FlatList
          data={history ?? []}
          keyExtractor={(d) => d.key}
          renderItem={({ item }) => <HistoryRow decision={item} />}
          ItemSeparatorComponent={Gap}
          ListHeaderComponent={header}
          ListEmptyComponent={
            <View style={styles.section}>
              {historyFailed ? (
                <StateCard
                  variant="error"
                  title={t('manager.approvals.history.errorTitle')}
                  onRetry={() => {
                    setHistoryFailed(false);
                    void loadHistory();
                  }}
                />
              ) : historyLoading || history === null ? (
                <View style={styles.stack}>
                  <SkeletonCard lines={2} />
                  <SkeletonCard lines={2} />
                </View>
              ) : (
                <StateCard variant="empty" title={t('manager.approvals.history.emptyTitle')} body={t('manager.approvals.history.emptyBody')} />
              )}
            </View>
          }
          ListFooterComponent={
            <Text style={[theme.typography.footnote, styles.footnote, { color: theme.colors.textMuted }]}>
              {t('manager.approvals.history.overtimeNote')}
            </Text>
          }
          contentContainerStyle={styles.content}
          refreshControl={<RefreshControl refreshing={refreshing} onRefresh={refresh} tintColor={theme.colors.accent} />}
          showsVerticalScrollIndicator={false}
        />
      </View>
    );
  }

  return (
    <View style={[styles.root, { backgroundColor: theme.colors.canvas }]}>
      <LiquidBackdrop subtle />
      <FlatList
        data={loading && !items.length ? [] : visible}
        keyExtractor={(i) => i.key}
        renderItem={({ item }) => (
          <ApprovalCard
            item={item}
            leave={item.entityId ? leaveFacts[item.entityId] ?? null : null}
            canDecide={canDecideAny && item.canDecide}
            onOpen={() => setSelected(item)}
          />
        )}
        ItemSeparatorComponent={Gap}
        ListHeaderComponent={header}
        ListEmptyComponent={
          <View style={styles.section}>
            {loading ? (
              <View style={styles.stack}>
                <SkeletonCard lines={3} />
                <SkeletonCard lines={3} />
              </View>
            ) : loadFailed ? (
              <StateCard variant="error" title={t('manager.approvals.loadErrorTitle')} onRetry={() => void loadInbox()} />
            ) : items.length && filter !== 'ALL' ? (
              <StateCard
                variant="empty"
                title={t('manager.approvals.filteredEmpty')}
                body={t('manager.approvals.emptyBody')}
                action={{ label: t('manager.approvals.showAll'), onPress: () => setFilter('ALL') }}
              />
            ) : (
              <StateCard variant="empty" title={t('manager.approvals.emptyTitle')} body={t('manager.approvals.emptyBody')} />
            )}
          </View>
        }
        contentContainerStyle={styles.content}
        refreshControl={<RefreshControl refreshing={refreshing} onRefresh={refresh} tintColor={theme.colors.accent} />}
        showsVerticalScrollIndicator={false}
      />
      <DecisionSheet
        item={selected}
        leave={selected?.entityId ? leaveFacts[selected.entityId] ?? null : null}
        canDecide={Boolean(selected && canDecideAny && selected.canDecide)}
        language={i18n.language}
        onClose={() => setSelected(null)}
        onDecided={onDecided}
        onConflict={onConflict}
      />
    </View>
  );
}

function Gap() {
  return <View style={styles.gap} />;
}

function FeedbackBanner({ feedback, onDismiss }: { feedback: Feedback; onDismiss: () => void }) {
  const { theme } = useTheme();
  const { t } = useTranslation();
  const tone = theme.colors.status[feedback.tone];
  return (
    <Surface tintColor={tone.tint} contentStyle={styles.banner}>
      <Ionicons
        accessible={false}
        name={feedback.tone === 'danger' ? 'alert-circle' : feedback.tone === 'info' ? 'information-circle' : 'checkmark-circle'}
        size={22}
        color={tone.fg}
      />
      <Text
        accessibilityLiveRegion="polite"
        accessibilityRole="alert"
        style={[theme.typography.subheadline, styles.flex, { color: theme.colors.text }]}
      >
        {feedback.text}
      </Text>
      <MotionPressable
        accessibilityRole="button"
        accessibilityLabel={t('manager.approvals.outcome.dismiss')}
        onPress={onDismiss}
        hitSlop={8}
        contentStyle={styles.iconButton}
      >
        <Ionicons accessible={false} name="close" size={18} color={theme.colors.textMuted} />
      </MotionPressable>
    </Surface>
  );
}

/** The one line that says what is being decided: dates/days for leave, date/time/hours for overtime. */
function useWhatLine(item: InboxItem, leave: LeaveFacts | null): string | null {
  const f = useFormat();
  const text = useManagerText();
  const { t } = useTranslation();
  if (item.overtime) {
    const parts = [
      item.overtime.workDate ? f.date(item.overtime.workDate, 'medium') : null,
      item.overtime.startUtc && item.overtime.endUtc
        ? t('manager.range', { from: f.time(item.overtime.startUtc), to: f.time(item.overtime.endUtc) })
        : null,
      item.overtime.minutes !== null ? text.duration(item.overtime.minutes) : null,
    ].filter(Boolean);
    return parts.length ? parts.join(' · ') : null;
  }
  if (leave?.startDate) {
    return [text.dateRange(leave.startDate, leave.endDate), leave.totalDays !== null ? text.days(leave.totalDays) : null]
      .filter(Boolean)
      .join(' · ');
  }
  return item.changeSummary;
}

function ApprovalCard({
  item,
  leave,
  canDecide,
  onOpen,
}: {
  item: InboxItem;
  leave: LeaveFacts | null;
  canDecide: boolean;
  onOpen: () => void;
}) {
  const { theme } = useTheme();
  const { t } = useTranslation();
  const f = useFormat();
  const text = useManagerText();
  const what = useWhatLine(item, leave);
  const kindLabel = item.subject ?? text.kind(item.kind);
  const meta = [
    item.submittedAt ? t('manager.approvals.card.submitted', { when: f.relative(item.submittedAt) }) : null,
    item.stepOrder !== null ? t('manager.approvals.card.step', { step: f.number(item.stepOrder) }) : null,
  ].filter(Boolean).join(' · ');
  const reason = item.overtime?.reason ?? leave?.reason ?? null;
  const cta = canDecide ? t('manager.approvals.card.review') : t('manager.approvals.card.view');

  return (
    <View style={styles.cardShell}>
      <MotionPressable
        accessibilityRole="button"
        accessibilityLabel={[kindLabel, text.requester(item), what, meta].filter(Boolean).join('. ')}
        accessibilityHint={cta}
        onPress={onOpen}
      >
        <Surface level={1} contentStyle={styles.card}>
          <View style={styles.cardTop}>
            <View style={[styles.kindIcon, { backgroundColor: theme.colors.surfaceInset }]}>
              <Ionicons accessible={false} name={KIND_ICON[item.kind]} size={18} color={theme.colors.accent} />
            </View>
            <Text numberOfLines={1} style={[theme.typography.footnote, styles.flex, { color: theme.colors.textSecondary }]}>
              {kindLabel}
            </Text>
            {item.isOverdue ? (
              <StatusPill
                tone="danger"
                icon="alert-circle"
                label={item.dueAt
                  ? t('manager.approvals.card.overdue', { date: f.date(item.dueAt, 'dayMonth') })
                  : t('manager.approvals.card.overdueNoDate')}
              />
            ) : item.dueAt ? (
              <StatusPill tone="neutral" label={t('manager.approvals.card.due', { date: f.date(item.dueAt, 'dayMonth') })} />
            ) : null}
          </View>
          <Text style={[theme.typography.title3, { color: theme.colors.text }]}>{text.requester(item)}</Text>
          {what ? <Text style={[theme.typography.callout, { color: theme.colors.text }]}>{what}</Text> : null}
          {reason ? (
            <Text numberOfLines={2} style={[theme.typography.subheadline, { color: theme.colors.textSecondary }]}>
              {reason}
            </Text>
          ) : null}
          {meta ? <Text style={[theme.typography.footnote, { color: theme.colors.textMuted }]}>{meta}</Text> : null}
          {!item.canDecide ? (
            <Text style={[theme.typography.footnote, { color: theme.colors.status.warning.fg }]}>{t('manager.approvals.card.blocked')}</Text>
          ) : null}
          <View style={[styles.cardAction, { borderTopColor: theme.colors.hairline }]}>
            <Text style={[theme.typography.headline, styles.flex, { color: theme.colors.accent }]}>{cta}</Text>
            <Ionicons accessible={false} name="chevron-up" size={18} color={theme.colors.accent} />
          </View>
        </Surface>
      </MotionPressable>
    </View>
  );
}

function FactRow({ label, value, sub }: { label: string; value: string; sub?: string | null }) {
  const { theme } = useTheme();
  return (
    <View accessible style={[styles.fact, { borderBottomColor: theme.colors.hairline }]}>
      <Text style={[theme.typography.footnote, { color: theme.colors.textSecondary }]}>{label}</Text>
      <Text style={[theme.typography.callout, { color: theme.colors.text }]}>{value}</Text>
      {sub ? <Text style={[theme.typography.footnote, { color: theme.colors.textMuted }]}>{sub}</Text> : null}
    </View>
  );
}

type LeaveContext = {
  leave: LeaveFacts;
  balance: BalanceFacts | null;
  balanceUnavailable: boolean;
  overlap: OutEntry[] | null;
};

function DecisionSheet({
  item,
  leave: leaveFromCard,
  canDecide,
  language,
  onClose,
  onDecided,
  onConflict,
}: {
  item: InboxItem | null;
  leave: LeaveFacts | null;
  canDecide: boolean;
  language: string;
  onClose: () => void;
  onDecided: (item: InboxItem, outcome: DecisionOutcome) => void;
  onConflict: (message: string) => void;
}) {
  const { theme } = useTheme();
  const { t } = useTranslation();
  const tx = t as unknown as Translate;
  const f = useFormat();
  const text = useManagerText();
  const inputRef = useRef<TextInput>(null);
  const [comment, setComment] = useState('');
  const [busy, setBusy] = useState<'approve' | 'reject' | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [reasonMissing, setReasonMissing] = useState(false);
  const [context, setContext] = useState<LeaveContext | null>(null);
  const [contextState, setContextState] = useState<'idle' | 'loading' | 'failed'>('idle');
  // Before → after for an employee change. Only fetched when the caller can decide it: the server answers 404 to
  // anyone else, so a non-decider keeps the field names only.
  const [changeFields, setChangeFields] = useState<EmployeeChangeField[] | null>(null);
  const [changeState, setChangeState] = useState<'idle' | 'loading' | 'failed'>('idle');

  const loadChangeDetail = useCallback(async (target: InboxItem) => {
    if (target.kind !== 'EMPLOYEE_CHANGE' || target.source !== 'workflow' || !target.canDecide) return;
    setChangeState('loading');
    try {
      setChangeFields(await approvalsApi.getEmployeeChangeDetail(target.id));
      setChangeState('idle');
    } catch {
      setChangeState('failed');
    }
  }, []);

  const loadContext = useCallback(async (target: InboxItem) => {
    if (target.kind !== 'LEAVE' || !target.entityId) return;
    setContextState('loading');
    try {
      setContext(await approvalsApi.getLeaveContext(target.entityId));
      setContextState('idle');
    } catch {
      setContextState('failed');
    }
  }, []);

  useEffect(() => {
    setComment('');
    setError(null);
    setReasonMissing(false);
    setBusy(null);
    setContext(null);
    setContextState('idle');
    setChangeFields(null);
    setChangeState('idle');
    if (item) {
      void loadContext(item);
      void loadChangeDetail(item);
    }
  }, [item, loadContext, loadChangeDetail]);

  const decide = async (decision: 'approve' | 'reject') => {
    if (!item || busy) return;
    if (decision === 'reject' && !comment.trim()) {
      setReasonMissing(true);
      haptics.error();
      inputRef.current?.focus();
      return;
    }
    setBusy(decision);
    setError(null);
    try {
      const outcome = await approvalsApi.decide(item, decision, comment);
      onDecided(item, outcome);
    } catch (e: any) {
      haptics.error();
      const mapped = decisionErrorText(e, tx, language);
      if (mapped.refresh) onConflict(mapped.text);
      else setError(mapped.text);
    } finally {
      setBusy(null);
    }
  };

  const leave = context?.leave ?? leaveFromCard;
  const name = item ? text.requester(item) : '';
  const verb = (v: string) => tx(`manager.approvals.verb.${v}`);

  const balanceValue = (c: LeaveContext): string => {
    if (c.balance) {
      return c.balance.afterApproval !== null
        ? t('manager.approvals.sheet.balanceAfter', { days: text.days(c.balance.afterApproval) })
        : t('manager.approvals.sheet.balanceNow', { days: text.days(c.balance.available) });
    }
    return c.balanceUnavailable ? t('manager.approvals.sheet.balanceUnknown') : t('manager.approvals.sheet.balanceNone');
  };

  return (
    <BottomSheet
      visible={item !== null}
      onClose={onClose}
      title={name}
      subtitle={item ? item.subject ?? text.kind(item.kind) : undefined}
      actions={
        item ? (
          canDecide ? (
            <>
              {error ? (
                <Text
                  accessibilityLiveRegion="assertive"
                  accessibilityRole="alert"
                  style={[theme.typography.footnote, { color: theme.colors.status.danger.fg }]}
                >
                  {error}
                </Text>
              ) : null}
              <View style={styles.actionRow}>
                <LiquidButton
                  label={t('manager.approvals.sheet.reject')}
                  icon="close"
                  variant="secondary"
                  onPress={() => void decide('reject')}
                  loading={busy === 'reject'}
                  disabled={busy !== null}
                  style={styles.flex}
                />
                <LiquidButton
                  label={t('manager.approvals.sheet.approve')}
                  icon="checkmark"
                  variant="success"
                  onPress={() => void decide('approve')}
                  loading={busy === 'approve'}
                  disabled={busy !== null}
                  style={styles.flex}
                />
              </View>
            </>
          ) : (
            <Text style={[theme.typography.subheadline, { color: theme.colors.textSecondary }]}>
              {item.blockedReason && !language.startsWith('ar') ? item.blockedReason : t('manager.approvals.sheet.blockedBody')}
            </Text>
          )
        ) : undefined
      }
    >
      {item ? (
        <>
          {item.isOverdue ? (
            <StatusPill
              tone="danger"
              icon="alert-circle"
              label={item.dueAt
                ? t('manager.approvals.card.overdue', { date: f.date(item.dueAt, 'medium') })
                : t('manager.approvals.card.overdueNoDate')}
            />
          ) : null}

          {item.overtime ? (
            <View>
              {item.overtime.workDate ? (
                <FactRow
                  label={t('manager.approvals.sheet.date')}
                  value={f.date(item.overtime.workDate, 'full')}
                  sub={f.hijri(item.overtime.workDate) || null}
                />
              ) : null}
              {item.overtime.startUtc && item.overtime.endUtc ? (
                <FactRow
                  label={t('manager.approvals.sheet.time')}
                  value={t('manager.range', { from: f.time(item.overtime.startUtc), to: f.time(item.overtime.endUtc) })}
                />
              ) : null}
              {item.overtime.minutes !== null ? (
                <FactRow label={t('manager.approvals.sheet.hours')} value={text.duration(item.overtime.minutes)} />
              ) : null}
              {item.overtime.reason ? <FactRow label={t('manager.approvals.sheet.reason')} value={item.overtime.reason} /> : null}
              <FactRow label={t('manager.approvals.sheet.pay')} value={t('manager.approvals.sheet.payNote')} />
              <FactRow label={t('manager.approvals.sheet.flow')} value={t('manager.approvals.sheet.flowOvertime')} />
            </View>
          ) : null}

          {item.kind === 'LEAVE' ? (
            contextState === 'loading' && !leave ? (
              <SkeletonCard lines={4} />
            ) : (
              <View>
                {leave?.leaveTypeName ? (
                  <FactRow
                    label={t('manager.approvals.sheet.type')}
                    value={leave.leaveTypeName}
                    sub={leave.isEmergency ? t('manager.approvals.sheet.emergency') : null}
                  />
                ) : null}
                {leave?.startDate ? (
                  <FactRow
                    label={t('manager.approvals.sheet.dates')}
                    value={text.dateRange(leave.startDate, leave.endDate)}
                    sub={f.hijri(leave.startDate) || null}
                  />
                ) : null}
                {leave && leave.totalDays !== null ? (
                  <FactRow
                    label={t('manager.approvals.sheet.duration')}
                    value={text.days(leave.totalDays)}
                    sub={leave.dayType
                      ? /half/i.test(leave.dayType) ? t('manager.approvals.sheet.dayHalf') : t('manager.approvals.sheet.dayFull')
                      : null}
                  />
                ) : null}
                {leave?.reason ? <FactRow label={t('manager.approvals.sheet.reason')} value={leave.reason} /> : null}
                {context ? (
                  <FactRow
                    label={t('manager.approvals.sheet.balance')}
                    value={balanceValue(context)}
                    sub={context.balance && context.balance.afterApproval !== null ? t('manager.approvals.sheet.balanceNote') : null}
                  />
                ) : null}
                {context ? (
                  <View accessible style={[styles.fact, { borderBottomColor: theme.colors.hairline }]}>
                    <Text style={[theme.typography.footnote, { color: theme.colors.textSecondary }]}>{t('manager.approvals.sheet.overlap')}</Text>
                    {context.overlap === null ? (
                      <Text style={[theme.typography.callout, { color: theme.colors.textMuted }]}>{t('manager.approvals.sheet.overlapUnknown')}</Text>
                    ) : context.overlap.length === 0 ? (
                      <Text style={[theme.typography.callout, { color: theme.colors.text }]}>{t('manager.approvals.sheet.overlapNone')}</Text>
                    ) : (
                      context.overlap.map((o) => (
                        <Text key={`${o.employeeId}-${o.startDate}`} style={[theme.typography.callout, { color: theme.colors.status.warning.fg }]}>
                          {t('manager.approvals.sheet.overlapEntry', { name: o.employeeName, dates: text.dateRange(o.startDate, o.endDate) })}
                          {o.approved ? '' : ` (${t('manager.approvals.sheet.notApprovedYet')})`}
                        </Text>
                      ))
                    )}
                  </View>
                ) : null}
                {contextState === 'failed' ? (
                  <View style={styles.inlineRetry}>
                    <Text style={[theme.typography.footnote, styles.flex, { color: theme.colors.status.warning.fg }]}>
                      {t('manager.approvals.sheet.contextError')}
                    </Text>
                    <LiquidButton label={t('kit.retry')} variant="secondary" size="compact" onPress={() => void loadContext(item)} />
                  </View>
                ) : null}
              </View>
            )
          ) : null}

          {!item.overtime && item.kind !== 'LEAVE' && item.changeSummary && !changeFields?.length ? (
            <FactRow label={t('manager.approvals.sheet.changes')} value={item.changeSummary} />
          ) : null}
          {changeFields?.length ? (
            <View>
              {changeFields.map((field) => (
                <FactRow
                  key={field.key}
                  label={field.label}
                  value={t('manager.approvals.sheet.changeProposed', { value: field.proposedValue ?? t('manager.approvals.sheet.changeNotSet') })}
                  sub={t('manager.approvals.sheet.changeNow', { value: field.currentValue ?? t('manager.approvals.sheet.changeNotSet') })}
                />
              ))}
              {changeFields.some((field) => field.masked) ? (
                <Text style={[theme.typography.footnote, { color: theme.colors.textMuted }]}>{t('manager.approvals.sheet.changeMaskedNote')}</Text>
              ) : null}
            </View>
          ) : null}
          {changeState === 'failed' ? (
            <View style={styles.inlineRetry}>
              <Text style={[theme.typography.footnote, styles.flex, { color: theme.colors.status.warning.fg }]}>
                {t('manager.approvals.sheet.changeError')}
              </Text>
              <LiquidButton label={t('kit.retry')} variant="secondary" size="compact" onPress={() => void loadChangeDetail(item)} />
            </View>
          ) : null}

          <View>
            {item.submittedAt ? <FactRow label={t('manager.approvals.sheet.submitted')} value={f.dateTime(item.submittedAt)} /> : null}
            {item.dueAt && !item.isOverdue ? <FactRow label={t('manager.approvals.sheet.due')} value={f.dateTime(item.dueAt)} /> : null}
            {item.source === 'workflow' ? (
              <FactRow
                label={t('manager.approvals.sheet.flow')}
                value={item.stepOrder !== null
                  ? t('manager.approvals.sheet.flowStep', { step: f.number(item.stepOrder) })
                  : t('manager.approvals.sheet.flowNoStep')}
              />
            ) : null}
            {item.priorDecisions.length ? (
              <View accessible style={[styles.fact, { borderBottomColor: theme.colors.hairline }]}>
                <Text style={[theme.typography.footnote, { color: theme.colors.textSecondary }]}>{t('manager.approvals.sheet.prior')}</Text>
                {item.priorDecisions.map((d, i) => (
                  <Text key={i} style={[theme.typography.callout, { color: theme.colors.text }]}>
                    {d.stepOrder !== null
                      ? t('manager.approvals.sheet.priorEntry', { step: f.number(d.stepOrder), decision: verb(d.decision), date: f.dateTime(d.decidedAt) })
                      : t('manager.approvals.sheet.priorEntryNoStep', { decision: verb(d.decision), date: f.dateTime(d.decidedAt) })}
                    {d.comments ? ` — ${d.comments}` : ''}
                  </Text>
                ))}
              </View>
            ) : null}
            <FactRow
              label={t('manager.approvals.sheet.source')}
              value={item.source === 'overtime' ? t('manager.approvals.source.overtime') : t('manager.approvals.source.workflow')}
            />
          </View>

          {canDecide ? (
            <View style={styles.commentBlock}>
              <Text style={[theme.typography.headline, { color: theme.colors.text }]}>{t('manager.approvals.sheet.commentLabel')}</Text>
              <Text style={[theme.typography.footnote, { color: theme.colors.textSecondary }]}>{t('manager.approvals.sheet.commentHint')}</Text>
              <TextInput
                ref={inputRef}
                value={comment}
                onChangeText={(value) => {
                  setComment(value);
                  if (value.trim()) setReasonMissing(false);
                }}
                multiline
                maxLength={1000}
                textAlignVertical="top"
                accessibilityLabel={t('manager.approvals.sheet.commentLabel')}
                accessibilityHint={t('manager.approvals.sheet.commentHint')}
                placeholder={t('manager.approvals.sheet.commentPlaceholder', { name })}
                placeholderTextColor={theme.colors.textMuted}
                selectionColor={theme.colors.accent}
                style={[
                  theme.typography.callout,
                  styles.input,
                  {
                    color: theme.colors.text,
                    backgroundColor: theme.colors.surfaceInset,
                    borderColor: reasonMissing ? theme.colors.status.danger.fg : theme.colors.hairline,
                  },
                ]}
              />
              {reasonMissing ? (
                <Text accessibilityLiveRegion="assertive" style={[theme.typography.footnote, { color: theme.colors.status.danger.fg }]}>
                  {t('manager.approvals.sheet.reasonRequired', { name })}
                </Text>
              ) : null}
            </View>
          ) : null}
        </>
      ) : null}
    </BottomSheet>
  );
}

function HistoryRow({ decision }: { decision: MyDecision }) {
  const { theme } = useTheme();
  const { t } = useTranslation();
  const f = useFormat();
  const text = useManagerText();
  const date = f.dateTime(decision.decidedAt);
  const mine = {
    approved: t('manager.approvals.history.youApproved', { date }),
    rejected: t('manager.approvals.history.youRejected', { date }),
    returned: t('manager.approvals.history.youReturned', { date }),
    other: t('manager.approvals.history.youDecided', { date }),
  }[decision.myDecision];
  const next = text.target(decision.nowWith);
  const state = decision.state === 'approved'
    ? { tone: 'success' as const, label: t('manager.approvals.history.stateApproved') }
    : decision.state === 'rejected'
      ? { tone: 'danger' as const, label: t('manager.approvals.history.stateRejected') }
      : decision.state === 'returned'
        ? { tone: 'warning' as const, label: t('manager.approvals.history.stateReturned') }
        : decision.state === 'pending'
          ? { tone: 'info' as const, label: next ? t('manager.approvals.history.statePendingWith', { next }) : t('manager.approvals.history.statePending') }
          : { tone: 'neutral' as const, label: t('manager.approvals.history.stateOther') };

  return (
    <View style={styles.cardShell}>
      <Surface contentStyle={styles.card}>
        <View style={styles.cardTop}>
          <View style={[styles.kindIcon, { backgroundColor: theme.colors.surfaceInset }]}>
            <Ionicons accessible={false} name={KIND_ICON[decision.kind]} size={18} color={theme.colors.accent} />
          </View>
          <Text numberOfLines={1} style={[theme.typography.footnote, styles.flex, { color: theme.colors.textSecondary }]}>
            {decision.subject ?? text.kind(decision.kind)}
          </Text>
          <StatusPill tone={state.tone} label={state.label} />
        </View>
        <Text style={[theme.typography.headline, { color: theme.colors.text }]}>{decision.requesterName ?? t('manager.someone')}</Text>
        <Text style={[theme.typography.subheadline, { color: theme.colors.textSecondary }]}>{mine}</Text>
        {decision.comments ? (
          <Text style={[theme.typography.subheadline, { color: theme.colors.text }]}>“{decision.comments}”</Text>
        ) : null}
      </Surface>
    </View>
  );
}

const styles = StyleSheet.create({
  root: { flex: 1 },
  flex: { flex: 1 },
  content: { paddingBottom: 24 },
  section: { paddingHorizontal: 16, marginBottom: 12 },
  stack: { gap: 10 },
  gap: { height: 10 },
  notice: { flexDirection: 'row', gap: 12, padding: 14, alignItems: 'flex-start' },
  partial: { marginBottom: 4 },
  banner: { flexDirection: 'row', alignItems: 'center', gap: 10, paddingVertical: 8, paddingStart: 14, paddingEnd: 4 },
  iconButton: { width: 44, height: 44, alignItems: 'center', justifyContent: 'center' },
  chipRail: { marginBottom: 12, flexGrow: 0 },
  chips: { paddingHorizontal: 16, gap: 8 },
  chip: { minHeight: 44, borderRadius: 22, borderWidth: StyleSheet.hairlineWidth, paddingHorizontal: 14, justifyContent: 'center' },
  chipText: { fontWeight: '600' },
  cardShell: { paddingHorizontal: 16 },
  card: { padding: 16, gap: 6 },
  cardTop: { flexDirection: 'row', alignItems: 'center', gap: 8, marginBottom: 2 },
  kindIcon: { width: 30, height: 30, borderRadius: 9, alignItems: 'center', justifyContent: 'center' },
  cardAction: {
    flexDirection: 'row',
    alignItems: 'center',
    minHeight: 44,
    marginTop: 6,
    borderTopWidth: StyleSheet.hairlineWidth,
    paddingTop: 6,
  },
  fact: { paddingVertical: 10, gap: 2, borderBottomWidth: StyleSheet.hairlineWidth },
  inlineRetry: { flexDirection: 'row', alignItems: 'center', gap: 10, paddingTop: 10 },
  commentBlock: { gap: 6, paddingTop: 4 },
  input: { minHeight: 96, borderRadius: 14, borderWidth: 1, paddingHorizontal: 12, paddingVertical: 10, textAlign: 'auto' },
  actionRow: { flexDirection: 'row', gap: 10 },
  footnote: { paddingHorizontal: 20, paddingTop: 14 },
});
