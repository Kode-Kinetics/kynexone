import React, { useCallback, useEffect, useState } from 'react';
import {
  ActivityIndicator,
  Alert,
  RefreshControl,
  ScrollView,
  StyleSheet,
  Text,
  View,
} from 'react-native';
import { useNavigation } from '@react-navigation/native';
import { Ionicons } from '@expo/vector-icons';
import { LinearGradient } from 'expo-linear-gradient';
import { payslipApi } from '@/api/adapters';
import { formatDate } from '@/utils/date';
import { useTheme } from '@/theme/ThemeProvider';
import {
  GlassSurface,
  LiquidBackdrop,
  MotionPressable,
  ScreenHero,
  SectionHeader,
} from '@/components/ui';
import type { Payslip } from '@/types';

export default function PayslipsScreen() {
  const navigation = useNavigation<any>();
  const { theme } = useTheme();
  const [payslips, setPayslips] = useState<Payslip[]>([]);
  const [loading, setLoading] = useState(true);
  const [refreshing, setRefreshing] = useState(false);

  const fetchPayslips = useCallback(async (isRefresh = false) => {
    if (isRefresh) setRefreshing(true);
    try {
      const data = await payslipApi.getList({ page: 1, limit: 24 });
      setPayslips(data.items ?? []);
    } catch (error: any) {
      Alert.alert('Payslips unavailable', error.message ?? 'Failed to load payslips.');
    } finally {
      setLoading(false);
      setRefreshing(false);
    }
  }, []);

  useEffect(() => {
    void fetchPayslips();
  }, [fetchPayslips]);

  const latest = payslips[0];

  return (
    <View style={[styles.root, { backgroundColor: theme.colors.canvas }]}>
      <LiquidBackdrop subtle />
      <ScrollView
        contentContainerStyle={styles.content}
        refreshControl={
          <RefreshControl
            refreshing={refreshing}
            onRefresh={() => void fetchPayslips(true)}
            tintColor={theme.colors.primary}
            colors={[theme.colors.primary]}
          />
        }
        showsVerticalScrollIndicator={false}
      >
        <ScreenHero
          eyebrow="Payroll"
          title="Payslips"
          subtitle="Private salary statements and payment history"
        />
        {loading ? (
          <View style={styles.section}>
            <GlassSurface radius={theme.radius.xl} contentStyle={styles.stateCard}>
              <ActivityIndicator color={theme.colors.primary} />
              <Text style={[theme.typography.caption, { color: theme.colors.textSecondary }]}>Loading payslips…</Text>
            </GlassSurface>
          </View>
        ) : latest ? (
          <View style={styles.section}>
            <MotionPressable
              onPress={() => navigation.navigate('PayslipDetail', { id: latest.id })}
              haptic="selection"
              contentStyle={styles.rounded}
            >
              <LinearGradient
                colors={theme.gradients.primary}
                start={{ x: 0, y: 0 }}
                end={{ x: 1, y: 1 }}
                style={styles.latestCard}
              >
                <View style={styles.latestHeader}>
                  <View>
                    <Text style={styles.latestEyebrow}>LATEST PAYSLIP</Text>
                    <Text style={styles.latestPeriod}>
                      {latest.periodLabel || formatDate(latest.periodStart, 'monthYear')}
                    </Text>
                  </View>
                  <View style={styles.latestArrow}>
                    <Ionicons name="arrow-forward" size={20} color="#FFFFFF" />
                  </View>
                </View>
                <Text style={styles.latestAmountLabel}>Net pay</Text>
                <Text style={styles.latestAmount}>
                  {latest.currency} {(latest.netPay ?? latest.netSalary).toLocaleString('en-US', {
                    minimumFractionDigits: 2,
                  })}
                </Text>
                <View style={styles.latestMetrics}>
                  <SalaryMetric label="Gross" value={`${latest.currency} ${(latest.grossPay ?? latest.grossSalary).toLocaleString()}`} />
                  <View style={styles.latestDivider} />
                  <SalaryMetric label="Deductions" value={`${latest.currency} ${latest.totalDeductions.toLocaleString()}`} />
                </View>
              </LinearGradient>
            </MotionPressable>
          </View>
        ) : null}

        <View style={styles.section}>
          <SectionHeader
            title="Salary history"
            subtitle={payslips.length ? `${payslips.length} published statement${payslips.length === 1 ? '' : 's'}` : 'Published statements appear here'}
          />
          {!loading && payslips.length === 0 ? (
            <GlassSurface radius={theme.radius.xl} contentStyle={styles.emptyCard}>
              <View style={[styles.emptyIcon, { backgroundColor: `${theme.colors.primary}18` }]}>
                <Ionicons name="wallet-outline" size={29} color={theme.colors.primary} />
              </View>
              <Text style={[theme.typography.h3, { color: theme.colors.text }]}>No payslips yet</Text>
              <Text style={[theme.typography.caption, styles.emptyText, { color: theme.colors.textMuted }]}>
                Your salary statements will appear after payroll is finalized and published.
              </Text>
            </GlassSurface>
          ) : (
            <View style={styles.list}>
              {payslips.map((payslip) => (
                <PayslipCard
                  key={payslip.id}
                  payslip={payslip}
                  onPress={() => navigation.navigate('PayslipDetail', { id: payslip.id })}
                />
              ))}
            </View>
          )}
        </View>
        <View style={styles.bottomSpacer} />
      </ScrollView>
    </View>
  );
}
function SalaryMetric({ label, value }: { label: string; value: string }) {
  return (
    <View style={styles.salaryMetric}>
      <Text style={styles.salaryMetricLabel}>{label}</Text>
      <Text style={styles.salaryMetricValue}>{value}</Text>
    </View>
  );
}

function PayslipCard({ payslip, onPress }: { payslip: Payslip; onPress: () => void }) {
  const { theme } = useTheme();
  const period = payslip.periodLabel || formatDate(payslip.periodStart, 'monthYear');
  const month = payslip.month
    ? new Date(payslip.year, payslip.month - 1, 1).toLocaleString('en-US', { month: 'short' })
    : 'PAY';
  const status = payslip.status || 'Published';
  const statusColor = status.toLowerCase() === 'published'
    ? theme.colors.success
    : status.toLowerCase() === 'pending'
      ? theme.colors.warning
      : theme.colors.textSecondary;

  return (
    <MotionPressable
      onPress={onPress}
      haptic="selection"
      contentStyle={styles.rounded}
      accessibilityRole="button"
      accessibilityLabel={`${period} payslip`}
    >
      <GlassSurface elevated={false} radius={theme.radius.xl} contentStyle={styles.payslipCard}>
        <View style={[styles.monthBadge, { backgroundColor: `${theme.colors.primary}18` }]}>
          <Text style={[theme.typography.micro, { color: theme.colors.primary }]}>{month.toUpperCase()}</Text>
          <Text style={[styles.monthYear, { color: theme.colors.primary }]}>{payslip.year || '—'}</Text>
        </View>
        <View style={styles.payslipCopy}>
          <Text style={[theme.typography.bodyStrong, { color: theme.colors.text }]}>{period}</Text>
          <Text style={[theme.typography.caption, { color: theme.colors.textSecondary, marginTop: 4 }]}>
            Net pay · {payslip.currency} {(payslip.netPay ?? payslip.netSalary).toLocaleString('en-US', {
              minimumFractionDigits: 2,
            })}
          </Text>
          <Text style={[theme.typography.micro, { color: theme.colors.textMuted, marginTop: 4 }]}>
            Paid {payslip.paymentDate ? formatDate(payslip.paymentDate, 'display') : '—'}
          </Text>
        </View>
        <View style={styles.payslipEnd}>
          <View style={[styles.statusPill, { backgroundColor: `${statusColor}18` }]}>
            <View style={[styles.statusDot, { backgroundColor: statusColor }]} />
            <Text style={[theme.typography.micro, { color: statusColor }]}>{status}</Text>
          </View>
          <Ionicons name="chevron-forward" size={18} color={theme.colors.textMuted} />
        </View>
      </GlassSurface>
    </MotionPressable>
  );
}
const styles = StyleSheet.create({
  root: { flex: 1 },
  content: { paddingBottom: 34 },
  section: { paddingHorizontal: 16, marginTop: 16 },
  rounded: { borderRadius: 24 },
  stateCard: { minHeight: 170, alignItems: 'center', justifyContent: 'center', gap: 12, padding: 22 },
  latestCard: { borderRadius: 26, padding: 19, minHeight: 230 },
  latestHeader: { flexDirection: 'row', justifyContent: 'space-between', alignItems: 'flex-start' },
  latestEyebrow: { color: 'rgba(255,255,255,0.72)', fontSize: 10, lineHeight: 14, fontWeight: '800', letterSpacing: 1.2 },
  latestPeriod: { color: '#FFFFFF', fontSize: 19, lineHeight: 24, fontWeight: '800', marginTop: 4 },
  latestArrow: { width: 43, height: 43, borderRadius: 16, backgroundColor: 'rgba(255,255,255,0.16)', alignItems: 'center', justifyContent: 'center' },
  latestAmountLabel: { color: 'rgba(255,255,255,0.72)', fontSize: 12, marginTop: 24 },
  latestAmount: { color: '#FFFFFF', fontSize: 31, lineHeight: 37, fontWeight: '800', letterSpacing: -0.7, marginTop: 3 },
  latestMetrics: { flexDirection: 'row', alignItems: 'stretch', marginTop: 22, backgroundColor: 'rgba(255,255,255,0.11)', borderRadius: 17, paddingVertical: 12 },
  salaryMetric: { flex: 1, paddingHorizontal: 14 },
  salaryMetricLabel: { color: 'rgba(255,255,255,0.65)', fontSize: 10, fontWeight: '600' },
  salaryMetricValue: { color: '#FFFFFF', fontSize: 13, fontWeight: '700', marginTop: 4 },
  latestDivider: { width: StyleSheet.hairlineWidth, backgroundColor: 'rgba(255,255,255,0.24)' },
  list: { gap: 9 },
  payslipCard: { minHeight: 92, flexDirection: 'row', alignItems: 'center', gap: 13, padding: 13 },
  monthBadge: { width: 54, height: 58, borderRadius: 17, alignItems: 'center', justifyContent: 'center' },
  monthYear: { fontSize: 15, lineHeight: 18, fontWeight: '800', marginTop: 2 },
  payslipCopy: { flex: 1, minWidth: 0 },
  payslipEnd: { alignItems: 'flex-end', gap: 10 },
  statusPill: { flexDirection: 'row', alignItems: 'center', gap: 5, paddingHorizontal: 8, paddingVertical: 5, borderRadius: 999 },
  statusDot: { width: 6, height: 6, borderRadius: 3 },
  emptyCard: { minHeight: 210, alignItems: 'center', justifyContent: 'center', gap: 9, padding: 24 },
  emptyIcon: { width: 58, height: 58, borderRadius: 21, alignItems: 'center', justifyContent: 'center', marginBottom: 4 },
  emptyText: { textAlign: 'center', maxWidth: 280 },
  bottomSpacer: { height: 14 },
});
