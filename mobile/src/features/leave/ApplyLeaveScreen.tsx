// ============================================================
// ZAYRA MOBILE — Apply Leave Screen
// ============================================================

import React, { useState, useEffect, useCallback } from 'react';
import {
  View, Text, ScrollView, TouchableOpacity, StyleSheet,
  TextInput, Alert, ActivityIndicator, Platform, Switch,
} from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import DateTimePicker from '@react-native-community/datetimepicker';
import * as DocumentPicker from 'expo-document-picker';
import { documentsApi, leaveApi, normalizePickedFile, type PickedFile } from '@/api/services';
import { formatDate, toISODate } from '@/utils/date';
import { COLORS } from '@/config';
import { FEATURES } from '@/config/features';
import type { LeaveBalance } from '@/types';

interface Props {
  navigation: any;
}

export default function ApplyLeaveScreen({ navigation }: Props) {
  const [leaveTypes, setLeaveTypes] = useState<{ id: string; name: string }[]>([]);
  const [balances, setBalances] = useState<LeaveBalance[]>([]);
  const [loading, setLoading] = useState(false);
  const [submitting, setSubmitting] = useState(false);

  // Form state
  const [selectedTypeId, setSelectedTypeId] = useState('');
  const [startDate, setStartDate] = useState(new Date());
  const [endDate, setEndDate] = useState(new Date());
  const [isHalfDay, setIsHalfDay] = useState(false);
  const [halfDayPeriod, setHalfDayPeriod] = useState<'MORNING' | 'AFTERNOON'>('MORNING');
  const [reason, setReason] = useState('');
  const [attachment, setAttachment] = useState<PickedFile | null>(null);

  // Date picker control
  const [showStartPicker, setShowStartPicker] = useState(false);
  const [showEndPicker, setShowEndPicker] = useState(false);

  const loadData = useCallback(async () => {
    setLoading(true);
    try {
      const [types, bals] = await Promise.all([
        leaveApi.getLeaveTypes(),
        leaveApi.getLeaveBalances(),
      ]);
      setLeaveTypes(types);
      setBalances(bals);
      if (types.length > 0) setSelectedTypeId(types[0].id);
    } catch {
      Alert.alert('Error', 'Could not load leave types.');
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void loadData();
  }, [loadData]);

  async function pickAttachment() {
    try {
      const result = await DocumentPicker.getDocumentAsync({
        type: ['application/pdf', 'image/*'],
        copyToCacheDirectory: true,
      });
      if (!result.canceled && result.assets?.[0]) {
        const asset = result.assets[0];
        setAttachment(normalizePickedFile({
          uri: asset.uri,
          name: asset.name,
          mimeType: asset.mimeType,
          size: asset.size,
        }));
      }
    } catch {
      Alert.alert('Error', 'Could not pick file.');
    }
  }

  async function submit() {
    if (!selectedTypeId) {
      Alert.alert('Error', 'Please select a leave type.');
      return;
    }
    if (endDate < startDate) {
      Alert.alert('Error', 'End date cannot be before start date.');
      return;
    }

    setSubmitting(true);
    try {
      const uploaded = attachment
        ? await documentsApi.uploadDocument({
            file: attachment,
            documentType: 'Leave Attachment',
          })
        : null;
      await leaveApi.submitLeaveRequest({
        leaveTypeId: selectedTypeId,
        startDate: toISODate(startDate),
        endDate: toISODate(endDate),
        isHalfDay,
        halfDayPeriod: isHalfDay ? halfDayPeriod : undefined,
        reason: reason.trim() || undefined,
        attachmentDocumentId: uploaded?.id,
      });
      Alert.alert('Success', 'Your leave request has been submitted.', [
        { text: 'OK', onPress: () => navigation.goBack() },
      ]);
    } catch (err: any) {
      Alert.alert(
        'Submission Failed',
        err?.response?.data?.message ?? 'Could not submit request. Please try again.'
      );
    } finally {
      setSubmitting(false);
    }
  }

  const selectedBalance = balances.find((b) => b.leaveTypeId === selectedTypeId);

  const totalDays = isHalfDay
    ? 0.5
    : Math.max(
        1,
        Math.floor(
          (endDate.getTime() - startDate.getTime()) / (1000 * 60 * 60 * 24)
        ) + 1
      );

  if (loading) {
    return (
      <View style={styles.centered}>
        <ActivityIndicator color={COLORS.blue} size="large" />
      </View>
    );
  }

  return (
    <View style={styles.container}>
      {/* Header */}
      <View style={styles.header}>
        <TouchableOpacity onPress={() => navigation.goBack()}>
          <Ionicons name="close" size={24} color={COLORS.text} />
        </TouchableOpacity>
        <Text style={styles.headerTitle}>Apply for Leave</Text>
        <TouchableOpacity
          style={[styles.submitHeaderBtn, submitting && { opacity: 0.5 }]}
          onPress={submit}
          disabled={submitting}
        >
          {submitting ? (
            <ActivityIndicator size="small" color={COLORS.blue} />
          ) : (
            <Text style={styles.submitHeaderBtnText}>Submit</Text>
          )}
        </TouchableOpacity>
      </View>

      <ScrollView style={styles.form} contentContainerStyle={styles.formContent}>
        {/* Leave Type */}
        <View style={styles.field}>
          <Text style={styles.label}>Leave Type *</Text>
          <ScrollView horizontal showsHorizontalScrollIndicator={false}>
            {leaveTypes.map((lt) => {
              const bal = balances.find((b) => b.leaveTypeId === lt.id);
              const isSelected = selectedTypeId === lt.id;
              return (
                <TouchableOpacity
                  key={lt.id}
                  style={[styles.typeChip, isSelected && styles.typeChipSelected]}
                  onPress={() => setSelectedTypeId(lt.id)}
                >
                  <Text style={[styles.typeChipText, isSelected && styles.typeChipTextSelected]}>
                    {lt.name}
                  </Text>
                  {bal && (
                    <Text
                      style={[styles.typeChipBalance, isSelected && styles.typeChipBalanceSelected]}
                    >
                      {bal.available} avail.
                    </Text>
                  )}
                </TouchableOpacity>
              );
            })}
          </ScrollView>

          {/* Balance info */}
          {selectedBalance && (
            <View style={styles.balanceInfo}>
              <View style={styles.balanceItem}>
                <Text style={styles.balanceValue}>{selectedBalance.available}</Text>
                <Text style={styles.balanceLabel}>Available</Text>
              </View>
              <View style={styles.balanceSep} />
              <View style={styles.balanceItem}>
                <Text style={styles.balanceValue}>{selectedBalance.used}</Text>
                <Text style={styles.balanceLabel}>Used</Text>
              </View>
              <View style={styles.balanceSep} />
              <View style={styles.balanceItem}>
                <Text style={[styles.balanceValue, { color: COLORS.warning }]}>
                  {selectedBalance.pending}
                </Text>
                <Text style={styles.balanceLabel}>Pending</Text>
              </View>
            </View>
          )}
        </View>

        {/* Half Day toggle */}
        <View style={styles.field}>
          <View style={styles.switchRow}>
            <View>
              <Text style={styles.label}>Half Day</Text>
              <Text style={styles.fieldHint}>Apply for half a working day</Text>
            </View>
            <Switch
              value={isHalfDay}
              onValueChange={setIsHalfDay}
              trackColor={{ true: COLORS.blue }}
              thumbColor="#fff"
            />
          </View>
          {isHalfDay && (
            <View style={styles.halfDayPicker}>
              {(['MORNING', 'AFTERNOON'] as const).map((period) => (
                <TouchableOpacity
                  key={period}
                  style={[styles.periodBtn, halfDayPeriod === period && styles.periodBtnActive]}
                  onPress={() => setHalfDayPeriod(period)}
                >
                  <Text
                    style={[
                      styles.periodBtnText,
                      halfDayPeriod === period && styles.periodBtnTextActive,
                    ]}
                  >
                    {period === 'MORNING' ? '☀️ Morning' : '🌙 Afternoon'}
                  </Text>
                </TouchableOpacity>
              ))}
            </View>
          )}
        </View>

        {/* Dates */}
        {!isHalfDay ? (
          <>
            <View style={styles.field}>
              <Text style={styles.label}>Start Date *</Text>
              <TouchableOpacity
                style={styles.dateBtn}
                onPress={() => setShowStartPicker(true)}
              >
                <Ionicons name="calendar-outline" size={18} color={COLORS.blue} />
                <Text style={styles.dateBtnText}>{formatDate(toISODate(startDate), 'date')}</Text>
                <Ionicons name="chevron-down" size={16} color={COLORS.muted} />
              </TouchableOpacity>
              {showStartPicker && (
                <DateTimePicker
                  value={startDate}
                  mode="date"
                  minimumDate={new Date()}
                  onChange={(_, date) => {
                    setShowStartPicker(false);
                    if (date) {
                      setStartDate(date);
                      if (date > endDate) setEndDate(date);
                    }
                  }}
                />
              )}
            </View>
            <View style={styles.field}>
              <Text style={styles.label}>End Date *</Text>
              <TouchableOpacity
                style={styles.dateBtn}
                onPress={() => setShowEndPicker(true)}
              >
                <Ionicons name="calendar-outline" size={18} color={COLORS.blue} />
                <Text style={styles.dateBtnText}>{formatDate(toISODate(endDate), 'date')}</Text>
                <Ionicons name="chevron-down" size={16} color={COLORS.muted} />
              </TouchableOpacity>
              {showEndPicker && (
                <DateTimePicker
                  value={endDate}
                  mode="date"
                  minimumDate={startDate}
                  onChange={(_, date) => {
                    setShowEndPicker(false);
                    if (date) setEndDate(date);
                  }}
                />
              )}
            </View>
          </>
        ) : (
          <View style={styles.field}>
            <Text style={styles.label}>Date *</Text>
            <TouchableOpacity
              style={styles.dateBtn}
              onPress={() => setShowStartPicker(true)}
            >
              <Ionicons name="calendar-outline" size={18} color={COLORS.blue} />
              <Text style={styles.dateBtnText}>{formatDate(toISODate(startDate), 'date')}</Text>
              <Ionicons name="chevron-down" size={16} color={COLORS.muted} />
            </TouchableOpacity>
            {showStartPicker && (
              <DateTimePicker
                value={startDate}
                mode="date"
                minimumDate={new Date()}
                onChange={(_, date) => {
                  setShowStartPicker(false);
                  if (date) { setStartDate(date); setEndDate(date); }
                }}
              />
            )}
          </View>
        )}

        {/* Duration preview */}
        <View style={styles.durationPreview}>
          <Ionicons name="time-outline" size={16} color={COLORS.blue} />
          <Text style={styles.durationText}>
            Duration:{' '}
            <Text style={styles.durationBold}>
              {totalDays} {totalDays === 1 ? 'day' : 'days'}
            </Text>
          </Text>
          {selectedBalance && totalDays > selectedBalance.available && (
            <View style={styles.overBalance}>
              <Ionicons name="warning-outline" size={14} color={COLORS.error} />
              <Text style={styles.overBalanceText}>Exceeds balance</Text>
            </View>
          )}
        </View>

        {/* Reason */}
        <View style={styles.field}>
          <Text style={styles.label}>Reason</Text>
          <TextInput
            style={styles.textarea}
            placeholder="Optional: Provide a reason for your leave request"
            placeholderTextColor={COLORS.muted}
            value={reason}
            onChangeText={setReason}
            multiline
            numberOfLines={4}
            textAlignVertical="top"
          />
        </View>

        {/* Attachment — hidden until the storage upload flow exists (FEATURES.FILE_UPLOAD) */}
        {FEATURES.FILE_UPLOAD && (
        <View style={styles.field}>
          <Text style={styles.label}>Attachment</Text>
          <TouchableOpacity style={styles.uploadBtn} onPress={pickAttachment}>
            {attachment ? (
              <View style={styles.attachedFile}>
                <Ionicons name="document-outline" size={18} color={COLORS.blue} />
                <Text style={styles.attachedFileName} numberOfLines={1}>
                  {attachment.name}
                </Text>
                <TouchableOpacity onPress={() => setAttachment(null)}>
                  <Ionicons name="close-circle" size={18} color={COLORS.error} />
                </TouchableOpacity>
              </View>
            ) : (
              <>
                <Ionicons name="cloud-upload-outline" size={22} color={COLORS.muted} />
                <Text style={styles.uploadBtnText}>Upload document (PDF or image)</Text>
              </>
            )}
          </TouchableOpacity>
        </View>
        )}

        <View style={{ height: 32 }} />
      </ScrollView>
    </View>
  );
}

const styles = StyleSheet.create({
  container: { flex: 1, backgroundColor: COLORS.bg },
  centered: { flex: 1, alignItems: 'center', justifyContent: 'center' },
  header: {
    flexDirection: 'row', alignItems: 'center', justifyContent: 'space-between',
    paddingHorizontal: 16, paddingTop: Platform.OS === 'ios' ? 56 : 40, paddingBottom: 16,
    backgroundColor: COLORS.card,
    shadowColor: '#000', shadowOffset: { width: 0, height: 1 }, shadowOpacity: 0.05, shadowRadius: 3, elevation: 2,
  },
  headerTitle: { fontSize: 17, fontWeight: '700', color: COLORS.text },
  submitHeaderBtn: { paddingHorizontal: 16, paddingVertical: 8, backgroundColor: COLORS.blue, borderRadius: 20 },
  submitHeaderBtnText: { fontSize: 14, fontWeight: '700', color: '#fff' },
  form: { flex: 1 },
  formContent: { padding: 16 },
  field: { marginBottom: 20 },
  label: { fontSize: 14, fontWeight: '600', color: COLORS.text, marginBottom: 8 },
  fieldHint: { fontSize: 12, color: COLORS.muted, marginTop: 2 },
  typeChip: {
    paddingHorizontal: 16, paddingVertical: 10, borderRadius: 20, marginRight: 8,
    backgroundColor: COLORS.card, borderWidth: 1.5, borderColor: COLORS.border,
  },
  typeChipSelected: { backgroundColor: `${COLORS.blue}15`, borderColor: COLORS.blue },
  typeChipText: { fontSize: 14, fontWeight: '600', color: COLORS.text },
  typeChipTextSelected: { color: COLORS.blue },
  typeChipBalance: { fontSize: 11, color: COLORS.muted, marginTop: 1 },
  typeChipBalanceSelected: { color: COLORS.blue },
  balanceInfo: {
    flexDirection: 'row', backgroundColor: COLORS.card, borderRadius: 12,
    padding: 14, marginTop: 12,
    shadowColor: '#000', shadowOffset: { width: 0, height: 1 }, shadowOpacity: 0.05, shadowRadius: 3, elevation: 1,
  },
  balanceItem: { flex: 1, alignItems: 'center' },
  balanceValue: { fontSize: 20, fontWeight: '800', color: COLORS.text },
  balanceLabel: { fontSize: 11, color: COLORS.muted, marginTop: 2 },
  balanceSep: { width: 1, backgroundColor: COLORS.border, marginHorizontal: 8 },
  switchRow: {
    flexDirection: 'row', alignItems: 'center', justifyContent: 'space-between',
    backgroundColor: COLORS.card, borderRadius: 12, padding: 14,
  },
  halfDayPicker: { flexDirection: 'row', gap: 10, marginTop: 10 },
  periodBtn: {
    flex: 1, paddingVertical: 10, borderRadius: 10,
    backgroundColor: COLORS.card, borderWidth: 1.5, borderColor: COLORS.border,
    alignItems: 'center',
  },
  periodBtnActive: { backgroundColor: `${COLORS.blue}15`, borderColor: COLORS.blue },
  periodBtnText: { fontSize: 14, fontWeight: '600', color: COLORS.text },
  periodBtnTextActive: { color: COLORS.blue },
  dateBtn: {
    flexDirection: 'row', alignItems: 'center',
    backgroundColor: COLORS.card, borderRadius: 12, padding: 14, gap: 10,
    borderWidth: 1, borderColor: COLORS.border,
  },
  dateBtnText: { flex: 1, fontSize: 15, fontWeight: '600', color: COLORS.text },
  durationPreview: {
    flexDirection: 'row', alignItems: 'center',
    backgroundColor: `${COLORS.blue}10`, borderRadius: 10, padding: 12, gap: 8,
    marginBottom: 20,
  },
  durationText: { fontSize: 14, color: COLORS.text, flex: 1 },
  durationBold: { fontWeight: '700', color: COLORS.blue },
  overBalance: { flexDirection: 'row', alignItems: 'center', gap: 4 },
  overBalanceText: { fontSize: 12, color: COLORS.error, fontWeight: '600' },
  textarea: {
    backgroundColor: COLORS.card, borderRadius: 12, borderWidth: 1, borderColor: COLORS.border,
    padding: 14, fontSize: 15, color: COLORS.text, minHeight: 100,
  },
  uploadBtn: {
    flexDirection: 'row', alignItems: 'center', justifyContent: 'center',
    backgroundColor: COLORS.card, borderRadius: 12,
    borderWidth: 1.5, borderColor: COLORS.border, borderStyle: 'dashed',
    padding: 20, gap: 10,
  },
  uploadBtnText: { fontSize: 14, color: COLORS.muted },
  attachedFile: { flexDirection: 'row', alignItems: 'center', gap: 10, flex: 1 },
  attachedFileName: { flex: 1, fontSize: 14, color: COLORS.text, fontWeight: '500' },
});
