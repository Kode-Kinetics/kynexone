import React, { useCallback, useEffect, useState } from 'react';
import { ScrollView, StyleSheet, Text, View } from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import { useNavigation, useRoute } from '@react-navigation/native';
import * as Sharing from 'expo-sharing';
import { useTranslation } from 'react-i18next';
import { withSensitiveDownload } from '@/security/sensitiveFiles';
import {
  DirectionalIcon,
  LiquidBackdrop,
  LiquidButton,
  MotionPressable,
  ScreenHero,
  Skeleton,
  SkeletonCard,
  StateCard,
  StatusPill,
  StickyFooter,
  Surface,
} from '@/components/ui';
import { useTheme } from '@/theme/ThemeProvider';
import { useFormat } from '@/utils/format';
import { haptics } from '@/utils/haptics';
import { payslipApi } from '@/api/adapters';
import type { PayslipView } from '@/api/services';
import { deductionsApi, OTHER_RUNS_CODE, type DeductionStatement } from '@/api/deductions';
import type { Payslip, PayslipLine } from '@/types';
import { DetailRow, Notice } from '@/features/leave/LeavePayParts';
import { useLeavePayText } from '@/features/leave/useLeavePayText';
import { errorShape } from '@/features/leave/leavePayText';
import {
  adjacentPayslips,
  deductionCategory,
  payslipCheck,
  payslipStatusKind,
  sumLines,
  unitemisedAmount,
} from './payslipLogic';
import { useHijriPeriod, usePayslipPeriod } from './usePayslipPeriod';

type DeductionSource = { kind: 'loading' } | { kind: 'statement'; data: DeductionStatement } | { kind: 'basic' };

export default function PayslipDetailScreen() {
  const route = useRoute<any>();
  const navigation = useNavigation<any>();
  const { theme } = useTheme();
  const { t } = useTranslation();
  const text = useLeavePayText();
  const period = usePayslipPeriod();
  const hijri = useHijriPeriod();
  const id: string = route.params?.id;

  const [payslip, setPayslip] = useState<PayslipView | null>(null);
  const [list, setList] = useState<Payslip[]>([]);
  const [deductions, setDeductions] = useState<DeductionSource>({ kind: 'loading' });
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [downloading, setDownloading] = useState(false);
  const [downloadError, setDownloadError] = useState<string | null>(null);

  const load = useCallback(async () => {
    setLoading(true);
    setError(null);
    setDownloadError(null);
    setDeductions({ kind: 'loading' });
    try {
      setPayslip(await payslipApi.getDetail(id));
    } catch (e) {
      setPayslip(null);
      setError(errorShape(e).status === 404 ? t('leavePay.payslip.notFoundBody') : text.error(e));
    } finally {
      setLoading(false);
    }
    // Release A itemised deductions; off for the tenant (403/404) or failing → the payslip's own lines.
    deductionsApi
      .forPayslip(id)
      .then((r) => setDeductions(r.available ? { kind: 'statement', data: r.data } : { kind: 'basic' }))
      .catch(() => setDeductions({ kind: 'basic' }));
  }, [id, t, text]);

  useEffect(() => {
    void load();
  }, [load]);

  useEffect(() => {
    let active = true;
    payslipApi
      .getList()
      .then((r) => active && setList(r.items))
      .catch(() => undefined);
    return () => {
      active = false;
    };
  }, []);

  const downloadPdf = async () => {
    setDownloading(true);
    setDownloadError(null);
    try {
      if (!(await Sharing.isAvailableAsync())) {
        setDownloadError(t('leavePay.payslip.shareUnavailable'));
        return;
      }
      const { url, headers } = await payslipApi.download(id);
      await withSensitiveDownload(
        { url, headers, fileExtension: 'pdf' },
        async (uri) => {
          await Sharing.shareAsync(uri, { mimeType: 'application/pdf', UTI: 'com.adobe.pdf' });
        },
      );
    } catch {
      haptics.error();
      setDownloadError(t('leavePay.payslip.downloadFailed'));
    } finally {
      setDownloading(false);
    }
  };

  const { newer, older } = adjacentPayslips(list, id);
  const listed = list.find((p) => p.id === id);
  const title = payslip ? period(payslip) : listed ? period(listed) : t('leavePay.payslip.eyebrow');
  const hijriLine = payslip ? hijri(payslip.year, payslip.month) : '';

  const switcher =
    newer || older ? (
      <View style={styles.switcher}>
        {older ? (
          <MonthButton
            direction="back"
            label={t('leavePay.payslip.older')}
            period={period(older)}
            onPress={() => navigation.setParams({ id: older.id })}
          />
        ) : (
          <View style={styles.flex} />
        )}
        {newer ? (
          <MonthButton
            direction="forward"
            label={t('leavePay.payslip.newer')}
            period={period(newer)}
            onPress={() => navigation.setParams({ id: newer.id })}
          />
        ) : (
          <View style={styles.flex} />
        )}
      </View>
    ) : null;

  let body: React.ReactNode;
  if (loading) {
    body = (
      <>
        <Surface level={2} contentStyle={styles.hero}>
          <Skeleton width="30%" height={14} />
          <Skeleton width="60%" height={36} />
        </Surface>
        <SkeletonCard lines={3} />
        <SkeletonCard lines={4} />
      </>
    );
  } else if (error || !payslip) {
    body = <StateCard variant="error" body={error ?? undefined} onRetry={() => void load()} />;
  } else {
    body = <PayslipBody payslip={payslip} deductions={deductions} />;
  }

  return (
    <View style={[styles.root, { backgroundColor: theme.colors.canvas }]}>
      <LiquidBackdrop subtle />
      <ScrollView contentContainerStyle={styles.content} showsVerticalScrollIndicator={false}>
        <ScreenHero eyebrow={t('leavePay.payslip.eyebrow')} title={title} subtitle={hijriLine || undefined}>
          {switcher}
        </ScreenHero>
        <View style={styles.body}>
          {body}
          {downloadError ? <Notice tone="danger" body={downloadError} /> : null}
        </View>
      </ScrollView>
      {payslip && !loading ? (
        <StickyFooter>
          <LiquidButton
            testID="payslip-download"
            variant="secondary"
            icon="download-outline"
            label={t('leavePay.payslip.download')}
            onPress={() => void downloadPdf()}
            loading={downloading}
          />
        </StickyFooter>
      ) : null}
    </View>
  );
}

function PayslipBody({ payslip, deductions }: { payslip: PayslipView; deductions: DeductionSource }) {
  const { theme } = useTheme();
  const { t } = useTranslation();
  const f = useFormat();
  const text = useLeavePayText();
  const currency = payslip.currency || undefined;
  const money = (n: number | null | undefined) => f.money(n, currency);
  const gross = payslip.grossPay ?? payslip.grossSalary;
  const net = payslip.netPay ?? payslip.netSalary;
  const check = payslipCheck(gross, payslip.totalDeductions, net);
  const statusKind = payslipStatusKind(payslip.status);
  const lineName = (l: PayslipLine) => text.name(l.description || l.componentName, l.descriptionAr);

  return (
    <>
      {/* Net pay */}
      <Surface level={2} contentStyle={styles.hero} testID="payslip-net">
        <Text style={[theme.typography.footnote, { color: theme.colors.textSecondary }]}>{t('leavePay.payslips.netPay')}</Text>
        <Text style={[theme.typography.largeTitle, styles.tabular, { color: theme.colors.text }]}>{money(net)}</Text>
        <View style={styles.heroFoot}>
          <StatusPill tone={statusKind === 'paid' ? 'success' : 'info'} label={t(`leavePay.payslips.status.${statusKind}`)} />
          {payslip.paymentDate ? (
            <Text style={[theme.typography.footnote, { color: theme.colors.textSecondary }]}>
              {t('leavePay.payslips.paidOn', { date: f.date(payslip.paymentDate, 'medium') })}
            </Text>
          ) : null}
        </View>
      </Surface>

      {/* Gross − Deductions = Net */}
      <Surface level={1} contentStyle={styles.card} testID="payslip-check">
        <Text style={[theme.typography.headline, { color: theme.colors.text }]}>{t('leavePay.payslip.check.title')}</Text>
        <CheckRow sign="" label={t('leavePay.payslip.check.gross')} value={money(gross)} />
        <CheckRow sign="−" label={t('leavePay.payslip.check.deductions')} value={money(payslip.totalDeductions)} />
        <View style={[styles.rule, { backgroundColor: theme.colors.hairline }]} />
        <CheckRow sign="=" label={t('leavePay.payslip.check.net')} value={money(net)} strong />
        {check.matches ? (
          <View style={styles.matches}>
            <Ionicons accessible={false} name="checkmark-circle" size={16} color={theme.colors.status.success.fg} />
            <Text style={[theme.typography.footnote, { color: theme.colors.status.success.fg }]}>{t('leavePay.payslip.check.matches')}</Text>
          </View>
        ) : (
          <Notice tone="warning" body={t('leavePay.payslip.check.mismatch', { amount: money(Math.abs(check.difference)) })} />
        )}
        {payslip.reconciled === false ? <Notice tone="warning" body={t('leavePay.payslip.check.linesMismatch')} /> : null}
      </Surface>

      {/* Earnings */}
      {payslip.earnings.length > 0 ? (
        <Collapsible title={t('leavePay.payslip.earnings')} total={money(gross)} initiallyOpen testID="payslip-earnings">
          {payslip.earnings.map((l, i) => (
            <DetailRow key={l.id ?? `e${i}`} label={lineName(l)} value={money(l.amount)} />
          ))}
          <DetailRow label={t('leavePay.payslip.earningsTotal')} value={money(gross)} strong />
        </Collapsible>
      ) : null}

      {/* Deductions */}
      <Collapsible title={t('leavePay.payslip.deductionsTitle')} total={money(payslip.totalDeductions)} initiallyOpen testID="payslip-deductions">
        <DeductionLines payslip={payslip} source={deductions} money={money} lineName={lineName} />
      </Collapsible>

      {/* Employer contributions — on top of pay, never deducted */}
      {payslip.employerLines.length > 0 ? (
        <Collapsible title={t('leavePay.payslip.employer')} total={money(sumLines(payslip.employerLines))} testID="payslip-employer">
          <Text style={[theme.typography.footnote, styles.note, { color: theme.colors.textMuted }]}>{t('leavePay.payslip.employerNote')}</Text>
          {payslip.employerLines.map((l, i) => (
            <DetailRow key={l.id ?? `c${i}`} label={lineName(l)} value={money(l.amount)} />
          ))}
        </Collapsible>
      ) : null}

      {/* Details */}
      {payslip.periodEnd || payslip.ytdGross !== undefined || payslip.ytdNet !== undefined || payslip.paidDays !== undefined || payslip.wpsReferenceNumber ? (
        <Surface level={1} contentStyle={styles.card}>
          <Text style={[theme.typography.headline, { color: theme.colors.text }]}>{t('leavePay.payslip.details')}</Text>
          {payslip.periodStart && payslip.periodEnd ? (
            <DetailRow
              label={t('leavePay.payslip.period')}
              value={t('leavePay.common.dateRange', { start: f.date(payslip.periodStart, 'medium'), end: f.date(payslip.periodEnd, 'medium') })}
            />
          ) : null}
          {payslip.ytdGross !== undefined ? <DetailRow label={t('leavePay.payslip.ytdGross')} value={money(payslip.ytdGross)} /> : null}
          {payslip.ytdNet !== undefined ? <DetailRow label={t('leavePay.payslip.ytdNet')} value={money(payslip.ytdNet)} /> : null}
          {payslip.workingDays !== undefined ? <DetailRow label={t('leavePay.payslip.workingDays')} value={f.number(payslip.workingDays)} /> : null}
          {payslip.paidDays !== undefined ? <DetailRow label={t('leavePay.payslip.paidDays')} value={f.number(payslip.paidDays)} /> : null}
          {payslip.wpsReferenceNumber ? <DetailRow label={t('leavePay.payslip.wps')} value={payslip.wpsReferenceNumber} selectable /> : null}
        </Surface>
      ) : null}
    </>
  );
}

function DeductionLines({
  payslip,
  source,
  money,
  lineName,
}: {
  payslip: PayslipView;
  source: DeductionSource;
  money: (n: number | null | undefined) => string;
  lineName: (l: PayslipLine) => string;
}) {
  const { theme } = useTheme();
  const { t } = useTranslation();
  const text = useLeavePayText();

  if (source.kind === 'loading') {
    return (
      <View style={styles.loadingLines}>
        <Skeleton width="80%" height={14} />
        <Skeleton width="60%" height={14} />
      </View>
    );
  }

  if (source.kind === 'statement') {
    const own = source.data.lines.filter((l) => l.componentCode !== OTHER_RUNS_CODE);
    const gap = unitemisedAmount(payslip.totalDeductions, own);
    return (
      <>
        {own.length === 0 ? (
          <Text style={[theme.typography.subheadline, styles.note, { color: theme.colors.textSecondary }]}>{t('leavePay.payslip.noDeductions')}</Text>
        ) : (
          own.map((l, i) => {
            const category = deductionCategory(l.category);
            const isDebt = category === 'EmployerLoan' || category === 'SalaryAdvance';
            const label = isDebt && (l.loanType || l.loanNumber)
              ? [text.name(l.loanType, l.loanTypeAr), l.loanNumber].filter(Boolean).join(' · ')
              : l.label;
            const sub = [
              t(`leavePay.payslip.category.${category}`),
              isDebt && l.balanceAfter !== null ? t('leavePay.payslip.loanLeft', { amount: money(l.balanceAfter) }) : null,
              isDebt && l.instalmentsRemaining !== null ? text.count('instalmentsLeft', l.instalmentsRemaining) : null,
            ]
              .filter(Boolean)
              .join(' · ');
            return <DetailRow key={`${l.componentCode}-${i}`} label={label} sublabel={sub} value={money(l.amount)} />;
          })
        )}
        <DetailRow label={t('leavePay.payslip.deductionsTotal')} value={money(payslip.totalDeductions)} strong />
        {gap ? <Notice tone="warning" body={t('leavePay.payslip.unitemised', { amount: money(gap) })} /> : null}
        {!source.data.reconciles && !gap ? <Notice tone="warning" body={t('leavePay.payslip.check.linesMismatch')} /> : null}
        {source.data.reasons.map((r) => (
          <Notice
            key={r.code}
            tone="warning"
            title={text.name(r.titleEn, r.titleAr)}
            body={[text.name(r.whyEn, r.whyAr), text.name(r.fixEn, r.fixAr)].filter(Boolean).join(' ')}
          />
        ))}
      </>
    );
  }

  // Fallback: the payslip's own deduction lines only. What they don't explain is flagged, never invented.
  const gap = unitemisedAmount(payslip.totalDeductions, payslip.deductions);
  return (
    <>
      {payslip.deductions.length === 0 && payslip.totalDeductions === 0 ? (
        <Text style={[theme.typography.subheadline, styles.note, { color: theme.colors.textSecondary }]}>{t('leavePay.payslip.noDeductions')}</Text>
      ) : (
        payslip.deductions.map((l, i) => <DetailRow key={l.id ?? `d${i}`} label={lineName(l)} value={money(l.amount)} />)
      )}
      {payslip.deductions.length > 0 || payslip.totalDeductions !== 0 ? (
        <DetailRow label={t('leavePay.payslip.deductionsTotal')} value={money(payslip.totalDeductions)} strong />
      ) : null}
      {gap ? <Notice tone="warning" body={t('leavePay.payslip.unitemised', { amount: money(gap) })} /> : null}
      {payslip.totalDeductions !== 0 ? (
        <Text style={[theme.typography.footnote, styles.note, { color: theme.colors.textMuted }]}>{t('leavePay.payslip.deductionsBasic')}</Text>
      ) : null}
    </>
  );
}

function CheckRow({ sign, label, value, strong }: { sign: string; label: string; value: string; strong?: boolean }) {
  const { theme } = useTheme();
  const type = strong ? theme.typography.headline : theme.typography.subheadline;
  return (
    <View style={styles.checkRow} accessible accessibilityLabel={`${sign} ${label} ${value}`.trim()}>
      <Text style={[type, styles.sign, { color: theme.colors.textMuted }]}>{sign}</Text>
      <Text style={[type, styles.flex, { color: theme.colors.text }]}>{label}</Text>
      <Text style={[type, styles.tabular, { color: theme.colors.text }]}>{value}</Text>
    </View>
  );
}

function Collapsible({
  title,
  total,
  initiallyOpen = false,
  children,
  testID,
}: {
  title: string;
  total: string;
  initiallyOpen?: boolean;
  children: React.ReactNode;
  testID?: string;
}) {
  const { theme } = useTheme();
  const { t } = useTranslation();
  const [open, setOpen] = useState(initiallyOpen);
  return (
    <Surface level={1} contentStyle={styles.collapsible} testID={testID}>
      <MotionPressable
        accessibilityRole="button"
        accessibilityState={{ expanded: open }}
        accessibilityLabel={`${title}, ${total}`}
        accessibilityHint={open ? t('leavePay.payslip.hide') : t('leavePay.payslip.show')}
        haptic="selection"
        onPress={() => setOpen((v) => !v)}
        contentStyle={styles.collapsibleHead}
      >
        <Text style={[theme.typography.headline, styles.flex, { color: theme.colors.text }]}>{title}</Text>
        <Text style={[theme.typography.headline, styles.tabular, { color: theme.colors.text }]}>{total}</Text>
        <Ionicons accessible={false} name={open ? 'chevron-up' : 'chevron-down'} size={18} color={theme.colors.textMuted} />
      </MotionPressable>
      {open ? <View style={styles.collapsibleBody}>{children}</View> : null}
    </Surface>
  );
}

function MonthButton({
  direction,
  label,
  period,
  onPress,
}: {
  direction: 'back' | 'forward';
  label: string;
  period: string;
  onPress: () => void;
}) {
  const { theme } = useTheme();
  const { t } = useTranslation();
  const back = direction === 'back';
  return (
    <MotionPressable
      accessibilityRole="button"
      accessibilityLabel={t('leavePay.payslip.switchTo', { period })}
      haptic="selection"
      onPress={onPress}
      style={styles.flex}
      contentStyle={[styles.monthButton, !back && styles.monthButtonEnd]}
    >
      {back ? <DirectionalIcon name="chevronBack" size={18} color={theme.colors.accent} /> : null}
      <View style={!back && styles.alignEnd}>
        <Text style={[theme.typography.footnote, { color: theme.colors.textMuted }]}>{label}</Text>
        <Text numberOfLines={1} style={[theme.typography.subheadline, { color: theme.colors.accent, fontWeight: '600' }]}>{period}</Text>
      </View>
      {!back ? <DirectionalIcon name="chevron" size={18} color={theme.colors.accent} /> : null}
    </MotionPressable>
  );
}

const styles = StyleSheet.create({
  root: { flex: 1 },
  content: { paddingBottom: 28 },
  body: { paddingHorizontal: 16, gap: 14 },
  hero: { padding: 18, gap: 6 },
  heroFoot: { flexDirection: 'row', alignItems: 'center', flexWrap: 'wrap', gap: 10, marginTop: 6 },
  card: { padding: 16, gap: 8 },
  checkRow: { flexDirection: 'row', alignItems: 'center', gap: 8, minHeight: 28 },
  sign: { width: 16, textAlign: 'center' },
  rule: { height: StyleSheet.hairlineWidth },
  matches: { flexDirection: 'row', alignItems: 'center', gap: 6 },
  collapsible: { paddingHorizontal: 16 },
  collapsibleHead: { minHeight: 56, flexDirection: 'row', alignItems: 'center', gap: 10 },
  collapsibleBody: { paddingBottom: 12, gap: 8 },
  note: { paddingVertical: 4 },
  loadingLines: { gap: 10, paddingVertical: 8 },
  switcher: { flexDirection: 'row', gap: 12, marginTop: 12 },
  monthButton: { minHeight: 48, flexDirection: 'row', alignItems: 'center', gap: 6 },
  monthButtonEnd: { justifyContent: 'flex-end' },
  alignEnd: { alignItems: 'flex-end' },
  tabular: { fontVariant: ['tabular-nums'] },
  flex: { flex: 1, minWidth: 0 },
});
