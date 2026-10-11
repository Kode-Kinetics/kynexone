import React, { useCallback, useEffect, useMemo, useState } from 'react';
import { Platform, ScrollView, StyleSheet, Text, View } from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import * as DocumentPicker from 'expo-document-picker';
import { useTranslation } from 'react-i18next';
import {
  LiquidButton,
  MotionPressable,
  SegmentedControl,
  SkeletonCard,
  StateCard,
  StickyFooter,
  Surface,
} from '@/components/ui';
import { useTheme } from '@/theme/ThemeProvider';
import { useFormat } from '@/utils/format';
import { haptics } from '@/utils/haptics';
import { FEATURES } from '@/config/features';
import { documentsApi, normalizePickedFile, type PickedFile } from '@/api/services';
import {
  leaveRequestsApi,
  type LeaveBalanceRow,
  simpleBalanceEquation,
  type LeaveTypeOption,
  type MyLeaveRequest,
} from '@/api/leaveRequests';
import {
  balanceHint,
  describeRequestedLength,
  normalizeLeaveRange,
  type LeaveDayMode,
} from './leaveLogic';
import { FieldError, FieldLabel, Notice, TextArea } from './LeavePayParts';
import { PickerField } from './PickerField';
import { useLeavePayText } from './useLeavePayText';

interface Props {
  onSent: (request: MyLeaveRequest, typeName: string) => void;
}

/** The leave request form: one vertical column, one primary action in a sticky footer. */
export function LeaveRequestForm({ onSent }: Props) {
  const { theme } = useTheme();
  const { t } = useTranslation();
  const f = useFormat();
  const text = useLeavePayText();
  const today = f.businessDate() ?? undefined;

  const [types, setTypes] = useState<LeaveTypeOption[]>([]);
  const [balances, setBalances] = useState<LeaveBalanceRow[]>([]);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState<string | null>(null);

  const [typeId, setTypeId] = useState('');
  const [mode, setMode] = useState<LeaveDayMode>('full');
  const [start, setStart] = useState<string | null>(null);
  const [end, setEnd] = useState<string | null>(null);
  const [reason, setReason] = useState('');
  const [attachment, setAttachment] = useState<PickedFile | null>(null);
  const [attachmentError, setAttachmentError] = useState<string | null>(null);
  const [showErrors, setShowErrors] = useState(false);
  const [submitting, setSubmitting] = useState(false);
  const [submitError, setSubmitError] = useState<string | null>(null);

  const load = useCallback(async () => {
    setLoading(true);
    setLoadError(null);
    const [typesResult, balancesResult] = await Promise.allSettled([
      leaveRequestsApi.getTypes(),
      leaveRequestsApi.getBalances(),
    ]);
    if (typesResult.status === 'rejected') {
      setLoadError(text.error(typesResult.reason));
      setLoading(false);
      return;
    }
    const loadedTypes = typesResult.value;
    // A missing balance list must not block the form; types simply show no balance line.
    const loadedBalances = balancesResult.status === 'fulfilled' ? balancesResult.value : [];
    setTypes(loadedTypes);
    setBalances(loadedBalances);
    setTypeId((current) => {
      if (current && loadedTypes.some((x) => x.id === current)) return current;
      const withBalance = loadedTypes.find((x) => loadedBalances.some((b) => b.leaveTypeId === x.id));
      return (withBalance ?? loadedTypes[0])?.id ?? '';
    });
    setLoading(false);
  }, [text]);

  useEffect(() => {
    void load();
  }, [load]);

  const selectedType = types.find((x) => x.id === typeId) ?? null;
  // Statutory event leave (maternity, Hajj…) has no running balance: no balance card, no balance hint.
  const balance = balances.find((b) => b.leaveTypeId === typeId && b.statutoryDays === null) ?? null;
  const equation = balance ? simpleBalanceEquation(balance) : null;
  const halfAllowed = Boolean(selectedType?.isHalfDayAllowed);
  const effectiveMode: LeaveDayMode = halfAllowed ? mode : 'full';

  // The range that will be sent — a half day is always one date (the old screen's data bug).
  const range = useMemo(
    () => (start ? normalizeLeaveRange({ start, end: end ?? start, mode: effectiveMode }) : null),
    [effectiveMode, end, start],
  );
  const length = range ? describeRequestedLength({ ...range, mode: effectiveMode }) : null;
  const hint = length ? balanceHint(length.days, balance?.available) : 'unknown';
  const typeName = selectedType ? text.name(selectedType.nameEn, selectedType.nameAr) : '';

  const errors = {
    type: !typeId ? t('leavePay.leave.form.typeError') : null,
    start: !start ? t('leavePay.leave.form.pickDate') : null,
    reason: selectedType?.requiresReason && !reason.trim() ? t('leavePay.leave.form.reasonError') : null,
    attachment:
      FEATURES.FILE_UPLOAD && selectedType?.requiresAttachment && !attachment ? t('leavePay.leave.form.attachmentError') : null,
  };
  const hasErrors = Object.values(errors).some(Boolean);

  const selectType = (id: string) => {
    setTypeId(id);
    setSubmitError(null);
    const next = types.find((x) => x.id === id);
    if (!next?.isHalfDayAllowed) setMode('full');
  };

  const selectMode = (next: LeaveDayMode) => {
    setMode(next);
    // Switching to a half day collapses the range to its first date, in state as well as on send.
    if (next === 'half' && start) setEnd(start);
  };

  const selectStart = (value: string) => {
    setStart(value);
    if (effectiveMode === 'half' || !end || end < value) setEnd(value);
  };

  const pickAttachment = async () => {
    setAttachmentError(null);
    try {
      const result = await DocumentPicker.getDocumentAsync({ type: ['application/pdf', 'image/*'], copyToCacheDirectory: true });
      const file = !result.canceled ? result.assets?.[0] : undefined;
      if (file) {
        setAttachment(normalizePickedFile({ uri: file.uri, name: file.name, mimeType: file.mimeType, size: file.size }));
      }
    } catch {
      setAttachmentError(t('leavePay.leave.form.attachmentFailed'));
    }
  };

  const submit = async () => {
    setShowErrors(true);
    setSubmitError(null);
    if (hasErrors || !range || !selectedType) {
      haptics.error();
      return;
    }
    setSubmitting(true);
    try {
      const uploaded = attachment
        ? await documentsApi.uploadDocument({ file: attachment, documentType: 'Leave Attachment' })
        : null;
      const created = await leaveRequestsApi.submit({
        leaveTypeId: selectedType.id,
        startDate: range.start,
        endDate: range.end,
        isHalfDay: effectiveMode === 'half',
        reason,
        attachmentDocumentId: uploaded?.id,
      });
      haptics.success();
      setStart(null);
      setEnd(null);
      setReason('');
      setAttachment(null);
      setShowErrors(false);
      onSent(created, typeName);
    } catch (error) {
      haptics.error();
      setSubmitError(text.error(error));
    } finally {
      setSubmitting(false);
    }
  };

  if (loading) {
    return (
      <View style={styles.pad}>
        <SkeletonCard lines={2} />
        <SkeletonCard lines={3} style={styles.gapTop} />
      </View>
    );
  }

  if (loadError) {
    return (
      <View style={styles.pad}>
        <StateCard variant="error" body={loadError} onRetry={() => void load()} />
      </View>
    );
  }

  const lengthLabel = length
    ? length.basis === 'halfDay'
      ? t('leavePay.halfDay')
      : text.count('calendarDays', length.days)
    : null;

  return (
    <>
      <ScrollView
        contentContainerStyle={styles.content}
        keyboardShouldPersistTaps="handled"
        keyboardDismissMode={Platform.OS === 'ios' ? 'interactive' : 'on-drag'}
        automaticallyAdjustKeyboardInsets
        showsVerticalScrollIndicator={false}
      >
        {/* 1 · Leave type */}
        <View style={styles.group} accessibilityRole="radiogroup">
          <FieldLabel label={t('leavePay.leave.form.typeLabel')} />
          <View style={styles.chips}>
            {types.map((type) => {
              const selected = type.id === typeId;
              const b = balances.find((x) => x.leaveTypeId === type.id && x.statutoryDays === null);
              const name = text.name(type.nameEn, type.nameAr);
              const sub = b ? text.count('daysLeft', b.available) : null;
              return (
                <MotionPressable
                  key={type.id}
                  testID={`leave-type-${type.id}`}
                  accessibilityRole="radio"
                  accessibilityState={{ selected }}
                  accessibilityLabel={sub ? `${name}, ${sub}` : name}
                  haptic="selection"
                  onPress={() => selectType(type.id)}
                  contentStyle={[
                    styles.chip,
                    {
                      backgroundColor: selected ? theme.colors.status.info.tint : theme.colors.card,
                      borderColor: selected ? theme.colors.accent : theme.colors.hairline,
                    },
                  ]}
                >
                  <Text style={[theme.typography.subheadline, styles.chipName, { color: selected ? theme.colors.accent : theme.colors.text }]}>
                    {name}
                  </Text>
                  {sub ? (
                    <Text style={[theme.typography.footnote, { color: theme.colors.textSecondary }]}>{sub}</Text>
                  ) : null}
                </MotionPressable>
              );
            })}
          </View>
          {showErrors ? <FieldError text={errors.type} /> : null}
        </View>

        {balance && selectedType ? (
          <Surface level={1} contentStyle={styles.balance}>
            <View style={styles.balanceHead}>
              <Text style={[theme.typography.headline, styles.flex, { color: theme.colors.text }]}>
                {t('leavePay.leave.balance.title', { type: typeName })}
              </Text>
              <Text style={[theme.typography.footnote, { color: theme.colors.textMuted }]}>
                {balance.year ? t('leavePay.leave.balance.period', { year: balance.year }) : t('leavePay.leave.balance.periodUnknown')}
              </Text>
            </View>
            <View style={styles.balanceRow}>
              <BalanceFigure label={t('leavePay.leave.balance.available')} value={f.number(balance.available)} strong />
              <BalanceFigure label={t('leavePay.leave.balance.used')} value={f.number(balance.used)} />
              <BalanceFigure label={t('leavePay.leave.balance.pending')} value={f.number(balance.pending)} />
            </View>
            {equation ? (
              <Text style={[theme.typography.footnote, { color: theme.colors.textMuted }]}>
                {t('leavePay.leave.balance.equation', {
                  granted: f.number(equation.granted),
                  used: f.number(equation.used),
                  pending: f.number(equation.pending),
                  available: f.number(equation.available),
                })}
              </Text>
            ) : null}
          </Surface>
        ) : null}

        {/* 2 · How long */}
        {halfAllowed ? (
          <View style={styles.group}>
            <FieldLabel label={t('leavePay.leave.form.lengthLabel')} />
            <SegmentedControl
              testID="leave-day-mode"
              value={effectiveMode}
              onChange={selectMode}
              segments={[
                { key: 'full', label: t('leavePay.leave.form.fullDays') },
                { key: 'half', label: t('leavePay.leave.form.halfDay') },
              ]}
            />
            {effectiveMode === 'half' ? (
              <Text style={[theme.typography.footnote, { color: theme.colors.textMuted }]}>
                {t('leavePay.leave.form.halfDayNote')}
              </Text>
            ) : null}
          </View>
        ) : null}

        {/* 3 · Dates */}
        <PickerField
          testID="leave-start"
          mode="date"
          label={effectiveMode === 'half' ? t('leavePay.leave.form.dateLabel') : t('leavePay.leave.form.firstDay')}
          value={start}
          onChange={selectStart}
          placeholder={t('leavePay.leave.form.pickDate')}
          minimumDate={today}
          error={showErrors && errors.start ? errors.start : undefined}
        />
        {effectiveMode === 'full' ? (
          <PickerField
            testID="leave-end"
            mode="date"
            label={t('leavePay.leave.form.lastDay')}
            value={start ? range?.end ?? null : null}
            onChange={setEnd}
            placeholder={t('leavePay.leave.form.pickDate')}
            minimumDate={start ?? today}
          />
        ) : null}

        {length && lengthLabel ? (
          <Surface level={0} contentStyle={styles.length}>
            <View style={styles.lengthHead}>
              <Ionicons accessible={false} name="calendar-number-outline" size={20} color={theme.colors.accent} />
              <Text style={[theme.typography.title3, styles.flex, { color: theme.colors.text }]}>{lengthLabel}</Text>
            </View>
            <Text style={[theme.typography.footnote, { color: theme.colors.textSecondary }]}>
              {length.basis === 'halfDay' ? t('leavePay.leave.length.halfDayNote') : t('leavePay.leave.length.calendarNote')}
            </Text>
          </Surface>
        ) : null}
        {hint === 'mayExceed' && balance ? (
          <Notice tone="info" body={t('leavePay.leave.length.mayExceed', { available: text.days(balance.available) })} />
        ) : null}

        {/* 4 · Reason */}
        <TextArea
          testID="leave-reason"
          label={t('leavePay.leave.form.reasonLabel')}
          optional={!selectedType?.requiresReason}
          value={reason}
          onChangeText={setReason}
          placeholder={t('leavePay.leave.form.reasonPlaceholder')}
          error={showErrors ? errors.reason : null}
        />

        {/* 5 · Supporting document */}
        {FEATURES.FILE_UPLOAD ? (
          <View style={styles.group}>
            <FieldLabel label={t('leavePay.leave.form.attachmentLabel')} optional={!selectedType?.requiresAttachment} />
            {attachment ? (
              <Surface level={0} contentStyle={styles.file}>
                <Ionicons accessible={false} name="document-attach-outline" size={20} color={theme.colors.accent} />
                <View style={styles.flex}>
                  <Text numberOfLines={1} style={[theme.typography.subheadline, { color: theme.colors.text }]}>{attachment.name}</Text>
                  <Text style={[theme.typography.footnote, { color: theme.colors.textMuted }]}>{t('leavePay.leave.form.attachmentReady')}</Text>
                </View>
                <MotionPressable
                  accessibilityRole="button"
                  accessibilityLabel={t('leavePay.leave.form.attachmentRemove', { name: attachment.name })}
                  onPress={() => setAttachment(null)}
                  contentStyle={styles.iconButton}
                >
                  <Ionicons accessible={false} name="close-circle" size={22} color={theme.colors.textMuted} />
                </MotionPressable>
              </Surface>
            ) : (
              <LiquidButton
                variant="secondary"
                size="compact"
                icon="attach-outline"
                label={t('leavePay.leave.form.attachmentAdd')}
                onPress={() => void pickAttachment()}
              />
            )}
            <FieldError text={attachmentError ?? (showErrors ? errors.attachment : null)} />
          </View>
        ) : null}

        {submitError ? <Notice tone="danger" title={t('leavePay.errors.sendFailedTitle')} body={submitError} /> : null}
      </ScrollView>

      <StickyFooter>
        <Text numberOfLines={2} style={[theme.typography.footnote, styles.summary, { color: theme.colors.textSecondary }]}>
          {selectedType
            ? t('leavePay.leave.footer.summary', {
                type: typeName,
                length: range && lengthLabel ? `${text.dateRange(range.start, range.end, 'dayMonth')} · ${lengthLabel}` : t('leavePay.leave.form.pickDate'),
              })
            : t('leavePay.leave.footer.summaryNoType')}
        </Text>
        <LiquidButton
          testID="leave-send"
          label={t('leavePay.leave.footer.send')}
          icon="paper-plane-outline"
          onPress={() => void submit()}
          loading={submitting}
          disabled={submitting || !typeId}
        />
      </StickyFooter>
    </>
  );
}

function BalanceFigure({ label, value, strong }: { label: string; value: string; strong?: boolean }) {
  const { theme } = useTheme();
  return (
    <View style={styles.figure} accessible accessibilityLabel={`${label}, ${value}`}>
      <Text style={[strong ? theme.typography.title2 : theme.typography.title3, { color: theme.colors.text, fontVariant: ['tabular-nums'] }]}>
        {value}
      </Text>
      <Text style={[theme.typography.footnote, { color: theme.colors.textSecondary }]}>{label}</Text>
    </View>
  );
}

const styles = StyleSheet.create({
  pad: { paddingHorizontal: 16, paddingTop: 4 },
  gapTop: { marginTop: 12 },
  content: { paddingHorizontal: 16, paddingTop: 4, paddingBottom: 24, gap: 18 },
  group: { gap: 8 },
  chips: { flexDirection: 'row', flexWrap: 'wrap', gap: 8 },
  chip: {
    minHeight: 44,
    justifyContent: 'center',
    paddingHorizontal: 14,
    paddingVertical: 8,
    borderRadius: 14,
    borderWidth: 1,
    gap: 2,
  },
  chipName: { fontWeight: '600' },
  balance: { padding: 16, gap: 12 },
  balanceHead: { flexDirection: 'row', alignItems: 'baseline', flexWrap: 'wrap', gap: 8 },
  balanceRow: { flexDirection: 'row', gap: 12 },
  figure: { flex: 1, gap: 2 },
  flex: { flex: 1 },
  length: { padding: 14, gap: 6 },
  lengthHead: { flexDirection: 'row', alignItems: 'center', gap: 8 },
  file: { flexDirection: 'row', alignItems: 'center', gap: 10, padding: 12 },
  iconButton: { width: 44, height: 44, alignItems: 'center', justifyContent: 'center' },
  summary: { textAlign: 'center' },
});
