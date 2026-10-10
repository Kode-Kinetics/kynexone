import React, { useCallback, useEffect, useMemo, useState } from 'react';
import { Platform, RefreshControl, ScrollView, StyleSheet, Switch, Text, View } from 'react-native';
import { useTranslation } from 'react-i18next';
import {
  LiquidBackdrop,
  LiquidButton,
  ScreenHero,
  SegmentedControl,
  SkeletonCard,
  StateCard,
  StatusPill,
  StickyFooter,
  Surface,
} from '@/components/ui';
import { useTheme } from '@/theme/ThemeProvider';
import type { StatusTone } from '@/theme/tokens';
import { useFormat } from '@/utils/format';
import { haptics } from '@/utils/haptics';
import { overtimeApi } from '@/api/adapters';
import type { OvertimePay, OvertimePolicyInfo, OvertimeRecord } from '@/api/services';
import { PickerField } from '@/features/leave/PickerField';
import { Notice, TextArea } from '@/features/leave/LeavePayParts';
import { useLeavePayText } from '@/features/leave/useLeavePayText';
import { checkOvertime, overtimeStatusKind, type OvertimeStatusKind } from './overtimeLogic';

type Segment = 'request' | 'history';
const MIN_REASON = 3;

const ROUNDING_KEYS: Record<string, 'roundingNearest15' | 'roundingUp15' | 'roundingDown15' | 'roundingNearest30'> = {
  Nearest15: 'roundingNearest15',
  Up15: 'roundingUp15',
  Down15: 'roundingDown15',
  Nearest30: 'roundingNearest30',
};

function statusTone(kind: OvertimeStatusKind): StatusTone {
  if (kind === 'approved') return 'success';
  if (kind === 'rejected') return 'danger';
  if (kind === 'cancelled' || kind === 'unknown') return 'neutral';
  return 'warning';
}

export default function OvertimeScreen({ route }: { route?: { params?: { segment?: Segment } } }) {
  const { theme } = useTheme();
  const { t } = useTranslation();
  const requested = route?.params?.segment;
  const [segment, setSegment] = useState<Segment>(requested === 'history' ? 'history' : 'request');
  const [sentNotice, setSentNotice] = useState(false);
  const [historyKey, setHistoryKey] = useState(0);

  useEffect(() => {
    if (requested === 'history' || requested === 'request') setSegment(requested);
  }, [requested]);

  return (
    <View style={[styles.root, { backgroundColor: theme.colors.canvas }]}>
      <LiquidBackdrop subtle />
      <ScreenHero eyebrow={t('leavePay.overtime.eyebrow')} title={t('leavePay.overtime.title')}>
        <SegmentedControl
          testID="overtime-segments"
          style={styles.segments}
          value={segment}
          onChange={(next) => {
            setSegment(next);
            if (next === 'request') setSentNotice(false);
          }}
          segments={[
            { key: 'request', label: t('leavePay.overtime.segments.request') },
            { key: 'history', label: t('leavePay.overtime.segments.history') },
          ]}
        />
      </ScreenHero>
      {segment === 'request' ? (
        <OvertimeForm
          onSent={() => {
            setSentNotice(true);
            setHistoryKey((k) => k + 1);
            setSegment('history');
          }}
        />
      ) : (
        <OvertimeHistory
          reloadKey={historyKey}
          sentNotice={sentNotice}
          onDismissNotice={() => setSentNotice(false)}
          onRequestNew={() => setSegment('request')}
        />
      )}
    </View>
  );
}

function OvertimeForm({ onSent }: { onSent: () => void }) {
  const { theme } = useTheme();
  const { t } = useTranslation();
  const f = useFormat();
  const text = useLeavePayText();

  // No pre-filled times: an untouched form can never be sent as someone's real hours.
  const [date, setDate] = useState<string | null>(() => f.businessDate());
  const [start, setStart] = useState<string | null>(null);
  const [end, setEnd] = useState<string | null>(null);
  const [endsNextDay, setEndsNextDay] = useState(false);
  const [reason, setReason] = useState('');
  const [policy, setPolicy] = useState<OvertimePolicyInfo | null>(null);
  const [showErrors, setShowErrors] = useState(false);
  const [submitting, setSubmitting] = useState(false);
  const [submitError, setSubmitError] = useState<string | null>(null);

  useEffect(() => {
    let active = true;
    // Policy limits are a hint; without them only the 12-hour ceiling applies (the server still checks).
    overtimeApi
      .getPolicyLimits()
      .then((p) => active && setPolicy(p))
      .catch(() => active && setPolicy(null));
    return () => {
      active = false;
    };
  }, []);

  const check = useMemo(() => checkOvertime({ start, end, endsNextDay, policy }), [end, endsNextDay, policy, start]);
  const reasonError = reason.trim().length < MIN_REASON ? t('leavePay.overtime.form.reasonError') : null;

  const issueText = (() => {
    switch (check.issue) {
      case 'endNotAfterStart':
        return t('leavePay.overtime.issues.endNotAfterStart');
      case 'overHardCap':
        return t('leavePay.overtime.issues.overHardCap');
      case 'belowPolicyMinimum':
        return t('leavePay.overtime.issues.belowPolicyMinimum', { min: text.duration(policy?.minimumMinutes ?? 0) });
      case 'abovePolicyDailyMax':
        return t('leavePay.overtime.issues.abovePolicyDailyMax', { max: text.duration(policy?.maximumMinutesPerDay ?? 0) });
      default:
        return null;
    }
  })();

  const roundingKey = policy ? ROUNDING_KEYS[policy.roundingRule] : undefined;
  const showCounted =
    check.minutes !== null && check.roundedMinutes !== null && check.roundedMinutes !== check.minutes && roundingKey;

  const submit = async () => {
    setShowErrors(true);
    setSubmitError(null);
    if (!date || check.issue || reasonError || !start || !end) {
      haptics.error();
      return;
    }
    setSubmitting(true);
    try {
      await overtimeApi.create({ date, startTime: start, endTime: end, endsNextDay, reason: reason.trim(), timeZone: f.timeZone });
      haptics.success();
      setStart(null);
      setEnd(null);
      setEndsNextDay(false);
      setReason('');
      setShowErrors(false);
      onSent();
    } catch (error) {
      haptics.error();
      setSubmitError(text.error(error));
    } finally {
      setSubmitting(false);
    }
  };

  return (
    <>
      <ScrollView
        contentContainerStyle={styles.content}
        keyboardShouldPersistTaps="handled"
        keyboardDismissMode={Platform.OS === 'ios' ? 'interactive' : 'on-drag'}
        automaticallyAdjustKeyboardInsets
        showsVerticalScrollIndicator={false}
      >
        <PickerField
          testID="overtime-date"
          mode="date"
          label={t('leavePay.overtime.form.dateLabel')}
          value={date}
          onChange={setDate}
          placeholder={t('leavePay.overtime.form.pickDate')}
        />
        <View style={styles.times}>
          <View style={styles.time}>
            <PickerField
              testID="overtime-start"
              mode="time"
              label={t('leavePay.overtime.form.startLabel')}
              value={start}
              onChange={setStart}
              placeholder={t('leavePay.overtime.form.pickTime')}
              error={showErrors && !start ? t('leavePay.overtime.form.timesError') : undefined}
            />
          </View>
          <View style={styles.time}>
            <PickerField
              testID="overtime-end"
              mode="time"
              label={t('leavePay.overtime.form.endLabel')}
              value={end}
              onChange={setEnd}
              placeholder={t('leavePay.overtime.form.pickTime')}
              error={showErrors && !end ? t('leavePay.overtime.form.timesError') : undefined}
            />
          </View>
        </View>

        <Surface level={0} contentStyle={styles.switchRow}>
          <View style={styles.flex}>
            <Text style={[theme.typography.subheadline, { color: theme.colors.text, fontWeight: '600' }]}>
              {t('leavePay.overtime.form.nextDayLabel')}
            </Text>
            <Text style={[theme.typography.footnote, { color: theme.colors.textMuted }]}>
              {t('leavePay.overtime.form.nextDayHint')}
            </Text>
          </View>
          <Switch
            testID="overtime-next-day"
            accessibilityLabel={t('leavePay.overtime.form.nextDayLabel')}
            value={endsNextDay}
            onValueChange={(v) => {
              haptics.select();
              setEndsNextDay(v);
            }}
            trackColor={{ false: theme.colors.hairline, true: theme.colors.primaryFill }}
            thumbColor={theme.colors.onFill}
          />
        </Surface>

        {issueText ? <Notice tone="warning" body={issueText} testID="overtime-issue" /> : null}

        {check.minutes !== null && check.issue !== 'overHardCap' ? (
          <Surface level={1} contentStyle={styles.preview}>
            <Text style={[theme.typography.footnote, { color: theme.colors.textSecondary }]}>
              {t('leavePay.overtime.preview.duration')}
            </Text>
            <Text style={[theme.typography.title2, { color: theme.colors.text }]}>{text.duration(check.minutes)}</Text>
            {showCounted ? (
              <Text style={[theme.typography.footnote, { color: theme.colors.textSecondary }]}>
                {t('leavePay.overtime.preview.counted', {
                  value: text.duration(check.roundedMinutes ?? 0),
                  rule: t(`leavePay.overtime.preview.${roundingKey}`),
                })}
              </Text>
            ) : null}
            <Text style={[theme.typography.footnote, { color: theme.colors.textMuted }]}>
              {t('leavePay.overtime.preview.payNote')}
            </Text>
          </Surface>
        ) : null}

        {policy && policy.minimumMinutes > 0 && policy.maximumMinutesPerDay > 0 ? (
          <Text style={[theme.typography.footnote, { color: theme.colors.textMuted }]}>
            {t('leavePay.overtime.preview.policyLimits', {
              min: text.duration(policy.minimumMinutes),
              max: text.duration(policy.maximumMinutesPerDay),
            })}
          </Text>
        ) : null}

        <TextArea
          testID="overtime-reason"
          label={t('leavePay.overtime.form.reasonLabel')}
          value={reason}
          onChangeText={setReason}
          placeholder={t('leavePay.overtime.form.reasonPlaceholder')}
          error={showErrors ? reasonError : null}
        />

        {submitError ? <Notice tone="danger" title={t('leavePay.errors.sendFailedTitle')} body={submitError} /> : null}
      </ScrollView>

      <StickyFooter>
        <Text numberOfLines={1} style={[theme.typography.footnote, styles.summary, { color: theme.colors.textSecondary }]}>
          {date && check.minutes !== null && !check.issue
            ? t('leavePay.overtime.footer.summary', { date: f.date(date, 'dayMonth'), duration: text.duration(check.minutes) })
            : t('leavePay.overtime.footer.summaryEmpty')}
        </Text>
        <LiquidButton
          testID="overtime-send"
          label={t('leavePay.overtime.footer.send')}
          icon="paper-plane-outline"
          onPress={() => void submit()}
          loading={submitting}
          disabled={submitting}
        />
      </StickyFooter>
    </>
  );
}

function OvertimeHistory({
  reloadKey,
  sentNotice,
  onDismissNotice,
  onRequestNew,
}: {
  reloadKey: number;
  sentNotice: boolean;
  onDismissNotice: () => void;
  onRequestNew: () => void;
}) {
  const { theme } = useTheme();
  const { t } = useTranslation();
  const text = useLeavePayText();
  const [items, setItems] = useState<OvertimeRecord[]>([]);
  const [pay, setPay] = useState<Record<string, OvertimePay>>({});
  const [loading, setLoading] = useState(true);
  const [refreshing, setRefreshing] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(
    async (refresh = false) => {
      if (refresh) setRefreshing(true);
      else setLoading(true);
      setError(null);
      try {
        const [mine, calculations] = await Promise.all([
          overtimeApi.getMy(),
          // Pay is shown only where payroll actually calculated it; a failed read just hides it.
          overtimeApi.getMyPay().catch(() => [] as OvertimePay[]),
        ]);
        setItems(mine.items);
        setPay(Object.fromEntries(calculations.map((c) => [c.overtimeRequestId, c])));
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

  let body: React.ReactNode;
  if (loading) {
    body = (
      <View style={styles.list}>
        <SkeletonCard lines={2} />
        <SkeletonCard lines={2} />
      </View>
    );
  } else if (error) {
    body = <StateCard variant="error" body={error} onRetry={() => void load()} />;
  } else if (items.length === 0) {
    body = (
      <StateCard
        variant="empty"
        title={t('leavePay.overtime.list.emptyTitle')}
        body={t('leavePay.overtime.list.emptyBody')}
        action={{ label: t('leavePay.overtime.list.emptyAction'), onPress: onRequestNew }}
      />
    );
  } else {
    body = (
      <View style={styles.list}>
        {items.map((r) => (
          <OvertimeCard key={r.id} record={r} pay={pay[r.id]} />
        ))}
      </View>
    );
  }

  return (
    <ScrollView
      contentContainerStyle={styles.content}
      refreshControl={<RefreshControl refreshing={refreshing} onRefresh={() => void load(true)} tintColor={theme.colors.accent} />}
      showsVerticalScrollIndicator={false}
    >
      {sentNotice ? <Notice tone="success" body={t('leavePay.overtime.sent')} onDismiss={onDismissNotice} /> : null}
      {body}
    </ScrollView>
  );
}

function OvertimeCard({ record, pay }: { record: OvertimeRecord; pay?: OvertimePay }) {
  const { theme } = useTheme();
  const { t } = useTranslation();
  const f = useFormat();
  const text = useLeavePayText();
  const kind = overtimeStatusKind(record.rawStatus);
  const status = t(`leavePay.overtime.status.${kind}`);
  const minutes = record.durationMinutes ?? 0;
  const date = f.date(record.date, 'full');

  return (
    <Surface level={1} contentStyle={styles.card} testID={`overtime-request-${record.id}`}>
      <View accessible accessibilityLabel={t('leavePay.overtime.list.a11yRow', { date, duration: text.duration(minutes), status })} style={styles.cardBody}>
        <View style={styles.cardHead}>
          <Text style={[theme.typography.headline, styles.flex, { color: theme.colors.text }]}>{date}</Text>
          <StatusPill tone={statusTone(kind)} label={status} />
        </View>
        {record.startUtc && record.endUtc ? (
          <Text style={[theme.typography.subheadline, { color: theme.colors.text }]}>
            {t('leavePay.common.dateRange', { start: f.time(record.startUtc), end: f.time(record.endUtc) })}
          </Text>
        ) : null}
        <Text style={[theme.typography.footnote, { color: theme.colors.textSecondary }]}>
          {t('leavePay.overtime.list.requested', { value: text.duration(minutes) })}
          {kind === 'approved' && (record.approvedMinutes ?? 0) > 0
            ? ` · ${t('leavePay.overtime.list.approved', { value: text.duration(record.approvedMinutes ?? 0) })}`
            : ''}
        </Text>
        {record.reason ? (
          <Text numberOfLines={3} style={[theme.typography.footnote, { color: theme.colors.textMuted }]}>{record.reason}</Text>
        ) : null}
      </View>
      {pay ? (
        <Surface level={0} contentStyle={styles.pay}>
          <View style={styles.flex}>
            <Text style={[theme.typography.footnote, { color: theme.colors.textSecondary }]}>{t('leavePay.overtime.list.pay')}</Text>
            <Text style={[theme.typography.footnote, { color: theme.colors.textMuted }]}>
              {t('leavePay.overtime.list.payDetail', { hours: text.duration(pay.approvedMinutes), multiplier: f.number(pay.multiplier) })}
            </Text>
          </View>
          <Text style={[theme.typography.headline, { color: theme.colors.text, fontVariant: ['tabular-nums'] }]}>
            {f.money(pay.amount, pay.currency || undefined)}
          </Text>
        </Surface>
      ) : null}
    </Surface>
  );
}

const styles = StyleSheet.create({
  root: { flex: 1 },
  segments: { marginTop: 14 },
  content: { paddingHorizontal: 16, paddingTop: 4, paddingBottom: 28, gap: 16 },
  times: { flexDirection: 'row', flexWrap: 'wrap', gap: 12 },
  time: { flexGrow: 1, flexBasis: 150 },
  switchRow: { flexDirection: 'row', alignItems: 'center', gap: 12, padding: 14 },
  preview: { padding: 16, gap: 4 },
  summary: { textAlign: 'center' },
  list: { gap: 10 },
  card: { padding: 14, gap: 12 },
  cardBody: { gap: 4 },
  cardHead: { flexDirection: 'row', alignItems: 'flex-start', gap: 10, marginBottom: 2 },
  pay: { flexDirection: 'row', alignItems: 'center', gap: 12, padding: 12 },
  flex: { flex: 1 },
});
