import React, { useCallback, useEffect, useState } from 'react';
import { RefreshControl, ScrollView, StyleSheet, Text, View } from 'react-native';
import { useNavigation } from '@react-navigation/native';
import { useTranslation } from 'react-i18next';
import {
  DirectionalIcon,
  LiquidBackdrop,
  MotionPressable,
  ScreenHero,
  SectionHeader,
  Skeleton,
  SkeletonRow,
  StateCard,
  StatusPill,
  Surface,
} from '@/components/ui';
import { useTheme } from '@/theme/ThemeProvider';
import { useFormat } from '@/utils/format';
import { payslipApi } from '@/api/adapters';
import { deductionsApi, type DebtBalance } from '@/api/deductions';
import type { Payslip } from '@/types';
import { useLeavePayText } from '@/features/leave/useLeavePayText';
import { payslipStatusKind } from './payslipLogic';
import { useHijriPeriod, usePayslipPeriod } from './usePayslipPeriod';
import { useSensitiveScreen } from '@/safety/useSensitiveScreen';

export default function PayslipsScreen() {
  useSensitiveScreen('payslips');
  const navigation = useNavigation<any>();
  const { theme } = useTheme();
  const { t } = useTranslation();
  const f = useFormat();
  const text = useLeavePayText();
  const period = usePayslipPeriod();
  const hijri = useHijriPeriod();

  const [payslips, setPayslips] = useState<Payslip[]>([]);
  const [debts, setDebts] = useState<DebtBalance[]>([]);
  const [loading, setLoading] = useState(true);
  const [refreshing, setRefreshing] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(
    async (refresh = false) => {
      if (refresh) setRefreshing(true);
      else setLoading(true);
      setError(null);
      try {
        const [list, deductions] = await Promise.all([
          payslipApi.getList(),
          // Release A: loans and advances being repaid. Off for the tenant → nothing shown.
          deductionsApi.mine(6).catch(() => ({ available: false as const })),
        ]);
        setPayslips(list.items);
        setDebts(deductions.available ? [...deductions.data.loans, ...deductions.data.advances].filter((d) => d.outstanding > 0) : []);
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
  }, [load]);

  const open = (p: Payslip) => navigation.navigate('PayslipDetail', { id: p.id });
  const [latest, ...earlier] = payslips;

  let body: React.ReactNode;
  if (loading) {
    body = (
      <>
        <Surface level={2} contentStyle={styles.hero}>
          <Skeleton width="40%" height={14} />
          <Skeleton width="55%" height={34} />
          <Skeleton width="70%" height={14} />
        </Surface>
        <View>
          <SkeletonRow />
          <SkeletonRow />
        </View>
      </>
    );
  } else if (error) {
    body = <StateCard variant="error" body={error} onRetry={() => void load()} />;
  } else if (!latest) {
    body = <StateCard variant="empty" title={t('leavePay.payslips.emptyTitle')} body={t('leavePay.payslips.emptyBody')} />;
  } else {
    const net = latest.netPay ?? latest.netSalary;
    const gross = latest.grossPay ?? latest.grossSalary;
    const statusKind = payslipStatusKind(latest.status);
    const latestHijri = hijri(latest.year, latest.month);
    body = (
      <>
        <MotionPressable
          testID="payslip-latest"
          accessibilityRole="button"
          accessibilityLabel={`${t('leavePay.payslips.latest')}, ${t('leavePay.payslips.a11yRow', { period: period(latest), amount: f.money(net, latest.currency || undefined) })}`}
          accessibilityHint={t('leavePay.payslips.open')}
          onPress={() => open(latest)}
        >
          <Surface level={2} contentStyle={styles.hero}>
            <View style={styles.heroHead}>
              <View style={styles.flex}>
                <Text style={[theme.typography.footnote, styles.strong, { color: theme.colors.accent }]}>
                  {t('leavePay.payslips.latest')}
                </Text>
                <Text style={[theme.typography.title3, { color: theme.colors.text }]}>{period(latest)}</Text>
                {latestHijri ? (
                  <Text style={[theme.typography.footnote, { color: theme.colors.textMuted }]}>{latestHijri}</Text>
                ) : null}
              </View>
              <DirectionalIcon name="chevron" size={20} color={theme.colors.textMuted} />
            </View>
            <View>
              <Text style={[theme.typography.footnote, { color: theme.colors.textSecondary }]}>{t('leavePay.payslips.netPay')}</Text>
              <Text style={[theme.typography.largeTitle, styles.amount, { color: theme.colors.text }]}>
                {f.money(net, latest.currency || undefined)}
              </Text>
            </View>
            <View style={[styles.heroFigures, { borderTopColor: theme.colors.hairline }]}>
              <Figure label={t('leavePay.payslips.gross')} value={f.money(gross, latest.currency || undefined)} />
              <Figure label={t('leavePay.payslips.deductions')} value={f.money(latest.totalDeductions, latest.currency || undefined)} />
            </View>
            <View style={styles.heroFoot}>
              <StatusPill
                tone={statusKind === 'paid' ? 'success' : 'info'}
                label={t(`leavePay.payslips.status.${statusKind}`)}
              />
              {latest.paymentDate ? (
                <Text style={[theme.typography.footnote, { color: theme.colors.textSecondary }]}>
                  {t('leavePay.payslips.paidOn', { date: f.date(latest.paymentDate, 'medium') })}
                </Text>
              ) : null}
            </View>
          </Surface>
        </MotionPressable>

        {debts.length > 0 ? (
          <View>
            <SectionHeader title={t('leavePay.payslips.debts.title')} subtitle={t('leavePay.payslips.debts.subtitle')} />
            <Surface level={1} contentStyle={styles.rows}>
              {debts.map((d, i) => (
                <View
                  key={d.id}
                  accessible
                  style={[styles.debt, i < debts.length - 1 && { borderBottomWidth: StyleSheet.hairlineWidth, borderBottomColor: theme.colors.hairline }]}
                >
                  <View style={styles.flex}>
                    <Text style={[theme.typography.subheadline, styles.strong, { color: theme.colors.text }]}>
                      {text.name(d.type, d.typeAr)} · {d.loanNo}
                    </Text>
                    <Text style={[theme.typography.footnote, { color: theme.colors.textSecondary }]}>
                      {[
                        t('leavePay.payslips.debts.instalment', { amount: f.money(d.instalment, d.currency || undefined) }),
                        d.remaining !== null ? text.count('instalmentsLeft', d.remaining) : null,
                        d.nextDueOn ? t('leavePay.payslips.debts.nextDue', { date: f.date(d.nextDueOn, 'medium') }) : null,
                      ]
                        .filter(Boolean)
                        .join(' · ')}
                    </Text>
                  </View>
                  <Text style={[theme.typography.subheadline, styles.strong, styles.tabular, { color: theme.colors.text }]}>
                    {t('leavePay.payslips.debts.outstanding', { amount: f.money(d.outstanding, d.currency || undefined) })}
                  </Text>
                </View>
              ))}
            </Surface>
          </View>
        ) : null}

        {earlier.length > 0 ? (
          <View>
            <SectionHeader title={t('leavePay.payslips.earlier')} />
            <Surface level={1} contentStyle={styles.rows}>
              {earlier.map((p, i) => {
                const amount = f.money(p.netPay ?? p.netSalary, p.currency || undefined);
                return (
                  <MotionPressable
                    key={p.id}
                    testID={`payslip-row-${p.id}`}
                    accessibilityRole="button"
                    accessibilityLabel={t('leavePay.payslips.a11yRow', { period: period(p), amount })}
                    accessibilityHint={t('leavePay.payslips.open')}
                    onPress={() => open(p)}
                    contentStyle={[
                      styles.row,
                      i < earlier.length - 1 && { borderBottomWidth: StyleSheet.hairlineWidth, borderBottomColor: theme.colors.hairline },
                    ]}
                  >
                    <View style={styles.flex}>
                      <Text style={[theme.typography.subheadline, styles.strong, { color: theme.colors.text }]}>{period(p)}</Text>
                      {p.paymentDate ? (
                        <Text style={[theme.typography.footnote, { color: theme.colors.textMuted }]}>
                          {t('leavePay.payslips.paidOn', { date: f.date(p.paymentDate, 'medium') })}
                        </Text>
                      ) : null}
                    </View>
                    <Text style={[theme.typography.subheadline, styles.tabular, { color: theme.colors.text }]}>{amount}</Text>
                    <DirectionalIcon name="chevron" size={16} color={theme.colors.textMuted} />
                  </MotionPressable>
                );
              })}
            </Surface>
          </View>
        ) : null}
      </>
    );
  }

  return (
    <View style={[styles.root, { backgroundColor: theme.colors.canvas }]}>
      <LiquidBackdrop subtle />
      <ScrollView
        contentContainerStyle={styles.content}
        refreshControl={<RefreshControl refreshing={refreshing} onRefresh={() => void load(true)} tintColor={theme.colors.accent} />}
        showsVerticalScrollIndicator={false}
      >
        <ScreenHero eyebrow={t('leavePay.payslips.eyebrow')} title={t('leavePay.payslips.title')} />
        <View style={styles.body}>{body}</View>
      </ScrollView>
    </View>
  );
}

function Figure({ label, value }: { label: string; value: string }) {
  const { theme } = useTheme();
  return (
    <View style={styles.figure}>
      <Text style={[theme.typography.footnote, { color: theme.colors.textSecondary }]}>{label}</Text>
      <Text style={[theme.typography.subheadline, styles.strong, styles.tabular, { color: theme.colors.text }]}>{value}</Text>
    </View>
  );
}

const styles = StyleSheet.create({
  root: { flex: 1 },
  content: { paddingBottom: 28 },
  body: { paddingHorizontal: 16, gap: 18 },
  hero: { padding: 18, gap: 14 },
  heroHead: { flexDirection: 'row', alignItems: 'flex-start', gap: 10 },
  heroFigures: { flexDirection: 'row', flexWrap: 'wrap', gap: 16, paddingTop: 12, borderTopWidth: StyleSheet.hairlineWidth },
  heroFoot: { flexDirection: 'row', alignItems: 'center', flexWrap: 'wrap', gap: 10 },
  figure: { flexGrow: 1, flexBasis: 120, gap: 2 },
  amount: { fontVariant: ['tabular-nums'] },
  rows: { paddingHorizontal: 14 },
  row: { minHeight: 56, flexDirection: 'row', alignItems: 'center', gap: 10, paddingVertical: 10 },
  debt: { flexDirection: 'row', alignItems: 'center', gap: 10, paddingVertical: 12 },
  strong: { fontWeight: '600' },
  tabular: { fontVariant: ['tabular-nums'] },
  flex: { flex: 1, minWidth: 0 },
});
