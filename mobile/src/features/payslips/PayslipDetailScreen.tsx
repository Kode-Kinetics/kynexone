import React, { useState, useEffect } from 'react';
import {
  View, Text, ScrollView, TouchableOpacity,
  ActivityIndicator, Alert,
} from 'react-native';
import { useRoute, useNavigation } from '@react-navigation/native';
import * as FileSystem from 'expo-file-system/legacy';
import * as Sharing from 'expo-sharing';
import { payslipApi } from '@/api/adapters';
import { PayslipDetail } from '@/types';
import { formatDate } from '@/utils/date';
import { COLORS } from '@/config';

function SectionHeader({ title }: { title: string }) {
  return (
    <View style={{ flexDirection: 'row', alignItems: 'center', marginVertical: 14 }}>
      <View style={{ flex: 1, height: 1, backgroundColor: '#E5E7EB' }} />
      <Text style={{ color: '#6B7280', fontSize: 12, fontWeight: '600', marginHorizontal: 10, textTransform: 'uppercase' }}>
        {title}
      </Text>
      <View style={{ flex: 1, height: 1, backgroundColor: '#E5E7EB' }} />
    </View>
  );
}

function LineItem({ label, amount, currency, highlight }: {
  label: string; amount: number; currency: string; highlight?: boolean;
}) {
  return (
    <View style={{
      flexDirection: 'row', justifyContent: 'space-between', alignItems: 'center',
      paddingVertical: 8, borderBottomWidth: 1, borderBottomColor: '#F3F4F6',
    }}>
      <Text style={{ fontSize: 14, color: highlight ? '#111827' : '#374151', fontWeight: highlight ? '600' : '400' }}>
        {label}
      </Text>
      <Text style={{ fontSize: 14, fontWeight: highlight ? '700' : '500', color: highlight ? COLORS.navy : '#374151' }}>
        {currency} {amount.toLocaleString(undefined, { minimumFractionDigits: 2, maximumFractionDigits: 2 })}
      </Text>
    </View>
  );
}

export default function PayslipDetailScreen() {
  const route = useRoute<any>();
  const navigation = useNavigation();
  const { id } = route.params as { id: string };

  const [payslip, setPayslip] = useState<PayslipDetail | null>(null);
  const [loading, setLoading] = useState(true);
  const [downloading, setDownloading] = useState(false);

  useEffect(() => {
    (async () => {
      try {
        const data = await payslipApi.getDetail(id);
        setPayslip(data);
      } catch (e: any) {
        Alert.alert('Error', e.message || 'Failed to load payslip');
        navigation.goBack();
      } finally {
        setLoading(false);
      }
    })();
  }, [id, navigation]);

  const downloadPdf = async () => {
    setDownloading(true);
    try {
      // The endpoint is authenticated; downloadAsync does not go through axios,
      // so the bearer token and tenant header are passed explicitly.
      const { url, headers } = await payslipApi.download(id);
      const fileUri = `${FileSystem.documentDirectory}payslip_${id}.pdf`;
      const result = await FileSystem.downloadAsync(url, fileUri, { headers });
      if (result.status === 200) {
        if (await Sharing.isAvailableAsync()) {
          await Sharing.shareAsync(result.uri, { mimeType: 'application/pdf' });
        } else {
          Alert.alert('Downloaded', `Saved to ${result.uri}`);
        }
      } else {
        throw new Error(`Download failed (HTTP ${result.status})`);
      }
    } catch (e: any) {
      Alert.alert('Error', e.message || 'Failed to download payslip');
    } finally {
      setDownloading(false);
    }
  };

  if (loading) {
    return (
      <View style={{ flex: 1, alignItems: 'center', justifyContent: 'center', backgroundColor: COLORS.background }}>
        <ActivityIndicator color={COLORS.blue} size="large" />
      </View>
    );
  }

  if (!payslip) return null;

  const earnings = payslip.details?.filter((d) => d.type === 'Earning') ?? [];
  const deductions = payslip.details?.filter((d) => d.type === 'Deduction') ?? [];

  return (
    <View style={{ flex: 1, backgroundColor: COLORS.background }}>
      {/* Header */}
      <View style={{ backgroundColor: COLORS.navy, paddingTop: 56, paddingBottom: 20, paddingHorizontal: 20 }}>
        <TouchableOpacity onPress={() => navigation.goBack()} style={{ marginBottom: 12 }}>
          <Text style={{ color: 'rgba(255,255,255,0.7)', fontSize: 14 }}>← Back</Text>
        </TouchableOpacity>
        <Text style={{ color: '#fff', fontSize: 22, fontWeight: '700' }}>
          {payslip.periodLabel || formatDate(payslip.periodStart, 'monthYear')}
        </Text>
        <Text style={{ color: 'rgba(255,255,255,0.6)', fontSize: 13, marginTop: 2 }}>
          Payslip · {payslip.status}
        </Text>
      </View>

      <ScrollView contentContainerStyle={{ padding: 20, paddingBottom: 40 }}>
        {/* Net pay hero */}
        <View style={{
          backgroundColor: COLORS.blue, borderRadius: 16, padding: 20, alignItems: 'center',
          shadowColor: COLORS.blue, shadowOffset: { width: 0, height: 4 }, shadowOpacity: 0.3, shadowRadius: 8, elevation: 4,
        }}>
          <Text style={{ color: 'rgba(255,255,255,0.8)', fontSize: 13 }}>Net Pay</Text>
          <Text style={{ color: '#fff', fontSize: 36, fontWeight: '800', marginTop: 4 }}>
            {payslip.currency} {(payslip.netPay ?? payslip.netSalary).toLocaleString(undefined, { minimumFractionDigits: 2 })}
          </Text>
          {payslip.paymentDate && (
            <Text style={{ color: 'rgba(255,255,255,0.7)', fontSize: 12, marginTop: 6 }}>
              Paid on {formatDate(payslip.paymentDate, 'display')}
            </Text>
          )}
        </View>

        {/* Summary row */}
        <View style={{
          flexDirection: 'row', backgroundColor: '#fff', borderRadius: 14, padding: 16,
          marginTop: 16, gap: 0,
          shadowColor: '#000', shadowOffset: { width: 0, height: 1 }, shadowOpacity: 0.06, shadowRadius: 4, elevation: 2,
        }}>
          {[
            { label: 'Gross Pay', value: payslip.grossPay },
            { label: 'Deductions', value: payslip.totalDeductions },
            { label: 'Net Pay', value: payslip.netPay },
          ].map((item, i) => (
            <View key={item.label} style={{
              flex: 1, alignItems: 'center',
              borderRightWidth: i < 2 ? 1 : 0, borderRightColor: '#E5E7EB',
            }}>
              <Text style={{ fontSize: 11, color: '#9CA3AF', textTransform: 'uppercase' }}>{item.label}</Text>
              <Text style={{ fontSize: 15, fontWeight: '700', color: i === 2 ? COLORS.blue : '#111827', marginTop: 4 }}>
                {(item.value ?? 0).toLocaleString(undefined, { minimumFractionDigits: 0 })}
              </Text>
            </View>
          ))}
        </View>

        {/* Period info */}
        <View style={{
          backgroundColor: '#fff', borderRadius: 14, padding: 16, marginTop: 12,
          shadowColor: '#000', shadowOffset: { width: 0, height: 1 }, shadowOpacity: 0.06, shadowRadius: 4, elevation: 2,
        }}>
          {(payslip.periodStart || payslip.periodEnd) && (
          <View style={{ flexDirection: 'row', justifyContent: 'space-between', paddingVertical: 6 }}>
            <Text style={{ color: '#6B7280', fontSize: 13 }}>Period</Text>
            <Text style={{ color: '#111827', fontSize: 13, fontWeight: '500' }}>
              {formatDate(payslip.periodStart, 'display')} – {formatDate(payslip.periodEnd, 'display')}
            </Text>
          </View>
          )}
          {payslip.ytdNet !== undefined && (
            <View style={{ flexDirection: 'row', justifyContent: 'space-between', paddingVertical: 6 }}>
              <Text style={{ color: '#6B7280', fontSize: 13 }}>Net Pay (YTD)</Text>
              <Text style={{ color: '#111827', fontSize: 13, fontWeight: '500' }}>
                {payslip.currency} {payslip.ytdNet.toLocaleString(undefined, { minimumFractionDigits: 2 })}
              </Text>
            </View>
          )}
          {payslip.workingDays !== undefined && (
            <View style={{ flexDirection: 'row', justifyContent: 'space-between', paddingVertical: 6 }}>
              <Text style={{ color: '#6B7280', fontSize: 13 }}>Working Days</Text>
              <Text style={{ color: '#111827', fontSize: 13, fontWeight: '500' }}>{payslip.workingDays}</Text>
            </View>
          )}
          {payslip.paidDays !== undefined && (
            <View style={{ flexDirection: 'row', justifyContent: 'space-between', paddingVertical: 6 }}>
              <Text style={{ color: '#6B7280', fontSize: 13 }}>Paid Days</Text>
              <Text style={{ color: '#111827', fontSize: 13, fontWeight: '500' }}>{payslip.paidDays}</Text>
            </View>
          )}
          {payslip.wpsReferenceNumber && (
            <View style={{ flexDirection: 'row', justifyContent: 'space-between', paddingVertical: 6 }}>
              <Text style={{ color: '#6B7280', fontSize: 13 }}>WPS Ref</Text>
              <Text style={{ color: '#111827', fontSize: 13, fontWeight: '500' }}>{payslip.wpsReferenceNumber}</Text>
            </View>
          )}
        </View>

        {/* Earnings */}
        {earnings.length > 0 && (
          <>
            <SectionHeader title="Earnings" />
            <View style={{
              backgroundColor: '#fff', borderRadius: 14, padding: 16,
              shadowColor: '#000', shadowOffset: { width: 0, height: 1 }, shadowOpacity: 0.06, shadowRadius: 4, elevation: 2,
            }}>
              {earnings.map((e) => (
                <LineItem key={e.id} label={e.componentName ?? e.description} amount={e.amount} currency={payslip.currency} />
              ))}
              <View style={{ flexDirection: 'row', justifyContent: 'space-between', marginTop: 8, paddingTop: 8, borderTopWidth: 2, borderTopColor: '#E5E7EB' }}>
                <Text style={{ fontWeight: '700', color: '#111827', fontSize: 14 }}>Total Earnings</Text>
                <Text style={{ fontWeight: '800', color: COLORS.navy, fontSize: 15 }}>
                  {payslip.currency} {(payslip.grossPay ?? payslip.grossSalary).toLocaleString(undefined, { minimumFractionDigits: 2 })}
                </Text>
              </View>
            </View>
          </>
        )}

        {/* Deductions */}
        {deductions.length > 0 && (
          <>
            <SectionHeader title="Deductions" />
            <View style={{
              backgroundColor: '#fff', borderRadius: 14, padding: 16,
              shadowColor: '#000', shadowOffset: { width: 0, height: 1 }, shadowOpacity: 0.06, shadowRadius: 4, elevation: 2,
            }}>
              {deductions.map((d) => (
                <LineItem key={d.id} label={d.componentName ?? d.description} amount={d.amount} currency={payslip.currency} />
              ))}
              <View style={{ flexDirection: 'row', justifyContent: 'space-between', marginTop: 8, paddingTop: 8, borderTopWidth: 2, borderTopColor: '#E5E7EB' }}>
                <Text style={{ fontWeight: '700', color: '#111827', fontSize: 14 }}>Total Deductions</Text>
                <Text style={{ fontWeight: '800', color: '#DC2626', fontSize: 15 }}>
                  {payslip.currency} {payslip.totalDeductions.toLocaleString(undefined, { minimumFractionDigits: 2 })}
                </Text>
              </View>
            </View>
          </>
        )}

        {/* Download button */}
        <TouchableOpacity
          onPress={downloadPdf}
          disabled={downloading}
          style={{
            backgroundColor: downloading ? '#93C5FD' : COLORS.blue,
            borderRadius: 12, padding: 16, alignItems: 'center',
            flexDirection: 'row', justifyContent: 'center', gap: 8, marginTop: 24,
          }}
        >
          {downloading ? (
            <ActivityIndicator color="#fff" />
          ) : (
            <>
              <Text style={{ fontSize: 20 }}>⬇️</Text>
              <Text style={{ color: '#fff', fontSize: 16, fontWeight: '700' }}>Download PDF</Text>
            </>
          )}
        </TouchableOpacity>
      </ScrollView>
    </View>
  );
}
