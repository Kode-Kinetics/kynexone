import React, { useState, useEffect, useCallback } from 'react';
import {
  View, Text, ScrollView, TouchableOpacity,
  ActivityIndicator, Alert, RefreshControl,
} from 'react-native';
import { useNavigation } from '@react-navigation/native';
import { payslipApi } from '@/api/adapters';
import { Payslip } from '@/types';
import { formatDate } from '@/utils/date';
import { COLORS } from '@/config';

function PayslipCard({ payslip, onPress }: { payslip: Payslip; onPress: () => void }) {
  const statusColors: Record<string, { bg: string; text: string }> = {
    Published: { bg: '#F0FDF4', text: '#15803D' },
    Draft:     { bg: '#FFF7ED', text: '#C2410C' },
    Pending:   { bg: '#FEF9C3', text: '#A16207' },
  };
  const s = (payslip.status ? statusColors[payslip.status] : undefined) ?? statusColors['Published'];

  return (
    <TouchableOpacity
      onPress={onPress}
      style={{
        backgroundColor: '#fff', borderRadius: 14, padding: 16, marginBottom: 12,
        shadowColor: '#000', shadowOffset: { width: 0, height: 1 }, shadowOpacity: 0.07, shadowRadius: 4, elevation: 2,
        flexDirection: 'row', alignItems: 'center',
      }}
    >
      {/* Month badge */}
      <View style={{
        width: 52, height: 52, borderRadius: 12, backgroundColor: '#EFF6FF',
        alignItems: 'center', justifyContent: 'center', marginRight: 14,
      }}>
        {/* /ess/payslips carries no period yet (Wave-2 spec) — never render "Invalid Date". */}
        <Text style={{ fontSize: 11, color: COLORS.blue, fontWeight: '700', textTransform: 'uppercase' }}>
          {payslip.month ? new Date(payslip.year, payslip.month - 1, 1).toLocaleString('en-US', { month: 'short' }) : 'PAY'}
        </Text>
        <Text style={{ fontSize: 14, color: COLORS.blue, fontWeight: '700' }}>
          {payslip.year || '💰'}
        </Text>
      </View>

      {/* Details */}
      <View style={{ flex: 1 }}>
        <Text style={{ fontSize: 15, fontWeight: '700', color: '#111827' }}>
          {payslip.periodLabel || formatDate(payslip.periodStart, 'monthYear')}
        </Text>
        <Text style={{ fontSize: 13, color: '#6B7280', marginTop: 2 }}>
          Net Pay: <Text style={{ color: COLORS.blue, fontWeight: '700' }}>
            {payslip.currency} {(payslip.netPay ?? payslip.netSalary).toLocaleString()}
          </Text>
        </Text>
        <Text style={{ fontSize: 12, color: '#9CA3AF', marginTop: 2 }}>
          Paid: {payslip.paymentDate ? formatDate(payslip.paymentDate, 'display') : '—'}
        </Text>
      </View>

      {/* Status + Arrow */}
      <View style={{ alignItems: 'flex-end', gap: 6 }}>
        <View style={{ backgroundColor: s.bg, borderRadius: 6, paddingHorizontal: 8, paddingVertical: 2 }}>
          <Text style={{ color: s.text, fontSize: 11, fontWeight: '600' }}>{payslip.status}</Text>
        </View>
        <Text style={{ color: '#D1D5DB', fontSize: 18 }}>›</Text>
      </View>
    </TouchableOpacity>
  );
}

export default function PayslipsScreen() {
  const navigation = useNavigation<any>();
  const [payslips, setPayslips] = useState<Payslip[]>([]);
  const [loading, setLoading] = useState(true);
  const [refreshing, setRefreshing] = useState(false);

  const fetchPayslips = useCallback(async () => {
    try {
      const data = await payslipApi.getList({ page: 1, limit: 24 });
      setPayslips(data.items || []);
    } catch (e: any) {
      Alert.alert('Error', e.message || 'Failed to load payslips');
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => { fetchPayslips(); }, [fetchPayslips]);

  const onRefresh = async () => {
    setRefreshing(true);
    await fetchPayslips();
    setRefreshing(false);
  };

  // Latest payslip for summary banner
  const latest = payslips[0];

  return (
    <View style={{ flex: 1, backgroundColor: COLORS.background }}>
      {/* Header */}
      <View style={{ backgroundColor: COLORS.navy, paddingTop: 56, paddingBottom: 20, paddingHorizontal: 20 }}>
        <Text style={{ color: '#fff', fontSize: 22, fontWeight: '700' }}>Payslips</Text>
        <Text style={{ color: 'rgba(255,255,255,0.6)', fontSize: 13, marginTop: 2 }}>
          Your salary history
        </Text>
      </View>

      {/* Latest payslip summary banner */}
      {latest && !loading && (
        <View style={{ marginHorizontal: 16, marginTop: -1 }}>
          <TouchableOpacity
            onPress={() => navigation.navigate('PayslipDetail', { id: latest.id })}
            style={{
              backgroundColor: COLORS.blue, borderRadius: 14, padding: 18,
              flexDirection: 'row', justifyContent: 'space-between', alignItems: 'center',
              shadowColor: COLORS.blue, shadowOffset: { width: 0, height: 4 }, shadowOpacity: 0.3, shadowRadius: 8, elevation: 4,
            }}
          >
            <View>
              <Text style={{ color: 'rgba(255,255,255,0.8)', fontSize: 12 }}>Latest Payslip</Text>
              <Text style={{ color: '#fff', fontSize: 18, fontWeight: '700', marginTop: 2 }}>
                {latest.periodLabel || formatDate(latest.periodStart, 'monthYear')}
              </Text>
              <View style={{ flexDirection: 'row', gap: 16, marginTop: 10 }}>
                <View>
                  <Text style={{ color: 'rgba(255,255,255,0.7)', fontSize: 11 }}>Gross</Text>
                  <Text style={{ color: '#fff', fontSize: 14, fontWeight: '600' }}>
                    {latest.currency} {(latest.grossPay ?? latest.grossSalary).toLocaleString()}
                  </Text>
                </View>
                <View>
                  <Text style={{ color: 'rgba(255,255,255,0.7)', fontSize: 11 }}>Deductions</Text>
                  <Text style={{ color: '#fff', fontSize: 14, fontWeight: '600' }}>
                    {latest.currency} {latest.totalDeductions.toLocaleString()}
                  </Text>
                </View>
                <View>
                  <Text style={{ color: 'rgba(255,255,255,0.7)', fontSize: 11 }}>Net</Text>
                  <Text style={{ color: '#fff', fontSize: 15, fontWeight: '700' }}>
                    {latest.currency} {(latest.netPay ?? latest.netSalary).toLocaleString()}
                  </Text>
                </View>
              </View>
            </View>
            <Text style={{ color: 'rgba(255,255,255,0.8)', fontSize: 32 }}>›</Text>
          </TouchableOpacity>
        </View>
      )}

      {/* List */}
      {loading ? (
        <ActivityIndicator color={COLORS.blue} style={{ marginTop: 60 }} />
      ) : (
        <ScrollView
          refreshControl={<RefreshControl refreshing={refreshing} onRefresh={onRefresh} />}
          contentContainerStyle={{ padding: 16, paddingTop: latest ? 16 : 16 }}
        >
          {payslips.length === 0 ? (
            <View style={{ alignItems: 'center', marginTop: 60 }}>
              <Text style={{ fontSize: 40 }}>💰</Text>
              <Text style={{ color: '#374151', fontSize: 16, fontWeight: '600', marginTop: 12 }}>No payslips yet</Text>
              <Text style={{ color: '#9CA3AF', fontSize: 13, marginTop: 4 }}>Your payslips will appear here once processed</Text>
            </View>
          ) : (
            <>
              <Text style={{ fontSize: 13, fontWeight: '600', color: '#6B7280', marginBottom: 12 }}>
                ALL PAYSLIPS ({payslips.length})
              </Text>
              {payslips.map((p) => (
                <PayslipCard
                  key={p.id}
                  payslip={p}
                  onPress={() => navigation.navigate('PayslipDetail', { id: p.id })}
                />
              ))}
            </>
          )}
        </ScrollView>
      )}
    </View>
  );
}
