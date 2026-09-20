import React, { useCallback, useEffect, useMemo, useState } from 'react';
import {
  ActivityIndicator,
  Alert,
  ScrollView,
  StyleSheet,
  Switch,
  Text,
  TextInput,
  View,
} from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import DateTimePicker from '@react-native-community/datetimepicker';
import * as DocumentPicker from 'expo-document-picker';
import {
  documentsApi,
  leaveApi,
  normalizePickedFile,
  type PickedFile,
} from '@/api/services';
import { formatDate, toISODate } from '@/utils/date';
import { FEATURES } from '@/config/features';
import { useTheme } from '@/theme/ThemeProvider';
import {
  GlassIconButton,
  GlassSurface,
  LiquidBackdrop,
  LiquidButton,
  MotionPressable,
  ScreenHero,
  SectionHeader,
  SwipeDeck,
} from '@/components/ui';
import type { LeaveBalance } from '@/types';

interface Props {
  navigation: any;
}

type HalfDayPeriod = 'MORNING' | 'AFTERNOON';

export default function ApplyLeaveScreen({ navigation }: Props) {
  const { theme } = useTheme();
  const [leaveTypes, setLeaveTypes] = useState<{ id: string; name: string }[]>([]);
  const [balances, setBalances] = useState<LeaveBalance[]>([]);
  const [loading, setLoading] = useState(true);
  const [submitting, setSubmitting] = useState(false);
  const [selectedTypeId, setSelectedTypeId] = useState('');
  const [startDate, setStartDate] = useState(new Date());
  const [endDate, setEndDate] = useState(new Date());
  const [isHalfDay, setIsHalfDay] = useState(false);
  const [halfDayPeriod, setHalfDayPeriod] = useState<HalfDayPeriod>('MORNING');
  const [reason, setReason] = useState('');
  const [attachment, setAttachment] = useState<PickedFile | null>(null);
  const [showStartPicker, setShowStartPicker] = useState(false);
  const [showEndPicker, setShowEndPicker] = useState(false);
  const loadData = useCallback(async () => {
    setLoading(true);
    try {
      const [types, currentBalances] = await Promise.all([
        leaveApi.getLeaveTypes(),
        leaveApi.getLeaveBalances(),
      ]);
      setLeaveTypes(types);
      setBalances(currentBalances);
      setSelectedTypeId((current) => current || types[0]?.id || '');
    } catch (error) {
      console.error('[ApplyLeave] load failed:', error);
      Alert.alert('Leave unavailable', 'Could not load leave types and balances.');
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => {
    void loadData();
  }, [loadData]);

  const pickAttachment = async () => {
    try {
      const result = await DocumentPicker.getDocumentAsync({
        type: ['application/pdf', 'image/*'],
        copyToCacheDirectory: true,
      });
      if (!result.canceled && result.assets?.[0]) {
        const file = result.assets[0];
        setAttachment(normalizePickedFile({
          uri: file.uri,
          name: file.name,
          mimeType: file.mimeType,
          size: file.size,
        }));
      }
    } catch {
      Alert.alert('Attachment unavailable', 'Could not select this file.');
    }
  };

  const selectedBalance = useMemo(
    () => balances.find((balance) => balance.leaveTypeId === selectedTypeId),
    [balances, selectedTypeId],
  );

  const totalDays = useMemo(() => {
    if (isHalfDay) return 0.5;
    return Math.max(
      1,
      Math.floor((endDate.getTime() - startDate.getTime()) / 86_400_000) + 1,
    );
  }, [endDate, isHalfDay, startDate]);

  const exceedsBalance = !!selectedBalance && totalDays > selectedBalance.available;
  const submit = async () => {
    if (!selectedTypeId) {
      Alert.alert('Leave type required', 'Choose the leave type you want to request.');
      return;
    }
    if (endDate < startDate) {
      Alert.alert('Check the dates', 'End date cannot be before start date.');
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

      Alert.alert('Request submitted', 'Your leave request is now in the approval workflow.', [
        { text: 'Done', onPress: () => navigation.goBack() },
      ]);
    } catch (error: any) {
      Alert.alert(
        'Submission failed',
        error?.response?.data?.message ?? 'Could not submit the request. Please try again.',
      );
    } finally {
      setSubmitting(false);
    }
  };

  const selectStartDate = (date?: Date) => {
    setShowStartPicker(false);
    if (!date) return;
    setStartDate(date);
    if (date > endDate || isHalfDay) setEndDate(date);
  };

  if (loading) {
    return (
      <View style={[styles.loadingRoot, { backgroundColor: theme.colors.canvas }]}>
        <LiquidBackdrop subtle />
        <GlassSurface radius={theme.radius.xl} style={styles.loadingCard} contentStyle={styles.loadingContent}>
          <ActivityIndicator color={theme.colors.primary} />
          <Text style={[theme.typography.caption, { color: theme.colors.textSecondary }]}>Loading leave balances…</Text>
        </GlassSurface>
      </View>
    );
  }
  return (
    <View style={[styles.root, { backgroundColor: theme.colors.canvas }]}>
      <LiquidBackdrop subtle />
      <ScrollView contentContainerStyle={styles.content} showsVerticalScrollIndicator={false}>
        <ScreenHero
          eyebrow="Time off"
          title="Apply for leave"
          subtitle="Plan your absence and send it through the approval workflow"
          actions={
            navigation.canGoBack() ? (
              <GlassIconButton icon="close" label="Close" onPress={() => navigation.goBack()} />
            ) : undefined
          }
        />

        <View style={styles.deckSection}>
          <SectionHeader title="Leave request" subtitle="Swipe through the three steps" />
          <SwipeDeck minHeight={545}>
            <View style={styles.formPage}>
              <SectionHeader title="1 · Leave type" subtitle="Choose a balance to use" />
          <ScrollView horizontal showsHorizontalScrollIndicator={false} contentContainerStyle={styles.typeRail}>
            {leaveTypes.map((leaveType) => {
              const balance = balances.find((item) => item.leaveTypeId === leaveType.id);
              const selected = selectedTypeId === leaveType.id;
              return (
                <MotionPressable
                  key={leaveType.id}
                  onPress={() => setSelectedTypeId(leaveType.id)}
                  haptic="selection"
                  contentStyle={[
                    styles.typeChip,
                    {
                      backgroundColor: selected ? `${theme.colors.primary}1F` : theme.colors.surfaceSoft,
                      borderColor: selected ? theme.colors.primary : theme.colors.border,
                    },
                  ]}
                  accessibilityRole="radio"
                  accessibilityState={{ selected }}
                >
                  <Ionicons
                    name={selected ? 'checkmark-circle' : 'ellipse-outline'}
                    size={18}
                    color={selected ? theme.colors.primary : theme.colors.textMuted}
                  />
                  <View>
                    <Text
                      style={[
                        theme.typography.bodyStrong,
                        { color: selected ? theme.colors.primary : theme.colors.text },
                      ]}
                    >
                      {leaveType.name}
                    </Text>
                    {balance ? (
                      <Text style={[theme.typography.micro, { color: theme.colors.textMuted, marginTop: 2 }]}>
                        {balance.available} {balance.unit.toLowerCase()} available
                      </Text>
                    ) : null}
                  </View>
                </MotionPressable>
              );
            })}
          </ScrollView>
        {selectedBalance ? (
          <View style={styles.pageBlock}>
            <GlassSurface elevated={false} radius={theme.radius.xl} contentStyle={styles.balanceCard}>
              <BalanceMetric label="Available" value={selectedBalance.available} accent={theme.colors.success} />
              <View style={[styles.balanceDivider, { backgroundColor: theme.colors.divider }]} />
              <BalanceMetric label="Used" value={selectedBalance.used} accent={theme.colors.textSecondary} />
              <View style={[styles.balanceDivider, { backgroundColor: theme.colors.divider }]} />
              <BalanceMetric label="Pending" value={selectedBalance.pending} accent={theme.colors.warning} />
            </GlassSurface>
          </View>
        ) : null}
            </View>

            <View style={styles.formPage}>
              <SectionHeader title="2 · Duration" subtitle="Choose days and dates" />
          <GlassSurface elevated={false} radius={theme.radius.xl} contentStyle={styles.durationCard}>
            <View style={styles.switchRow}>
              <View style={styles.switchCopy}>
                <Text style={[theme.typography.bodyStrong, { color: theme.colors.text }]}>Half day</Text>
                <Text style={[theme.typography.caption, { color: theme.colors.textMuted, marginTop: 2 }]}>
                  Request a morning or afternoon only
                </Text>
              </View>
              <Switch
                value={isHalfDay}
                onValueChange={setIsHalfDay}
                trackColor={{ false: theme.colors.border, true: theme.colors.primary }}
                thumbColor="#FFFFFF"
              />
            </View>

            {isHalfDay ? (
              <View style={styles.periodRow}>
                {(['MORNING', 'AFTERNOON'] as const).map((period) => {
                  const selected = period === halfDayPeriod;
                  return (
                    <MotionPressable
                      key={period}
                      onPress={() => setHalfDayPeriod(period)}
                      haptic="selection"
                      style={styles.periodShell}
                      contentStyle={[
                        styles.periodButton,
                        {
                          backgroundColor: selected ? `${theme.colors.primary}1F` : theme.colors.surfaceSoft,
                          borderColor: selected ? theme.colors.primary : theme.colors.border,
                        },
                      ]}
                    >
                      <Ionicons
                        name={period === 'MORNING' ? 'sunny-outline' : 'moon-outline'}
                        size={19}
                        color={selected ? theme.colors.primary : theme.colors.textMuted}
                      />
                      <Text
                        style={[
                          theme.typography.caption,
                          { color: selected ? theme.colors.primary : theme.colors.textSecondary, fontWeight: '700' },
                        ]}
                      >
                        {period === 'MORNING' ? 'Morning' : 'Afternoon'}
                      </Text>
                    </MotionPressable>
                  );
                })}
              </View>
            ) : null}
          </GlassSurface>
        <View style={styles.pageBlock}>
          <SectionHeader title="Dates" subtitle={isHalfDay ? 'Select the day' : 'Select the start and end dates'} />
          <View style={styles.dateGrid}>
            <DateField
              label={isHalfDay ? 'Date' : 'Start date'}
              value={startDate}
              onPress={() => setShowStartPicker(true)}
            />
            {!isHalfDay ? (
              <DateField
                label="End date"
                value={endDate}
                onPress={() => setShowEndPicker(true)}
              />
            ) : null}
          </View>
          {showStartPicker ? (
            <DateTimePicker
              value={startDate}
              mode="date"
              minimumDate={new Date()}
              onChange={(_, date) => selectStartDate(date)}
            />
          ) : null}
          {showEndPicker ? (
            <DateTimePicker
              value={endDate}
              mode="date"
              minimumDate={startDate}
              onChange={(_, date) => {
                setShowEndPicker(false);
                if (date) setEndDate(date);
              }}
            />
          ) : null}
        </View>

        <View style={styles.pageBlock}>
          <GlassSurface
            elevated={false}
            radius={theme.radius.xl}
            contentStyle={styles.durationPreview}
            tintColor={theme.isDark ? 'rgba(43,82,178,0.22)' : 'rgba(255,255,255,0.45)'}
          >
            <View style={[styles.durationIcon, { backgroundColor: `${theme.colors.primary}1C` }]}>
              <Ionicons name="hourglass-outline" size={22} color={theme.colors.primary} />
            </View>
            <View style={styles.durationCopy}>
              <Text style={[theme.typography.caption, { color: theme.colors.textMuted }]}>Requested duration</Text>
              <Text style={[theme.typography.h2, { color: theme.colors.text, marginTop: 2 }]}>
                {totalDays} {totalDays === 1 ? 'day' : 'days'}
              </Text>
            </View>
            {exceedsBalance ? (
              <View style={[styles.balanceWarning, { backgroundColor: `${theme.colors.danger}18` }]}>
                <Ionicons name="warning-outline" size={14} color={theme.colors.danger} />
                <Text style={[theme.typography.micro, { color: theme.colors.danger }]}>Over balance</Text>
              </View>
            ) : (
              <Ionicons name="checkmark-circle" size={24} color={theme.colors.success} />
            )}
          </GlassSurface>
        </View>
            </View>

            <View style={styles.formPage}>
              <SectionHeader title="3 · Details" subtitle="Add context and submit" />
          <GlassSurface elevated={false} radius={theme.radius.xl} contentStyle={styles.detailsCard}>
            <Text style={[theme.typography.caption, styles.fieldLabel, { color: theme.colors.textSecondary }]}>Reason</Text>
            <TextInput
              value={reason}
              onChangeText={setReason}
              multiline
              numberOfLines={4}
              textAlignVertical="top"
              placeholder="Add a short reason for your request"
              placeholderTextColor={theme.colors.textMuted}
              selectionColor={theme.colors.primary}
              style={[
                styles.textarea,
                theme.typography.body,
                {
                  color: theme.colors.text,
                  backgroundColor: theme.colors.surfaceSoft,
                  borderColor: theme.colors.border,
                },
              ]}
            />

            {FEATURES.FILE_UPLOAD ? (
              <View style={styles.attachmentBlock}>
                <Text style={[theme.typography.caption, styles.fieldLabel, { color: theme.colors.textSecondary }]}>
                  Supporting document
                </Text>
                {attachment ? (
                  <View style={[styles.attachmentSelected, { backgroundColor: theme.colors.surfaceSoft }]}>
                    <View style={[styles.fileIcon, { backgroundColor: `${theme.colors.primary}18` }]}>
                      <Ionicons name="document-outline" size={20} color={theme.colors.primary} />
                    </View>
                    <View style={styles.attachmentCopy}>
                      <Text numberOfLines={1} style={[theme.typography.bodyStrong, { color: theme.colors.text }]}>
                        {attachment.name}
                      </Text>
                      <Text style={[theme.typography.micro, { color: theme.colors.textMuted, marginTop: 2 }]}>Ready to upload</Text>
                    </View>
                    <MotionPressable
                      onPress={() => setAttachment(null)}
                      haptic="selection"
                      contentStyle={styles.removeFile}
                      accessibilityLabel="Remove attachment"
                    >
                      <Ionicons name="close" size={19} color={theme.colors.danger} />
                    </MotionPressable>
                  </View>
                ) : (
                  <MotionPressable
                    onPress={() => void pickAttachment()}
                    haptic="selection"
                    contentStyle={[
                      styles.attachmentPicker,
                      { borderColor: theme.colors.border, backgroundColor: theme.colors.surfaceSoft },
                    ]}
                  >
                    <Ionicons name="cloud-upload-outline" size={23} color={theme.colors.primary} />
                    <View style={styles.attachmentCopy}>
                      <Text style={[theme.typography.bodyStrong, { color: theme.colors.text }]}>Add document</Text>
                      <Text style={[theme.typography.micro, { color: theme.colors.textMuted, marginTop: 2 }]}>PDF or image</Text>
                    </View>
                    <Ionicons name="add-circle-outline" size={20} color={theme.colors.primary} />
                  </MotionPressable>
                )}
              </View>
            ) : null}
          </GlassSurface>

        <View style={styles.pageBlock}>
          <LiquidButton
            label={submitting ? 'Submitting request…' : 'Submit leave request'}
            icon="paper-plane-outline"
            onPress={() => void submit()}
            loading={submitting}
            disabled={submitting || !selectedTypeId}
          />
          <Text style={[theme.typography.micro, styles.submitNote, { color: theme.colors.textMuted }]}>
            Your manager and HR will be notified through the configured approval workflow.
          </Text>
        </View>
            </View>
          </SwipeDeck>
        </View>
      </ScrollView>
    </View>
  );
}
function BalanceMetric({ label, value, accent }: { label: string; value: number; accent: string }) {
  const { theme } = useTheme();
  return (
    <View style={styles.balanceMetric}>
      <Text style={[styles.balanceValue, { color: accent }]}>{value}</Text>
      <Text style={[theme.typography.micro, { color: theme.colors.textMuted }]}>{label}</Text>
    </View>
  );
}

function DateField({ label, value, onPress }: { label: string; value: Date; onPress: () => void }) {
  const { theme } = useTheme();
  return (
    <View style={styles.dateField}>
      <Text style={[theme.typography.caption, styles.fieldLabel, { color: theme.colors.textSecondary }]}>
        {label}
      </Text>
      <MotionPressable
        onPress={onPress}
        haptic="selection"
        contentStyle={[
          styles.dateButton,
          { backgroundColor: theme.colors.surface, borderColor: theme.colors.border },
        ]}
      >
        <View style={[styles.dateIcon, { backgroundColor: `${theme.colors.primary}18` }]}>
          <Ionicons name="calendar-outline" size={19} color={theme.colors.primary} />
        </View>
        <Text style={[theme.typography.bodyStrong, { color: theme.colors.text, flex: 1 }]}>
          {formatDate(toISODate(value), 'date')}
        </Text>
        <Ionicons name="chevron-down" size={17} color={theme.colors.textMuted} />
      </MotionPressable>
    </View>
  );
}

const styles = StyleSheet.create({
  root: { flex: 1 },
  content: { paddingBottom: 38 },
  loadingRoot: { flex: 1, alignItems: 'center', justifyContent: 'center', padding: 24 },
  loadingCard: { width: '100%', maxWidth: 320, minHeight: 170 },
  loadingContent: { alignItems: 'center', justifyContent: 'center', gap: 12, padding: 24 },
  section: { paddingHorizontal: 16, marginTop: 16 },
  deckSection: { marginTop: 16 },
  formPage: { paddingHorizontal: 16, paddingTop: 2 },
  pageBlock: { marginTop: 16 },
  typeRail: { gap: 9, paddingRight: 4 },
  typeChip: {
    minWidth: 164,
    minHeight: 72,
    flexDirection: 'row',
    alignItems: 'center',
    gap: 10,
    borderWidth: StyleSheet.hairlineWidth,
    borderRadius: 19,
    paddingHorizontal: 14,
    paddingVertical: 11,
  },
  balanceCard: { flexDirection: 'row', alignItems: 'stretch', paddingVertical: 14 },
  balanceMetric: { flex: 1, alignItems: 'center', gap: 3 },
  balanceValue: { fontSize: 23, lineHeight: 28, fontWeight: '800' },
  balanceDivider: { width: StyleSheet.hairlineWidth },
  durationCard: { padding: 15 },
  switchRow: { flexDirection: 'row', alignItems: 'center', gap: 12 },
  switchCopy: { flex: 1 },
  periodRow: { flexDirection: 'row', gap: 9, marginTop: 14 },
  periodShell: { flex: 1 },
  periodButton: {
    minHeight: 52,
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'center',
    gap: 7,
    borderRadius: 16,
    borderWidth: StyleSheet.hairlineWidth,
  },
  dateGrid: { gap: 12 },
  dateField: { flex: 1 },
  fieldLabel: { marginBottom: 7, fontWeight: '700' },
  dateButton: {
    minHeight: 60,
    borderWidth: StyleSheet.hairlineWidth,
    borderRadius: 18,
    flexDirection: 'row',
    alignItems: 'center',
    gap: 11,
    paddingHorizontal: 12,
  },
  dateIcon: { width: 38, height: 38, borderRadius: 14, alignItems: 'center', justifyContent: 'center' },
  durationPreview: { flexDirection: 'row', alignItems: 'center', gap: 13, padding: 15 },
  durationIcon: { width: 48, height: 48, borderRadius: 17, alignItems: 'center', justifyContent: 'center' },
  durationCopy: { flex: 1 },
  balanceWarning: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: 4,
    paddingHorizontal: 8,
    paddingVertical: 6,
    borderRadius: 999,
  },
  detailsCard: { padding: 15 },
  textarea: {
    minHeight: 112,
    borderWidth: StyleSheet.hairlineWidth,
    borderRadius: 17,
    paddingHorizontal: 14,
    paddingVertical: 12,
  },
  attachmentBlock: { marginTop: 16 },
  attachmentPicker: {
    minHeight: 72,
    flexDirection: 'row',
    alignItems: 'center',
    gap: 12,
    borderRadius: 17,
    borderWidth: StyleSheet.hairlineWidth,
    borderStyle: 'dashed',
    paddingHorizontal: 14,
  },
  attachmentSelected: {
    minHeight: 72,
    flexDirection: 'row',
    alignItems: 'center',
    gap: 12,
    borderRadius: 17,
    paddingHorizontal: 12,
  },
  fileIcon: { width: 40, height: 40, borderRadius: 14, alignItems: 'center', justifyContent: 'center' },
  attachmentCopy: { flex: 1, minWidth: 0 },
  removeFile: { width: 40, height: 40, borderRadius: 14, alignItems: 'center', justifyContent: 'center' },
  submitSection: { paddingHorizontal: 16, marginTop: 22 },
  submitNote: { textAlign: 'center', marginTop: 10, paddingHorizontal: 12 },
});
