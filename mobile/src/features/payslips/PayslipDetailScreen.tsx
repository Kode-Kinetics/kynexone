import React, { useEffect, useState } from 'react';
import {
  ActivityIndicator,
  Alert,
  ScrollView,
  StyleSheet,
  Text,
  useWindowDimensions,
  View,
} from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import { useNavigation, useRoute } from '@react-navigation/native';
import * as FileSystem from 'expo-file-system/legacy';
import * as Sharing from 'expo-sharing';
import { payslipApi } from '@/api/adapters';
import type { PayslipDetail } from '@/types';
import { formatDate } from '@/utils/date';
import {
  GlassSurface,
  LiquidBackdrop,
  LiquidButton,
  ScreenHero,
  SectionHeader,
} from '@/components/ui';
import { useTheme } from '@/theme/ThemeProvider';

export default function PayslipDetailScreen() {
  const route = useRoute<any>();
  const navigation = useNavigation();
  const { theme } = useTheme();
  const { width, fontScale } = useWindowDimensions();
  const stackSummary = width < 370 || fontScale > 1.2;
  const { id } = route.params as { id: string };

  const [payslip, setPayslip] = useState<PayslipDetail | null>(null);
  const [loading, setLoading] = useState(true);
  const [downloading, setDownloading] = useState(false);

  useEffect(() => {
    let active = true;
    payslipApi
      .getDetail(id)
      .then((data) => {
        if (active) setPayslip(data);
      })
      .catch((error: any) => {
        Alert.alert('Could not load payslip', error?.message || 'Please try again.');
        navigation.goBack();
      })
      .finally(() => active && setLoading(false));
    return () => {
      active = false;
    };
  }, [id, navigation]);

  const downloadPdf = async () => {
    setDownloading(true);
    try {
      const { url, headers } = await payslipApi.download(id);
      const fileUri = FileSystem.documentDirectory + 'payslip_' + id + '.pdf';
      const result = await FileSystem.downloadAsync(url, fileUri, { headers });

      if (result.status !== 200) {
        throw new Error('Download failed (HTTP ' + result.status + ')');
      }

      if (await Sharing.isAvailableAsync()) {
        await Sharing.shareAsync(result.uri, { mimeType: 'application/pdf' });
      } else {
        Alert.alert('Payslip saved', 'The PDF was saved to your device.');
      }
    } catch (error: any) {
      Alert.alert('Could not download payslip', error?.message || 'Please try again.');
    } finally {
      setDownloading(false);
    }
  };

  if (loading) {
    return (
      <View style={[styles.loadingRoot, { backgroundColor: theme.colors.canvas }]}>
        <LiquidBackdrop subtle />
        <GlassSurface
          accessibilityRole="progressbar"
          accessibilityLabel="Loading payslip"
          accessibilityLiveRegion="polite"
          radius={theme.radius.xl}
          contentStyle={styles.loadingCard}
        >
          <ActivityIndicator color={theme.colors.primary} />
          <Text style={[theme.typography.caption, { color: theme.colors.textSecondary }]}>
            Loading payslip…
          </Text>
        </GlassSurface>
      </View>
    );
  }

  if (!payslip) return null;

  const earnings = payslip.details?.filter((item) => item.type === 'Earning') ?? [];
  const deductions = payslip.details?.filter((item) => item.type === 'Deduction') ?? [];
  const grossPay = payslip.grossPay ?? payslip.grossSalary;
  const netPay = payslip.netPay ?? payslip.netSalary;

  return (
    <View style={[styles.root, { backgroundColor: theme.colors.canvas }]}>
      <LiquidBackdrop subtle />
      <ScrollView
        contentContainerStyle={styles.scroll}
        showsVerticalScrollIndicator={false}
      >
        <ScreenHero
          eyebrow="Payroll"
          title={payslip.periodLabel || formatDate(payslip.periodStart, 'monthYear')}
          subtitle={'Payslip · ' + payslip.status}
          onBack={() => navigation.goBack()}
        />

        <View style={styles.content}>
          <GlassSurface
            radius={theme.radius.xxl}
            contentStyle={styles.netCard}
            tintColor={theme.isDark ? 'rgba(47,107,255,0.28)' : 'rgba(47,107,255,0.18)'}
          >
            <View style={[styles.netIcon, { backgroundColor: theme.colors.primary + '18' }]}>
              <Ionicons name="wallet-outline" size={22} color={theme.colors.primary} />
            </View>
            <Text style={[theme.typography.caption, { color: theme.colors.textSecondary }]}>
              Net pay
            </Text>
            <Text
              style={[styles.netAmount, { color: theme.colors.text }]}
            >
              {formatMoney(payslip.currency, netPay)}
            </Text>
            {payslip.paymentDate ? (
              <View style={styles.paidRow}>
                <View style={[styles.paidDot, { backgroundColor: theme.colors.success }]} />
                <Text style={[theme.typography.caption, { color: theme.colors.textSecondary }]}>
                  Paid {formatDate(payslip.paymentDate, 'display')}
                </Text>
              </View>
            ) : null}
          </GlassSurface>

          <GlassSurface
            elevated={false}
            radius={theme.radius.xl}
            contentStyle={[styles.summary, stackSummary && styles.summaryStacked]}
          >
            <Metric
              label="Gross pay"
              value={formatMoney(payslip.currency, grossPay)}
              stacked={stackSummary}
            />
            <MetricDivider stacked={stackSummary} />
            <Metric
              label="Deductions"
              value={formatMoney(payslip.currency, payslip.totalDeductions)}
              stacked={stackSummary}
            />
            <MetricDivider stacked={stackSummary} />
            <Metric
              label="Net pay"
              value={formatMoney(payslip.currency, netPay)}
              accent
              stacked={stackSummary}
            />
          </GlassSurface>

          <GlassSurface elevated={false} radius={theme.radius.xl} contentStyle={styles.detailsCard}>
            {(payslip.periodStart || payslip.periodEnd) ? (
              <DetailRow
                label="Period"
                value={
                  formatDate(payslip.periodStart, 'display') +
                  ' – ' +
                  formatDate(payslip.periodEnd, 'display')
                }
              />
            ) : null}
            {payslip.ytdNet !== undefined ? (
              <DetailRow label="Net pay (YTD)" value={formatMoney(payslip.currency, payslip.ytdNet)} />
            ) : null}
            {payslip.workingDays !== undefined ? (
              <DetailRow label="Working days" value={String(payslip.workingDays)} />
            ) : null}
            {payslip.paidDays !== undefined ? (
              <DetailRow label="Paid days" value={String(payslip.paidDays)} />
            ) : null}
            {payslip.wpsReferenceNumber ? (
              <DetailRow label="WPS reference" value={payslip.wpsReferenceNumber} />
            ) : null}
          </GlassSurface>

          {earnings.length > 0 ? (
            <View>
              <SectionHeader title="Earnings" subtitle="Salary and allowances" />
              <GlassSurface elevated={false} radius={theme.radius.xl} contentStyle={styles.linesCard}>
                {earnings.map((item) => (
                  <LineItem
                    key={item.id}
                    label={item.componentName ?? item.description}
                    amount={item.amount}
                    currency={payslip.currency}
                  />
                ))}
                <TotalRow
                  label="Total earnings"
                  value={formatMoney(payslip.currency, grossPay)}
                />
              </GlassSurface>
            </View>
          ) : null}

          {deductions.length > 0 ? (
            <View>
              <SectionHeader title="Deductions" subtitle="Employee deductions" />
              <GlassSurface elevated={false} radius={theme.radius.xl} contentStyle={styles.linesCard}>
                {deductions.map((item) => (
                  <LineItem
                    key={item.id}
                    label={item.componentName ?? item.description}
                    amount={item.amount}
                    currency={payslip.currency}
                  />
                ))}
                <TotalRow
                  label="Total deductions"
                  value={formatMoney(payslip.currency, payslip.totalDeductions)}
                  danger
                />
              </GlassSurface>
            </View>
          ) : null}

          <LiquidButton
            label="Download PDF"
            icon="download-outline"
            onPress={() => void downloadPdf()}
            loading={downloading}
            disabled={downloading}
          />

          <Text style={[theme.typography.micro, styles.footer, { color: theme.colors.textMuted }]}>
            Payroll values are shown from the finalized payslip record.
          </Text>
        </View>
      </ScrollView>
    </View>
  );
}

function Metric({
  label,
  value,
  accent,
  stacked,
}: {
  label: string;
  value: string;
  accent?: boolean;
  stacked?: boolean;
}) {
  const { theme } = useTheme();
  return (
    <View style={[styles.metric, stacked && styles.metricStacked]}>
      <Text style={[theme.typography.micro, styles.metricLabel, { color: theme.colors.textMuted }]}>
        {label}
      </Text>
      <Text
        style={[
          theme.typography.bodyStrong,
          styles.metricValue,
          { color: accent ? theme.colors.primary : theme.colors.text },
        ]}
      >
        {value}
      </Text>
    </View>
  );
}

function MetricDivider({ stacked }: { stacked?: boolean }) {
  const { theme } = useTheme();
  return (
    <View
      style={[
        stacked ? styles.dividerHorizontal : styles.dividerVertical,
        { backgroundColor: theme.colors.divider },
      ]}
    />
  );
}

function DetailRow({ label, value }: { label: string; value: string }) {
  const { theme } = useTheme();
  return (
    <View style={[styles.detailRow, { borderBottomColor: theme.colors.divider }]}>
      <Text style={[theme.typography.caption, styles.detailLabel, { color: theme.colors.textSecondary }]}>
        {label}
      </Text>
      <Text
        selectable={label === 'WPS reference'}
        style={[theme.typography.caption, styles.detailValue, { color: theme.colors.text }]}
      >
        {value}
      </Text>
    </View>
  );
}

function LineItem({ label, amount, currency }: { label: string; amount: number; currency: string }) {
  const { theme } = useTheme();
  return (
    <View style={[styles.lineRow, { borderBottomColor: theme.colors.divider }]}>
      <Text style={[theme.typography.body, styles.lineLabel, { color: theme.colors.textSecondary }]}>
        {label}
      </Text>
      <Text style={[theme.typography.bodyStrong, styles.lineValue, { color: theme.colors.text }]}>
        {formatMoney(currency, amount)}
      </Text>
    </View>
  );
}

function TotalRow({
  label,
  value,
  danger,
}: {
  label: string;
  value: string;
  danger?: boolean;
}) {
  const { theme } = useTheme();
  return (
    <View style={[styles.totalRow, { borderTopColor: theme.colors.divider }]}>
      <Text style={[theme.typography.bodyStrong, { color: theme.colors.text }]}>{label}</Text>
      <Text
        style={[
          theme.typography.bodyStrong,
          { color: danger ? theme.colors.danger : theme.colors.primary },
        ]}
      >
        {value}
      </Text>
    </View>
  );
}

function formatMoney(currency: string, amount: number | undefined) {
  return (
    currency +
    ' ' +
    Number(amount ?? 0).toLocaleString(undefined, {
      minimumFractionDigits: 2,
      maximumFractionDigits: 2,
    })
  );
}

const styles = StyleSheet.create({
  root: { flex: 1 },
  scroll: { paddingBottom: 38 },
  content: { paddingHorizontal: 16, paddingTop: 12, gap: 14 },
  loadingRoot: { flex: 1, alignItems: 'center', justifyContent: 'center', padding: 24 },
  loadingCard: { minWidth: 220, minHeight: 130, alignItems: 'center', justifyContent: 'center', gap: 12 },
  netCard: { alignItems: 'center', padding: 20 },
  netIcon: {
    width: 46,
    height: 46,
    borderRadius: 16,
    alignItems: 'center',
    justifyContent: 'center',
    marginBottom: 10,
  },
  netAmount: { fontSize: 32, lineHeight: 39, fontWeight: '800', marginTop: 4, textAlign: 'center' },
  paidRow: { flexDirection: 'row', alignItems: 'center', gap: 6, marginTop: 8 },
  paidDot: { width: 7, height: 7, borderRadius: 4 },
  summary: { flexDirection: 'row', alignItems: 'stretch', paddingVertical: 14 },
  summaryStacked: { flexDirection: 'column', paddingHorizontal: 14 },
  metric: { flex: 1, minWidth: 0, alignItems: 'center', paddingHorizontal: 6 },
  metricStacked: { alignItems: 'flex-start', paddingHorizontal: 0, paddingVertical: 9 },
  metricLabel: { textTransform: 'uppercase' },
  metricValue: { marginTop: 4 },
  dividerVertical: { width: StyleSheet.hairlineWidth },
  dividerHorizontal: { height: StyleSheet.hairlineWidth, width: '100%' },
  detailsCard: { paddingHorizontal: 15, paddingVertical: 6 },
  detailRow: {
    minHeight: 46,
    borderBottomWidth: StyleSheet.hairlineWidth,
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: 14,
    paddingVertical: 9,
  },
  detailLabel: { flexShrink: 0 },
  detailValue: { flex: 1, textAlign: 'right' },
  linesCard: { paddingHorizontal: 15, paddingVertical: 5 },
  lineRow: {
    minHeight: 48,
    borderBottomWidth: StyleSheet.hairlineWidth,
    flexDirection: 'row',
    alignItems: 'center',
    gap: 12,
    paddingVertical: 9,
  },
  lineLabel: { flex: 1, minWidth: 0 },
  lineValue: { textAlign: 'right' },
  totalRow: {
    minHeight: 52,
    borderTopWidth: 1,
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'space-between',
    gap: 12,
    paddingTop: 10,
    marginTop: 4,
  },
  footer: { textAlign: 'center', paddingHorizontal: 18 },
});
