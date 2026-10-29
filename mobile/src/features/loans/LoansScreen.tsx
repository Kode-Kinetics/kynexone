import React, { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { RefreshControl, ScrollView, StyleSheet, Text, View } from 'react-native';
import { useFocusEffect } from '@react-navigation/native';
import { Ionicons } from '@expo/vector-icons';
import { useTranslation } from 'react-i18next';
import { loansApi } from '@/api/loans';
import {
  BottomSheet,
  DirectionalIcon,
  GlassTextField,
  LiquidBackdrop,
  LiquidButton,
  MotionPressable,
  ScreenHero,
  Skeleton,
  SkeletonCard,
  StateCard,
  StatusPill,
  Surface,
} from '@/components/ui';
import { useTheme } from '@/theme/ThemeProvider';
import { useFormat } from '@/utils/format';
import { haptics } from '@/utils/haptics';
import {
  applyState,
  estimatedInstallment,
  installmentCeiling,
  installmentStatus,
  isOpenLoan,
  loanStatus,
  loansAccessFromError,
  reasonDisplay,
  repaymentMethodFor,
  summarizeLoan,
  validateApplication,
  type ApplyState,
  type Loan,
  type LoanEligibility,
  type LoanInstallment,
  type LoanPolicySummary,
  type LoansAccess,
  type OfferedLoanType,
  type Reason,
} from './loanRules';
import { useSensitiveScreen } from '@/safety/useSensitiveScreen';

const NOTES_MAX = 500;

interface TypeCheck {
  eligibility: LoanEligibility | null | undefined;
  method: string;
  maxInstallments: number | null;
}

type LoadState =
  | { kind: 'loading' }
  | { kind: 'blocked'; access: Exclude<LoansAccess, 'ok'> }
  | {
      kind: 'ready';
      loans: Loan[];
      types: OfferedLoanType[];
      policies: LoanPolicySummary[];
      typeLimits: Map<string, number>;
    };

function failure(error: unknown): { status?: number; code?: string } {
  const e = error as { response?: { status?: number; data?: any } };
  const data = e?.response?.data;
  const code = typeof data?.error === 'string' ? data.error : typeof data?.code === 'string' ? data.code : undefined;
  return { status: e?.response?.status, code };
}

export default function LoansScreen() {
  useSensitiveScreen('loans');
  const { t, i18n } = useTranslation();
  const { theme } = useTheme();
  const f = useFormat();
  const language: 'en' | 'ar' = i18n.language?.startsWith('ar') ? 'ar' : 'en';
  const [state, setState] = useState<LoadState>({ kind: 'loading' });
  const [refreshing, setRefreshing] = useState(false);
  const [schedules, setSchedules] = useState<Record<string, LoanInstallment[] | null>>({});
  const [checks, setChecks] = useState<Record<string, TypeCheck>>({});
  const [detail, setDetail] = useState<Loan | null>(null);
  const [applying, setApplying] = useState<OfferedLoanType | null>(null);
  const [sent, setSent] = useState(false);
  // Pending, rejected and past loans have no schedule loaded up front; the detail sheet asks once.
  const requested = useRef(new Set<string>());

  const money = useCallback(
    (amount: number | null | undefined, currency: string | null | undefined) =>
      currency ? f.money(amount, currency) : f.number(amount, { minimumFractionDigits: 2, maximumFractionDigits: 2 }),
    [f],
  );

  const typeName = useCallback((loanTypeId: string, fallback: string, types: OfferedLoanType[]) => {
    const type = types.find((x) => x.loanTypeId === loanTypeId);
    if (!type) return fallback;
    return language === 'ar' && type.nameAr.trim() ? type.nameAr : type.nameEn || fallback;
  }, [language]);

  const checkType = useCallback(async (
    type: OfferedLoanType,
    policies: LoanPolicySummary[],
    typeLimits: Map<string, number>,
  ) => {
    const first = repaymentMethodFor(policies, type.loanTypeId);
    const ceiling = (policy: LoanPolicySummary | null) =>
      installmentCeiling(typeLimits.get(type.loanTypeId) ?? null, policy?.maxInstallments ?? null);
    setChecks((c) => ({ ...c, [type.loanTypeId]: { eligibility: undefined, method: first.method, maxInstallments: ceiling(first.policy) } }));
    try {
      let result = await loansApi.checkEligibility({ loanTypeId: type.loanTypeId, repaymentMethod: first.method });
      // The server names the policy it applied; that policy, not a guess, decides the repayment method.
      const named = repaymentMethodFor(policies, type.loanTypeId, result.policyId);
      if (named.method !== first.method) {
        result = await loansApi.checkEligibility({ loanTypeId: type.loanTypeId, repaymentMethod: named.method });
      }
      setChecks((c) => ({
        ...c,
        [type.loanTypeId]: { eligibility: result, method: named.method, maxInstallments: ceiling(named.policy ?? first.policy) },
      }));
    } catch {
      setChecks((c) => ({ ...c, [type.loanTypeId]: { eligibility: null, method: first.method, maxInstallments: ceiling(first.policy) } }));
    }
  }, []);

  const load = useCallback(async (mode: 'initial' | 'refresh' = 'initial') => {
    if (mode === 'refresh') setRefreshing(true);
    try {
      const enabled = await loansApi.moduleEnabled().catch(() => true);
      if (!enabled) {
        setState({ kind: 'blocked', access: 'moduleOff' });
        return;
      }
      const [loans, types, policies, typeLimits] = await Promise.all([
        loansApi.getMyLoans(),
        loansApi.getOfferedTypes(),
        loansApi.getPolicies().catch(() => [] as LoanPolicySummary[]),
        loansApi.getTypeLimits().catch(() => new Map<string, number>()),
      ]);
      setState({ kind: 'ready', loans, types, policies, typeLimits });

      const scheduled = loans.filter((loan) => loan.status === 'Active' || loan.status === 'Approved');
      requested.current = new Set(scheduled.map((loan) => loan.id));
      void Promise.all(scheduled.map(async (loan) => {
        try {
          const rows = await loansApi.getInstallments(loan.id);
          setSchedules((s) => ({ ...s, [loan.id]: rows }));
        } catch {
          setSchedules((s) => ({ ...s, [loan.id]: null }));
        }
      }));
      types.forEach((type) => {
        if (type.offered) void checkType(type, policies, typeLimits);
      });
    } catch (error) {
      const { status, code } = failure(error);
      const access = loansAccessFromError(status, code);
      setState((current) => (current.kind === 'ready' && access === 'error'
        ? current
        : { kind: 'blocked', access: access === 'ok' ? 'error' : access }));
    } finally {
      setRefreshing(false);
    }
  }, [checkType]);

  useFocusEffect(useCallback(() => {
    void load();
  }, [load]));

  const requestSchedule = useCallback((loan: Loan) => {
    if (requested.current.has(loan.id)) return;
    requested.current.add(loan.id);
    loansApi.getInstallments(loan.id)
      .then((rows) => setSchedules((s) => ({ ...s, [loan.id]: rows })))
      .catch(() => setSchedules((s) => ({ ...s, [loan.id]: null })));
  }, []);

  const ready = state.kind === 'ready' ? state : null;
  const openLoans = useMemo(() => (ready ? ready.loans.filter(isOpenLoan) : []), [ready]);
  const pastLoans = useMemo(() => (ready ? ready.loans.filter((loan) => !isOpenLoan(loan)) : []), [ready]);

  const reasonText = (reason: Reason): string => {
    const shown = reasonDisplay(reason, language);
    if (shown.kind === 'server') return shown.text;
    if (shown.kind === 'code') return t(`account.loans.reasons.${shown.code as 'Probation'}`);
    return t('account.loans.reasons.generic');
  };

  const renderOpenLoan = (loan: Loan, types: OfferedLoanType[]) => {
    const status = loanStatus(loan.status);
    const schedule = schedules[loan.id];
    const summary = summarizeLoan(loan, schedule ?? []);
    const name = typeName(loan.loanTypeId, loan.loanTypeName, types);
    const repaying = loan.status === 'Active' || loan.status === 'Approved';
    return (
      <MotionPressable
        key={loan.id}
        accessibilityRole="button"
        accessibilityLabel={[name, t(`account.loans.status.${status.key}`), repaying
          ? t('account.loans.remainingA11y', { amount: money(summary.remaining, loan.currency) })
          : null].filter(Boolean).join(', ')}
        accessibilityHint={t('account.loans.openDetailHint')}
        onPress={() => setDetail(loan)}
        testID={`loan-${loan.id}`}
      >
        <Surface contentStyle={styles.card}>
          <View style={styles.rowTop}>
            <View style={styles.flex}>
              <Text style={[theme.typography.headline, { color: theme.colors.text }]}>{name}</Text>
              <Text selectable style={[theme.typography.footnote, { color: theme.colors.textMuted }]}>{loan.loanNumber}</Text>
            </View>
            <StatusPill tone={status.tone} label={t(`account.loans.status.${status.key}`)} />
          </View>

          {repaying ? (
            <>
              <View>
                <Text style={[theme.typography.footnote, { color: theme.colors.textSecondary }]}>{t('account.loans.remaining')}</Text>
                <Text style={[theme.typography.title2, styles.figure, { color: theme.colors.text }]}>
                  {money(summary.remaining, loan.currency)}
                </Text>
                <Text style={[theme.typography.footnote, { color: theme.colors.textSecondary }]}>
                  {t('account.loans.ofPrincipal', { amount: money(summary.principal, loan.currency) })}
                </Text>
              </View>
              {summary.progress !== null ? (
                <View
                  accessible
                  accessibilityRole="progressbar"
                  accessibilityLabel={t('account.loans.repaidA11y', {
                    repaid: money(summary.repaid, loan.currency),
                    total: money(summary.principal, loan.currency),
                  })}
                  accessibilityValue={{ min: 0, max: 100, now: Math.round(summary.progress * 100) }}
                  style={[styles.track, { backgroundColor: theme.colors.surfaceInset }]}
                >
                  <View style={[styles.fill, { width: `${summary.progress * 100}%`, backgroundColor: theme.colors.accent }]} />
                </View>
              ) : null}
              {schedule === undefined ? (
                <Skeleton width="70%" height={14} />
              ) : schedule === null ? (
                <Text style={[theme.typography.footnote, { color: theme.colors.textSecondary }]}>{t('account.loans.scheduleUnavailable')}</Text>
              ) : (
                <>
                  <Text style={[theme.typography.footnote, { color: theme.colors.textSecondary }]}>
                    {t('account.loans.paidOf', { paid: summary.paidCount, total: summary.scheduledCount })}
                  </Text>
                  {summary.next ? (
                    <View style={[styles.next, { backgroundColor: theme.colors.surfaceInset }]}>
                      <Ionicons accessible={false} name="calendar-outline" size={20} color={theme.colors.accent} />
                      <View style={styles.flex}>
                        <Text style={[theme.typography.footnote, { color: theme.colors.textSecondary }]}>{t('account.loans.nextInstallment')}</Text>
                        <Text style={[theme.typography.headline, { color: theme.colors.text }]}>
                          {t('account.loans.nextLine', {
                            amount: money(summary.next.amountDue - summary.next.amountPaid, loan.currency),
                            date: f.date(summary.next.dueDate, 'medium'),
                          })}
                        </Text>
                        {loan.repaymentMethod === 'PayrollDeduction' ? (
                          <Text style={[theme.typography.footnote, { color: theme.colors.textMuted }]}>{t('account.loans.fromSalary')}</Text>
                        ) : null}
                      </View>
                    </View>
                  ) : null}
                </>
              )}
            </>
          ) : (
            <Text style={[theme.typography.subheadline, { color: theme.colors.textSecondary }]}>
              {t('account.loans.pendingLine', {
                amount: money(loan.requestedAmount, loan.currency),
                count: loan.requestedInstallments,
              })}
            </Text>
          )}
          <View style={styles.more}>
            <Text style={[theme.typography.footnote, { color: theme.colors.accent }]}>{t('account.loans.viewSchedule')}</Text>
            <DirectionalIcon name="chevron" size={16} color={theme.colors.accent} />
          </View>
        </Surface>
      </MotionPressable>
    );
  };

  const renderApplyCard = (type: OfferedLoanType, policies: LoanPolicySummary[], typeLimits: Map<string, number>) => {
    const check = checks[type.loanTypeId];
    const card: ApplyState = applyState(type, check?.eligibility, check?.maxInstallments ?? null);
    const name = language === 'ar' && type.nameAr.trim() ? type.nameAr : type.nameEn;
    return (
      <Surface key={type.loanTypeId} contentStyle={styles.card} testID={`loan-type-${type.code}`}>
        <View style={styles.rowTop}>
          <Text style={[theme.typography.headline, styles.flex, { color: theme.colors.text }]}>{name}</Text>
          {card.kind === 'ineligible' ? <StatusPill tone="warning" label={t('account.loans.notNow')} /> : null}
          {card.kind === 'notOffered' ? <StatusPill tone="neutral" label={t('account.loans.notOffered')} /> : null}
        </View>

        {card.kind === 'checking' ? (
          <View style={styles.skeletons}>
            <Skeleton width="80%" height={14} />
            <Skeleton width="55%" height={14} />
          </View>
        ) : null}

        {card.kind === 'eligible' ? (
          <>
            <Text style={[theme.typography.subheadline, { color: theme.colors.text }]}>
              {card.available !== null
                ? t('account.loans.upTo', { amount: money(card.available, card.currency) })
                : t('account.loans.eligibleNoLimit')}
            </Text>
            {card.maxInstallments !== null ? (
              <Text style={[theme.typography.footnote, { color: theme.colors.textSecondary }]}>
                {t('account.loans.maxInstallments', { count: card.maxInstallments })}
              </Text>
            ) : null}
            <LiquidButton
              label={t('account.loans.apply')}
              trailingIcon="forward"
              size="compact"
              onPress={() => setApplying(type)}
              style={styles.applyButton}
            />
          </>
        ) : null}

        {card.kind === 'ineligible' ? (
          <View style={styles.reasons} accessibilityRole="text">
            {card.reasons.map((reason, i) => (
              <View key={`${reason.code ?? 'r'}-${i}`} style={styles.reasonRow}>
                <Ionicons accessible={false} name="information-circle-outline" size={18} color={theme.colors.status.warning.fg} />
                <Text style={[theme.typography.subheadline, styles.flex, { color: theme.colors.text }]}>{reasonText(reason)}</Text>
              </View>
            ))}
            <Text style={[theme.typography.footnote, { color: theme.colors.textMuted }]}>{t('account.loans.askHr')}</Text>
          </View>
        ) : null}

        {card.kind === 'notOffered' ? (
          <Text style={[theme.typography.subheadline, { color: theme.colors.textSecondary }]}>{reasonText(card.reason)}</Text>
        ) : null}

        {card.kind === 'unknown' ? (
          <View style={styles.reasons}>
            <Text style={[theme.typography.subheadline, { color: theme.colors.textSecondary }]}>{t('account.loans.checkFailed')}</Text>
            <LiquidButton
              label={t('kit.retry')}
              variant="secondary"
              size="compact"
              onPress={() => void checkType(type, policies, typeLimits)}
              style={styles.applyButton}
            />
          </View>
        ) : null}
      </Surface>
    );
  };

  const renderBody = () => {
    if (state.kind === 'loading') {
      return (
        <View style={styles.list}>
          <SkeletonCard lines={3} />
          <SkeletonCard lines={2} />
        </View>
      );
    }
    if (state.kind === 'blocked') {
      if (state.access === 'error') return <StateCard variant="error" style={styles.state} onRetry={() => void load()} />;
      return (
        <StateCard
          variant="permission"
          style={styles.state}
          title={t(`account.loans.blocked.${state.access}Title`)}
          body={t(`account.loans.blocked.${state.access}Body`)}
        />
      );
    }
    const { types, policies, typeLimits } = state;
    return (
      <View style={styles.list}>
        {sent ? (
          <Surface tintColor={theme.colors.status.success.tint} contentStyle={styles.notice}>
            <Ionicons accessible={false} name="checkmark-circle" size={22} color={theme.colors.status.success.fg} />
            <Text accessibilityLiveRegion="polite" style={[theme.typography.subheadline, styles.flex, { color: theme.colors.text }]}>
              {t('account.loans.sent')}
            </Text>
          </Surface>
        ) : null}

        <Text accessibilityRole="header" style={[theme.typography.headline, { color: theme.colors.text }]}>
          {t('account.loans.yourLoans')}
        </Text>
        {openLoans.length === 0 ? (
          <Surface contentStyle={styles.card}>
            <Text style={[theme.typography.subheadline, { color: theme.colors.textSecondary }]}>{t('account.loans.noOpenLoans')}</Text>
          </Surface>
        ) : (
          openLoans.map((loan) => renderOpenLoan(loan, types))
        )}

        {types.length > 0 ? (
          <>
            <Text accessibilityRole="header" style={[theme.typography.headline, styles.sectionGap, { color: theme.colors.text }]}>
              {t('account.loans.applyHeader')}
            </Text>
            {types.map((type) => renderApplyCard(type, policies, typeLimits))}
            <Text style={[theme.typography.footnote, { color: theme.colors.textMuted }]}>{t('account.loans.applyFootnote')}</Text>
          </>
        ) : null}

        {pastLoans.length > 0 ? (
          <>
            <Text accessibilityRole="header" style={[theme.typography.headline, styles.sectionGap, { color: theme.colors.text }]}>
              {t('account.loans.pastLoans')}
            </Text>
            {pastLoans.map((loan) => {
              const status = loanStatus(loan.status);
              return (
                <MotionPressable
                  key={loan.id}
                  accessibilityRole="button"
                  accessibilityHint={t('account.loans.openDetailHint')}
                  onPress={() => setDetail(loan)}
                >
                  <Surface contentStyle={styles.pastRow}>
                    <View style={styles.flex}>
                      <Text style={[theme.typography.callout, { color: theme.colors.text }]}>
                        {typeName(loan.loanTypeId, loan.loanTypeName, types)}
                      </Text>
                      <Text style={[theme.typography.footnote, { color: theme.colors.textSecondary }]}>
                        {[money(loan.approvedAmount > 0 ? loan.approvedAmount : loan.requestedAmount, loan.currency), f.date(loan.createdAtUtc, 'medium')].join(' · ')}
                      </Text>
                    </View>
                    <StatusPill tone={status.tone} label={t(`account.loans.status.${status.key}`)} />
                  </Surface>
                </MotionPressable>
              );
            })}
          </>
        ) : null}
      </View>
    );
  };

  return (
    <View style={[styles.root, { backgroundColor: theme.colors.canvas }]}>
      <LiquidBackdrop subtle />
      <ScrollView
        contentContainerStyle={styles.content}
        refreshControl={
          <RefreshControl
            refreshing={refreshing}
            onRefresh={() => void load('refresh')}
            tintColor={theme.colors.accent}
            colors={[theme.colors.accent]}
          />
        }
        showsVerticalScrollIndicator={false}
      >
        <ScreenHero
          eyebrow={t('account.loans.eyebrow')}
          title={t('account.loans.title')}
          subtitle={t('account.loans.subtitle')}
        />
        {renderBody()}
      </ScrollView>

      <LoanDetailSheet
        loan={detail}
        name={detail && ready ? typeName(detail.loanTypeId, detail.loanTypeName, ready.types) : ''}
        schedule={detail ? schedules[detail.id] : undefined}
        money={money}
        onClose={() => setDetail(null)}
        onNeedSchedule={requestSchedule}
      />

      {applying && ready ? (
        <ApplySheet
          type={applying}
          name={language === 'ar' && applying.nameAr.trim() ? applying.nameAr : applying.nameEn}
          check={checks[applying.loanTypeId]}
          money={money}
          reasonText={reasonText}
          onClose={() => setApplying(null)}
          onSent={() => {
            setApplying(null);
            setSent(true);
            void load('refresh');
          }}
        />
      ) : null}
    </View>
  );
}

// ---------------------------------------------------------------------------------------------

function LoanDetailSheet({
  loan,
  name,
  schedule,
  money,
  onClose,
  onNeedSchedule,
}: {
  loan: Loan | null;
  name: string;
  schedule: LoanInstallment[] | null | undefined;
  money: (amount: number | null | undefined, currency: string | null | undefined) => string;
  onClose: () => void;
  onNeedSchedule: (loan: Loan) => void;
}) {
  const { t } = useTranslation();
  const { theme } = useTheme();
  const f = useFormat();
  useEffect(() => {
    if (loan) onNeedSchedule(loan);
  }, [loan, onNeedSchedule]);

  const rows: [string, string][] = loan
    ? [
        [t('account.loans.detail.requested'), money(loan.requestedAmount, loan.currency)],
        ...(loan.approvedAmount > 0 ? [[t('account.loans.detail.approved'), money(loan.approvedAmount, loan.currency)] as [string, string]] : []),
        [t('account.loans.detail.repaid'), money(loan.totalRepaid, loan.currency)],
        [t('account.loans.detail.remaining'), money(loan.outstandingBalance, loan.currency)],
        ...(loan.installmentAmount > 0 ? [[t('account.loans.detail.installment'), money(loan.installmentAmount, loan.currency)] as [string, string]] : []),
        [t('account.loans.detail.method'), loan.repaymentMethod === 'PayrollDeduction'
          ? t('account.loans.method.payroll')
          : loan.repaymentMethod === 'BankTransfer'
            ? t('account.loans.method.bank')
            : t('account.loans.method.other')],
      ]
    : [];

  return (
    <BottomSheet visible={loan !== null} onClose={onClose} title={name} subtitle={loan?.loanNumber}>
      {loan?.status === 'Rejected' && loan.rejectionReason ? (
        <Surface tintColor={theme.colors.status.danger.tint} contentStyle={styles.notice}>
          <Text style={[theme.typography.subheadline, styles.flex, { color: theme.colors.text }]}>
            {t('account.loans.detail.rejectedBecause', { reason: loan.rejectionReason })}
          </Text>
        </Surface>
      ) : null}
      <Surface level={0} contentStyle={styles.detailTable}>
        {rows.map(([label, value]) => (
          <View key={label} style={styles.detailRow}>
            <Text style={[theme.typography.subheadline, styles.flex, { color: theme.colors.textSecondary }]}>{label}</Text>
            <Text style={[theme.typography.headline, styles.tabular, { color: theme.colors.text }]}>{value}</Text>
          </View>
        ))}
      </Surface>

      <Text accessibilityRole="header" style={[theme.typography.headline, { color: theme.colors.text }]}>
        {t('account.loans.detail.schedule')}
      </Text>
      {schedule === undefined ? (
        <View style={styles.skeletons}>
          <Skeleton height={16} />
          <Skeleton height={16} />
          <Skeleton height={16} />
        </View>
      ) : schedule === null ? (
        <Text style={[theme.typography.subheadline, { color: theme.colors.textSecondary }]}>{t('account.loans.scheduleUnavailable')}</Text>
      ) : schedule.length === 0 ? (
        <Text style={[theme.typography.subheadline, { color: theme.colors.textSecondary }]}>{t('account.loans.detail.noSchedule')}</Text>
      ) : (
        schedule.map((row) => {
          const status = installmentStatus(row);
          return (
            <View
              key={row.id}
              accessible
              accessibilityLabel={[
                t('account.loans.detail.installmentNo', { n: row.installmentNumber }),
                f.date(row.dueDate, 'medium'),
                money(row.amountDue, loan?.currency),
                t(`account.loans.installmentStatus.${status.key}`),
              ].join(', ')}
              style={[styles.scheduleRow, { borderBottomColor: theme.colors.hairline }]}
            >
              <View style={styles.flex}>
                <Text style={[theme.typography.subheadline, { color: theme.colors.text }]}>
                  {t('account.loans.detail.installmentNo', { n: row.installmentNumber })}
                </Text>
                <Text style={[theme.typography.footnote, { color: theme.colors.textSecondary }]}>
                  {status.key === 'paid' && row.paidDate
                    ? t('account.loans.detail.paidOn', { date: f.date(row.paidDate, 'medium') })
                    : t('account.loans.detail.dueOn', { date: f.date(row.dueDate, 'medium') })}
                </Text>
              </View>
              <Text style={[theme.typography.subheadline, styles.tabular, { color: theme.colors.text }]}>
                {money(row.amountDue, loan?.currency)}
              </Text>
              <StatusPill tone={status.tone} label={t(`account.loans.installmentStatus.${status.key}`)} />
            </View>
          );
        })
      )}
    </BottomSheet>
  );
}

// ---------------------------------------------------------------------------------------------

function ApplySheet({
  type,
  name,
  check,
  money,
  reasonText,
  onClose,
  onSent,
}: {
  type: OfferedLoanType;
  name: string;
  check: TypeCheck | undefined;
  money: (amount: number | null | undefined, currency: string | null | undefined) => string;
  reasonText: (reason: Reason) => string;
  onClose: () => void;
  onSent: () => void;
}) {
  const { t } = useTranslation();
  const { theme } = useTheme();
  const eligibility = check?.eligibility ?? null;
  const available = eligibility ? eligibility.available ?? eligibility.maxAvailableAmount : null;
  const currency = eligibility?.currency ?? null;
  const maxInstallments = check?.maxInstallments ?? null;
  const [amountText, setAmountText] = useState('');
  const [installments, setInstallments] = useState(() => Math.min(12, maxInstallments ?? 12));
  const [notes, setNotes] = useState('');
  const [touched, setTouched] = useState(false);
  const [submitting, setSubmitting] = useState(false);
  const [refusal, setRefusal] = useState<Reason[] | null>(null);
  const [failed, setFailed] = useState(false);

  const result = validateApplication({ amountText, installments }, { available, maxInstallments });
  const estimate = estimatedInstallment(result.amount, installments);
  const max = maxInstallments ?? 600;

  const amountError = touched && result.errors.amount
    ? t(`account.loans.form.errors.${result.errors.amount}`, { amount: money(available, currency) })
    : undefined;

  const submit = async () => {
    setTouched(true);
    if (!result.ok || result.amount === null || submitting) {
      haptics.error();
      return;
    }
    setSubmitting(true);
    setRefusal(null);
    setFailed(false);
    haptics.commit();
    try {
      await loansApi.apply({
        loanTypeId: type.loanTypeId,
        amount: result.amount,
        installments,
        repaymentMethod: check?.method ?? 'BankTransfer',
        notes,
      });
      haptics.success();
      onSent();
    } catch (error) {
      haptics.error();
      const data = (error as { response?: { data?: any } })?.response?.data;
      const reasons: unknown = data?.reasons ?? data?.Reasons;
      const codes: unknown = data?.codes ?? data?.Codes;
      if (Array.isArray(reasons) && reasons.length > 0) {
        setRefusal(reasons.map((text, i) => ({
          code: Array.isArray(codes) && typeof codes[i] === 'string' ? codes[i] : null,
          text: String(text),
        })));
      } else {
        setFailed(true);
      }
    } finally {
      setSubmitting(false);
    }
  };

  const step = (delta: number) => setInstallments((n) => Math.min(max, Math.max(1, n + delta)));

  return (
    <BottomSheet
      visible
      onClose={onClose}
      title={t('account.loans.form.title', { type: name })}
      subtitle={available !== null ? t('account.loans.upTo', { amount: money(available, currency) }) : undefined}
      actions={
        <LiquidButton
          label={t('account.loans.form.send')}
          icon="paper-plane-outline"
          onPress={() => void submit()}
          loading={submitting}
          disabled={submitting}
          testID="loan-apply-send"
        />
      }
    >
      <GlassTextField
        label={currency ? t('account.loans.form.amountIn', { currency }) : t('account.loans.form.amount')}
        icon="cash-outline"
        value={amountText}
        onChangeText={setAmountText}
        onBlur={() => setTouched(true)}
        keyboardType="decimal-pad"
        inputMode="decimal"
        placeholder={available !== null ? f2(available) : undefined}
        error={amountError}
        containerStyle={styles.field}
      />

      <Text style={[theme.typography.subheadline, styles.label, { color: theme.colors.text }]}>{t('account.loans.form.installments')}</Text>
      <View style={styles.stepper}>
        <MotionPressable
          accessibilityRole="button"
          accessibilityLabel={t('account.loans.form.fewer')}
          disabled={installments <= 1}
          haptic="selection"
          onPress={() => step(-1)}
          contentStyle={[styles.stepButton, { backgroundColor: theme.colors.surfaceInset }]}
        >
          <Ionicons accessible={false} name="remove" size={22} color={theme.colors.accent} />
        </MotionPressable>
        <View
          accessible
          accessibilityRole="adjustable"
          accessibilityLabel={t('account.loans.form.installments')}
          accessibilityValue={{ text: t('account.loans.form.installmentsValue', { count: installments }) }}
          accessibilityActions={[{ name: 'increment' }, { name: 'decrement' }]}
          onAccessibilityAction={(e) => step(e.nativeEvent.actionName === 'increment' ? 1 : -1)}
          style={styles.stepValue}
        >
          <Text style={[theme.typography.title2, styles.tabular, { color: theme.colors.text }]}>{installments}</Text>
        </View>
        <MotionPressable
          accessibilityRole="button"
          accessibilityLabel={t('account.loans.form.more')}
          disabled={installments >= max}
          haptic="selection"
          onPress={() => step(1)}
          contentStyle={[styles.stepButton, { backgroundColor: theme.colors.surfaceInset }]}
        >
          <Ionicons accessible={false} name="add" size={22} color={theme.colors.accent} />
        </MotionPressable>
      </View>
      {maxInstallments !== null ? (
        <Text style={[theme.typography.footnote, styles.center, { color: theme.colors.textMuted }]}>
          {t('account.loans.maxInstallments', { count: maxInstallments })}
        </Text>
      ) : null}

      <Surface level={0} contentStyle={styles.estimate}>
        <Text style={[theme.typography.footnote, { color: theme.colors.textSecondary }]}>{t('account.loans.form.estimateLabel')}</Text>
        <Text style={[theme.typography.headline, styles.tabular, { color: theme.colors.text }]}>
          {estimate !== null ? t('account.loans.form.estimate', { amount: money(estimate, currency) }) : '—'}
        </Text>
        <Text style={[theme.typography.footnote, { color: theme.colors.textMuted }]}>
          {check?.method === 'PayrollDeduction'
            ? t('account.loans.form.byPayroll')
            : check?.method === 'BankTransfer'
              ? t('account.loans.form.byBank')
              : t('account.loans.form.byPolicy')}
        </Text>
      </Surface>

      <GlassTextField
        label={t('account.loans.form.notes')}
        icon="create-outline"
        value={notes}
        onChangeText={setNotes}
        maxLength={NOTES_MAX}
        multiline
        textAlignVertical="top"
        placeholder={t('account.loans.form.notesPlaceholder')}
        containerStyle={styles.field}
      />

      {refusal ? (
        <Surface tintColor={theme.colors.status.warning.tint} contentStyle={styles.refusal}>
          <Text accessibilityRole="alert" style={[theme.typography.subheadline, { color: theme.colors.text, fontWeight: '600' }]}>
            {t('account.loans.form.refused')}
          </Text>
          {refusal.map((reason, i) => (
            <Text key={i} style={[theme.typography.subheadline, { color: theme.colors.text }]}>{`• ${reasonText(reason)}`}</Text>
          ))}
        </Surface>
      ) : null}
      {failed ? (
        <Text accessibilityRole="alert" style={[theme.typography.footnote, { color: theme.colors.status.danger.fg }]}>
          {t('account.loans.form.failed')}
        </Text>
      ) : null}
    </BottomSheet>
  );
}

/** Placeholder digits for the amount field (no grouping, so it can be typed as shown). */
function f2(value: number): string {
  return String(Math.floor(value));
}

const styles = StyleSheet.create({
  root: { flex: 1 },
  content: { paddingBottom: 24 },
  list: { paddingHorizontal: 16, gap: 12 },
  state: { marginHorizontal: 16 },
  sectionGap: { marginTop: 8 },
  card: { padding: 16, gap: 10 },
  rowTop: { flexDirection: 'row', alignItems: 'flex-start', gap: 12 },
  flex: { flex: 1, minWidth: 0 },
  figure: { fontVariant: ['tabular-nums'], marginVertical: 2 },
  tabular: { fontVariant: ['tabular-nums'] },
  track: { height: 8, borderRadius: 4, overflow: 'hidden' },
  fill: { height: 8, borderRadius: 4 },
  next: { flexDirection: 'row', alignItems: 'center', gap: 12, padding: 12, borderRadius: 14 },
  more: { flexDirection: 'row', alignItems: 'center', gap: 4, alignSelf: 'flex-end', minHeight: 24 },
  skeletons: { gap: 8 },
  reasons: { gap: 8 },
  reasonRow: { flexDirection: 'row', alignItems: 'flex-start', gap: 8 },
  applyButton: { alignSelf: 'flex-start', minWidth: 140 },
  notice: { flexDirection: 'row', alignItems: 'center', gap: 10, padding: 14 },
  pastRow: { flexDirection: 'row', alignItems: 'center', gap: 12, padding: 14, minHeight: 64 },
  detailTable: { padding: 14, gap: 10 },
  detailRow: { flexDirection: 'row', alignItems: 'center', gap: 12 },
  scheduleRow: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: 10,
    paddingVertical: 10,
    borderBottomWidth: StyleSheet.hairlineWidth,
  },
  field: { marginBottom: 0 },
  label: { fontWeight: '600' },
  stepper: { flexDirection: 'row', alignItems: 'center', gap: 12 },
  stepButton: { width: 52, height: 52, borderRadius: 16, alignItems: 'center', justifyContent: 'center' },
  stepValue: { flex: 1, alignItems: 'center' },
  center: { textAlign: 'center' },
  estimate: { padding: 14, gap: 2 },
  refusal: { padding: 14, gap: 6 },
});
