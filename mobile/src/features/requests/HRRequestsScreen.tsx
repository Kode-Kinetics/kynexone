import React, { useCallback, useEffect, useMemo, useState } from 'react';
import {
  ActivityIndicator,
  Alert,
  Modal,
  RefreshControl,
  ScrollView,
  StyleSheet,
  Text,
  TextInput,
  View,
} from 'react-native';
import { useNavigation } from '@react-navigation/native';
import { Ionicons } from '@expo/vector-icons';
import Animated, { FadeInDown, FadeOutUp } from 'react-native-reanimated';
import * as DocumentPicker from 'expo-document-picker';
import { documentsApi, hrRequestsApi } from '@/api/adapters';
import { normalizePickedFile, type PickedFile } from '@/api/services';
import { formatDate } from '@/utils/date';
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
} from '@/components/ui';
import type { HRRequest } from '@/types';

const requestTypes: {
  key: string;
  label: string;
  icon: React.ComponentProps<typeof Ionicons>['name'];
}[] = [
  { key: 'SalaryCertificate', label: 'Salary certificate', icon: 'document-text-outline' },
  { key: 'ExperienceLetter', label: 'Experience letter', icon: 'briefcase-outline' },
  { key: 'NOC', label: 'No objection certificate', icon: 'shield-checkmark-outline' },
  { key: 'Complaint', label: 'Complaint or grievance', icon: 'warning-outline' },
  { key: 'General', label: 'General HR ticket', icon: 'chatbubble-ellipses-outline' },
  { key: 'BankLetter', label: 'Bank letter', icon: 'card-outline' },
  { key: 'LeaveEncashment', label: 'Leave encashment', icon: 'cash-outline' },
  { key: 'DocumentRequest', label: 'Document request', icon: 'folder-open-outline' },
];

export default function HRRequestsScreen() {
  const navigation = useNavigation<any>();
  const { theme, reduceMotion } = useTheme();
  const [requests, setRequests] = useState<HRRequest[]>([]);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [refreshing, setRefreshing] = useState(false);
  const [createModal, setCreateModal] = useState(false);
  const [submitting, setSubmitting] = useState(false);
  const [form, setForm] = useState({
    requestType: requestTypes[0].key,
    subject: '',
    description: '',
  });
  const [attachment, setAttachment] = useState<PickedFile | null>(null);
  const [typePickerOpen, setTypePickerOpen] = useState(false);
  const [completionMessage, setCompletionMessage] = useState<string | null>(null);

  const fetchRequests = useCallback(async () => {
    setLoadError(null);
    try {
      const data = await hrRequestsApi.getMy({ page: 1, limit: 50 });
      setRequests(data.items || []);
    } catch (error: any) {
      setLoadError(error.message || 'Failed to load requests.');
    } finally {
      setLoading(false);
      setRefreshing(false);
    }
  }, []);

  useEffect(() => {
    void fetchRequests();
  }, [fetchRequests]);

  const stats = useMemo(() => ({
    total: requests.length,
    active: requests.filter((request) => ['Open', 'InProgress'].includes(request.status)).length,
    resolved: requests.filter((request) => ['Resolved', 'Closed'].includes(request.status)).length,
    atRisk: requests.filter((request) => ['AtRisk', 'Breached'].includes(request.slaStatus ?? '')).length,
  }), [requests]);

  const selectedType = requestTypes.find((type) => type.key === form.requestType) ?? requestTypes[0];

  const beginRequest = (requestType?: string) => {
    if (requestType) setForm((current) => ({ ...current, requestType }));
    setCreateModal(true);
  };

  const pickFile = async () => {
    try {
      const result = await DocumentPicker.getDocumentAsync({ copyToCacheDirectory: true });
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
      Alert.alert('File unavailable', 'Could not select this attachment.');
    }
  };

  const submitRequest = async () => {
    if (!form.subject.trim()) {
      Alert.alert('Subject required', 'Add a concise subject for HR.');
      return;
    }
    if (!form.description.trim()) {
      Alert.alert('Description required', 'Explain what you need so HR can respond efficiently.');
      return;
    }

    setSubmitting(true);
    try {
      const uploaded = attachment
        ? await documentsApi.uploadDocument({
            file: attachment,
            documentType: 'HR Request Attachment',
          })
        : null;
      await hrRequestsApi.create({
        requestType: selectedType.label,
        subject: form.subject.trim(),
        description: form.description.trim(),
        attachmentDocumentId: uploaded?.id,
      });
      setCreateModal(false);
      setForm({ requestType: requestTypes[0].key, subject: '', description: '' });
      setAttachment(null);
      setCompletionMessage('Request submitted. HR has received it and the SLA clock has started.');
      await fetchRequests();
    } catch (error: any) {
      Alert.alert('Submission failed', error.message || 'Could not submit this request.');
    } finally {
      setSubmitting(false);
    }
  };

  return (
    <View style={[styles.root, { backgroundColor: theme.colors.canvas }]}>
      <LiquidBackdrop subtle />
      <ScrollView
        contentContainerStyle={styles.content}
        refreshControl={
          <RefreshControl
            refreshing={refreshing}
            onRefresh={() => {
              setRefreshing(true);
              void fetchRequests();
            }}
            tintColor={theme.colors.primary}
            colors={[theme.colors.primary]}
          />
        }
        showsVerticalScrollIndicator={false}
      >
        <ScreenHero
          eyebrow="Employee helpdesk"
          title="HR requests"
          subtitle={`${stats.active} active · ${stats.resolved} resolved`}
          actions={
            <GlassIconButton
              icon="add"
              label="Create HR request"
              accent
              onPress={() => beginRequest()}
            />
          }
        />

        {completionMessage ? (
          <Animated.View
            entering={reduceMotion ? undefined : FadeInDown.duration(220)}
            exiting={reduceMotion ? undefined : FadeOutUp.duration(160)}
            style={styles.completionWrap}
          >
            <GlassSurface
              accessibilityRole="alert"
              accessibilityLiveRegion="polite"
              elevated={false}
              radius={theme.radius.lg}
              contentStyle={styles.completionBanner}
            >
              <Ionicons name="checkmark-circle" size={22} color={theme.colors.success} />
              <Text style={[theme.typography.caption, styles.completionText, { color: theme.colors.text }]}>
                {completionMessage}
              </Text>
              <MotionPressable
                accessibilityRole="button"
                accessibilityLabel="Dismiss request confirmation"
                onPress={() => setCompletionMessage(null)}
                haptic="selection"
                contentStyle={styles.completionDismiss}
              >
                <Ionicons name="close" size={18} color={theme.colors.textMuted} />
              </MotionPressable>
            </GlassSurface>
          </Animated.View>
        ) : null}

        {!loading ? (
          <View style={styles.section}>
            <ScrollView horizontal showsHorizontalScrollIndicator={false} contentContainerStyle={styles.metricRail}>
              <RequestMetric label="Total" value={stats.total} icon="albums-outline" accent={theme.colors.primary} />
              <RequestMetric label="Active" value={stats.active} icon="hourglass-outline" accent={theme.colors.warning} />
              <RequestMetric label="Resolved" value={stats.resolved} icon="checkmark-done-outline" accent={theme.colors.success} />
              <RequestMetric label="SLA risk" value={stats.atRisk} icon="warning-outline" accent={theme.colors.danger} />
            </ScrollView>
          </View>
        ) : null}

        <View style={styles.section}>
          <SectionHeader title="Quick request" subtitle="Start with a common HR service" />
          <ScrollView horizontal showsHorizontalScrollIndicator={false} contentContainerStyle={styles.quickRail}>
            {requestTypes.slice(0, 6).map((requestType) => (
              <MotionPressable
                key={requestType.key}
                onPress={() => beginRequest(requestType.key)}
                haptic="selection"
                style={styles.quickShell}
                contentStyle={styles.rounded}
              >
                <GlassSurface elevated={false} radius={theme.radius.xl} style={styles.quickSurface} contentStyle={styles.quickCard}>
                  <View style={[styles.quickIcon, { backgroundColor: `${theme.colors.primary}18` }]}>
                    <Ionicons name={requestType.icon} size={22} color={theme.colors.primary} />
                  </View>
                  <Text numberOfLines={2} style={[theme.typography.bodyStrong, styles.quickLabel, { color: theme.colors.text }]}>
                    {requestType.label}
                  </Text>
                  <Ionicons name="arrow-up-outline" size={15} color={theme.colors.textMuted} style={styles.quickArrow} />
                </GlassSurface>
              </MotionPressable>
            ))}
          </ScrollView>
        </View>

        <View style={styles.section}>
          <SectionHeader title="My requests" subtitle={`${requests.length} ticket${requests.length === 1 ? '' : 's'}`} />
          {loading ? (
            <GlassSurface radius={theme.radius.xl} contentStyle={styles.stateCard}>
              <ActivityIndicator color={theme.colors.primary} />
              <Text style={[theme.typography.caption, { color: theme.colors.textSecondary }]}>Loading HR tickets…</Text>
            </GlassSurface>
          ) : loadError ? (
            <GlassSurface radius={theme.radius.xl} contentStyle={styles.stateCard}>
              <Ionicons name="cloud-offline-outline" size={28} color={theme.colors.danger} />
              <Text style={[theme.typography.h3, { color: theme.colors.text }]}>HR requests unavailable</Text>
              <Text style={[theme.typography.caption, { color: theme.colors.textSecondary, textAlign: 'center' }]}>{loadError}</Text>
              <LiquidButton label="Try again" onPress={() => void fetchRequests()} />
            </GlassSurface>
          ) : requests.length === 0 ? (
            <EmptyRequests onCreate={() => beginRequest()} />
          ) : (
            <View style={styles.list}>
              {requests.map((request) => (
                <RequestCard
                  key={request.id}
                  request={request}
                  onPress={() => navigation.navigate('HRRequestDetail', { id: request.id })}
                />
              ))}
            </View>
          )}
        </View>
        <View style={styles.bottomSpacer} />
      </ScrollView>

      <CreateRequestModal
        visible={createModal}
        form={form}
        selectedType={selectedType}
        attachment={attachment}
        typePickerOpen={typePickerOpen}
        submitting={submitting}
        onFormChange={setForm}
        onTypePickerToggle={() => setTypePickerOpen((current) => !current)}
        onTypeSelect={(requestType) => {
          setForm((current) => ({ ...current, requestType }));
          setTypePickerOpen(false);
        }}
        onPickFile={() => void pickFile()}
        onRemoveFile={() => setAttachment(null)}
        onClose={() => {
          setCreateModal(false);
          setTypePickerOpen(false);
        }}
        onSubmit={() => void submitRequest()}
      />
    </View>
  );
}

function RequestMetric({
  label,
  value,
  icon,
  accent,
}: {
  label: string;
  value: number;
  icon: React.ComponentProps<typeof Ionicons>['name'];
  accent: string;
}) {
  const { theme } = useTheme();
  return (
    <GlassSurface elevated={false} radius={theme.radius.xl} style={styles.metricCard} contentStyle={styles.metricContent}>
      <View style={[styles.metricIcon, { backgroundColor: `${accent}18` }]}>
        <Ionicons name={icon} size={19} color={accent} />
      </View>
      <Text style={[styles.metricValue, { color: theme.colors.text }]}>{value}</Text>
      <Text style={[theme.typography.micro, { color: theme.colors.textMuted }]}>{label}</Text>
    </GlassSurface>
  );
}

function RequestCard({ request, onPress }: { request: HRRequest; onPress: () => void }) {
  const { theme } = useTheme();
  const type = requestTypes.find((item) => item.key === request.requestType)
    ?? requestTypes.find((item) => item.label.toLowerCase() === request.requestType.toLowerCase())
    ?? { key: request.requestType, label: request.requestType, icon: 'chatbubble-ellipses-outline' as const };
  const status = getRequestStatus(request.status, theme);
  const sla = getSlaStatus(request.slaStatus, theme);

  return (
    <MotionPressable
      onPress={onPress}
      haptic="selection"
      contentStyle={styles.rounded}
      accessibilityRole="button"
      accessibilityLabel={`${request.subject}. ${request.status}`}
    >
      <GlassSurface elevated={false} radius={theme.radius.xl} contentStyle={styles.requestCard}>
        <View style={[styles.requestIcon, { backgroundColor: `${theme.colors.primary}17` }]}>
          <Ionicons name={type.icon} size={22} color={theme.colors.primary} />
        </View>
        <View style={styles.requestCopy}>
          <Text numberOfLines={2} style={[theme.typography.bodyStrong, { color: theme.colors.text }]}>
            {request.subject}
          </Text>
          <Text numberOfLines={1} style={[theme.typography.caption, { color: theme.colors.textMuted, marginTop: 3 }]}>
            {type.label}{request.ticketNumber ? ` · #${request.ticketNumber}` : ''}
          </Text>
          <View style={styles.requestMetaRow}>
            <StatusPill label={status.label} color={status.color} />
            {sla ? <StatusPill label={sla.label} color={sla.color} /> : null}
            {(request.commentsCount ?? 0) > 0 ? (
              <View style={styles.commentCount}>
                <Ionicons name="chatbubble-outline" size={12} color={theme.colors.textMuted} />
                <Text style={[theme.typography.micro, { color: theme.colors.textMuted }]}>{request.commentsCount}</Text>
              </View>
            ) : null}
          </View>
          <Text style={[theme.typography.micro, { color: theme.colors.textMuted, marginTop: 7 }]}>
            Created {formatDate(request.createdAt, 'display')}
          </Text>
        </View>
        <Ionicons name="chevron-forward" size={18} color={theme.colors.textMuted} />
      </GlassSurface>
    </MotionPressable>
  );
}

function StatusPill({ label, color }: { label: string; color: string }) {
  const { theme } = useTheme();
  return (
    <View style={[styles.statusPill, { backgroundColor: `${color}16` }]}>
      <View style={[styles.statusDot, { backgroundColor: color }]} />
      <Text style={[theme.typography.micro, { color }]}>{label}</Text>
    </View>
  );
}

function EmptyRequests({ onCreate }: { onCreate: () => void }) {
  const { theme } = useTheme();
  return (
    <GlassSurface radius={theme.radius.xl} contentStyle={styles.emptyCard}>
      <View style={[styles.emptyIcon, { backgroundColor: `${theme.colors.primary}18` }]}>
        <Ionicons name="chatbubble-ellipses-outline" size={31} color={theme.colors.primary} />
      </View>
      <Text style={[theme.typography.h3, { color: theme.colors.text }]}>No requests yet</Text>
      <Text style={[theme.typography.caption, styles.emptyText, { color: theme.colors.textMuted }]}>
        Create certificates, letters, grievances or general support tickets and track every response here.
      </Text>
      <MotionPressable
        onPress={onCreate}
        haptic="selection"
        contentStyle={[styles.emptyAction, { backgroundColor: `${theme.colors.primary}18` }]}
      >
        <Ionicons name="add-circle-outline" size={18} color={theme.colors.primary} />
        <Text style={[theme.typography.caption, { color: theme.colors.primary, fontWeight: '700' }]}>Create request</Text>
      </MotionPressable>
    </GlassSurface>
  );
}

function CreateRequestModal({
  visible,
  form,
  selectedType,
  attachment,
  typePickerOpen,
  submitting,
  onFormChange,
  onTypePickerToggle,
  onTypeSelect,
  onPickFile,
  onRemoveFile,
  onClose,
  onSubmit,
}: {
  visible: boolean;
  form: { requestType: string; subject: string; description: string };
  selectedType: (typeof requestTypes)[number];
  attachment: PickedFile | null;
  typePickerOpen: boolean;
  submitting: boolean;
  onFormChange: React.Dispatch<React.SetStateAction<{ requestType: string; subject: string; description: string }>>;
  onTypePickerToggle: () => void;
  onTypeSelect: (value: string) => void;
  onPickFile: () => void;
  onRemoveFile: () => void;
  onClose: () => void;
  onSubmit: () => void;
}) {
  const { theme } = useTheme();

  return (
    <Modal visible={visible} transparent animationType="slide" onRequestClose={onClose}>
      <View style={[styles.modalBackdrop, { backgroundColor: theme.colors.overlay }]}>
        <GlassSurface
          radius={theme.radius.xxl}
          style={styles.modalSheet}
          contentStyle={styles.modalContent}
          tintColor={theme.isDark ? 'rgba(10,26,52,0.95)' : 'rgba(255,255,255,0.95)'}
        >
          <View style={[styles.modalHandle, { backgroundColor: theme.colors.border }]} />
          <Text style={[theme.typography.h2, { color: theme.colors.text }]}>New HR request</Text>
          <Text style={[theme.typography.caption, { color: theme.colors.textMuted, marginTop: 5 }]}>
            Give HR enough context to resolve the request without unnecessary follow-up.
          </Text>

          <ScrollView style={styles.modalScroll} showsVerticalScrollIndicator={false} keyboardShouldPersistTaps="handled">
            <Text style={[theme.typography.caption, styles.fieldLabel, { color: theme.colors.textSecondary }]}>Request type</Text>
            <MotionPressable
              onPress={onTypePickerToggle}
              haptic="selection"
              contentStyle={[styles.selectButton, { backgroundColor: theme.colors.surfaceSoft, borderColor: theme.colors.border }]}
            >
              <View style={[styles.selectIcon, { backgroundColor: `${theme.colors.primary}18` }]}>
                <Ionicons name={selectedType.icon} size={19} color={theme.colors.primary} />
              </View>
              <Text style={[theme.typography.bodyStrong, { color: theme.colors.text, flex: 1 }]}>{selectedType.label}</Text>
              <Ionicons name={typePickerOpen ? 'chevron-up' : 'chevron-down'} size={18} color={theme.colors.textMuted} />
            </MotionPressable>

            {typePickerOpen ? (
              <GlassSurface elevated={false} radius={theme.radius.lg} style={styles.typePicker} contentStyle={styles.typePickerContent}>
                {requestTypes.map((requestType, index) => (
                  <MotionPressable
                    key={requestType.key}
                    onPress={() => onTypeSelect(requestType.key)}
                    haptic="selection"
                    contentStyle={[
                      styles.typeOption,
                      index < requestTypes.length - 1 && {
                        borderBottomColor: theme.colors.divider,
                        borderBottomWidth: StyleSheet.hairlineWidth,
                      },
                    ]}
                  >
                    <Ionicons name={requestType.icon} size={18} color={theme.colors.primary} />
                    <Text
                      style={[
                        theme.typography.caption,
                        { color: form.requestType === requestType.key ? theme.colors.primary : theme.colors.text, flex: 1 },
                      ]}
                    >
                      {requestType.label}
                    </Text>
                    {form.requestType === requestType.key ? (
                      <Ionicons name="checkmark" size={17} color={theme.colors.primary} />
                    ) : null}
                  </MotionPressable>
                ))}
              </GlassSurface>
            ) : null}

            <Text style={[theme.typography.caption, styles.fieldLabel, { color: theme.colors.textSecondary }]}>Subject</Text>
            <TextInput
              value={form.subject}
              onChangeText={(subject) => onFormChange((current) => ({ ...current, subject }))}
              placeholder="Brief summary of what you need"
              placeholderTextColor={theme.colors.textMuted}
              selectionColor={theme.colors.primary}
              style={[
                theme.typography.body,
                styles.input,
                { color: theme.colors.text, backgroundColor: theme.colors.surfaceSoft, borderColor: theme.colors.border },
              ]}
            />

            <Text style={[theme.typography.caption, styles.fieldLabel, { color: theme.colors.textSecondary }]}>Description</Text>
            <TextInput
              value={form.description}
              onChangeText={(description) => onFormChange((current) => ({ ...current, description }))}
              placeholder="Describe the request, relevant dates and expected outcome"
              placeholderTextColor={theme.colors.textMuted}
              selectionColor={theme.colors.primary}
              multiline
              numberOfLines={5}
              textAlignVertical="top"
              style={[
                theme.typography.body,
                styles.textarea,
                { color: theme.colors.text, backgroundColor: theme.colors.surfaceSoft, borderColor: theme.colors.border },
              ]}
            />

            {FEATURES.FILE_UPLOAD ? (
              <>
                <Text style={[theme.typography.caption, styles.fieldLabel, { color: theme.colors.textSecondary }]}>Attachment</Text>
                {attachment ? (
                  <View style={[styles.fileSelected, { backgroundColor: theme.colors.surfaceSoft }]}>
                    <View style={[styles.fileIcon, { backgroundColor: `${theme.colors.primary}18` }]}>
                      <Ionicons name="document-attach-outline" size={21} color={theme.colors.primary} />
                    </View>
                    <View style={styles.fileCopy}>
                      <Text numberOfLines={1} style={[theme.typography.bodyStrong, { color: theme.colors.text }]}>{attachment.name}</Text>
                      <Text style={[theme.typography.micro, { color: theme.colors.textMuted, marginTop: 2 }]}>Ready to upload</Text>
                    </View>
                    <MotionPressable onPress={onRemoveFile} haptic="selection" contentStyle={styles.removeFile} accessibilityRole="button" accessibilityLabel={`Remove ${attachment.name}`}>
                      <Ionicons name="close" size={19} color={theme.colors.danger} />
                    </MotionPressable>
                  </View>
                ) : (
                  <MotionPressable
                    onPress={onPickFile}
                    haptic="selection"
                    contentStyle={[styles.filePicker, { backgroundColor: theme.colors.surfaceSoft, borderColor: theme.colors.border }]}
                  >
                    <Ionicons name="cloud-upload-outline" size={24} color={theme.colors.primary} />
                    <View style={styles.fileCopy}>
                      <Text style={[theme.typography.bodyStrong, { color: theme.colors.text }]}>Attach supporting file</Text>
                      <Text style={[theme.typography.micro, { color: theme.colors.textMuted, marginTop: 2 }]}>Optional</Text>
                    </View>
                    <Ionicons name="add-circle-outline" size={20} color={theme.colors.primary} />
                  </MotionPressable>
                )}
              </>
            ) : null}
          </ScrollView>

          <View style={styles.modalActions}>
            <MotionPressable
              onPress={onClose}
              haptic="selection"
              style={styles.modalSecondaryShell}
              contentStyle={[styles.modalSecondary, { borderColor: theme.colors.border }]}
            >
              <Text style={[theme.typography.bodyStrong, { color: theme.colors.textSecondary }]}>Cancel</Text>
            </MotionPressable>
            <LiquidButton
              label="Submit request"
              icon="paper-plane-outline"
              onPress={onSubmit}
              loading={submitting}
              disabled={submitting}
              style={styles.modalPrimary}
            />
          </View>
        </GlassSurface>
      </View>
    </Modal>
  );
}

function getRequestStatus(status: string, theme: ReturnType<typeof useTheme>['theme']) {
  const map: Record<string, { label: string; color: string }> = {
    Open: { label: 'Open', color: theme.colors.primary },
    InProgress: { label: 'In progress', color: theme.colors.warning },
    Resolved: { label: 'Resolved', color: theme.colors.success },
    Closed: { label: 'Closed', color: theme.colors.textMuted },
    Cancelled: { label: 'Cancelled', color: theme.colors.textMuted },
  };
  return map[status] ?? { label: status, color: theme.colors.primary };
}

function getSlaStatus(status: string | undefined, theme: ReturnType<typeof useTheme>['theme']) {
  if (!status) return null;
  if (status === 'Breached') return { label: 'SLA breached', color: theme.colors.danger };
  if (status === 'AtRisk') return { label: 'SLA at risk', color: theme.colors.warning };
  return { label: 'SLA on time', color: theme.colors.success };
}

const styles = StyleSheet.create({
  root: { flex: 1 },
  content: { paddingBottom: 36 },
  section: { paddingHorizontal: 16, marginTop: 16 },
  completionWrap: { paddingHorizontal: 16, marginTop: 12 },
  completionBanner: { minHeight: 52, flexDirection: 'row', alignItems: 'center', gap: 10, paddingLeft: 14, paddingRight: 6, paddingVertical: 6 },
  completionText: { flex: 1 },
  completionDismiss: { width: 44, height: 44, alignItems: 'center', justifyContent: 'center' },
  metricRail: { gap: 9, paddingRight: 4 },
  metricCard: { width: 116, minHeight: 124 },
  metricContent: { padding: 14, justifyContent: 'space-between' },
  metricIcon: { width: 39, height: 39, borderRadius: 14, alignItems: 'center', justifyContent: 'center' },
  metricValue: { fontSize: 25, lineHeight: 29, fontWeight: '800', marginTop: 8 },
  quickRail: { gap: 9, paddingRight: 4 },
  quickShell: { width: 148, minHeight: 138 },
  rounded: { flex: 1, borderRadius: 24 },
  quickSurface: { flex: 1 },
  quickCard: { flex: 1, padding: 14 },
  quickIcon: { width: 42, height: 42, borderRadius: 15, alignItems: 'center', justifyContent: 'center' },
  quickLabel: { marginTop: 12, paddingRight: 12 },
  quickArrow: { position: 'absolute', top: 14, right: 14, transform: [{ rotate: '45deg' }] },
  stateCard: { minHeight: 180, alignItems: 'center', justifyContent: 'center', gap: 12, padding: 24 },
  list: { gap: 9 },
  requestCard: { minHeight: 112, flexDirection: 'row', alignItems: 'center', gap: 12, padding: 14 },
  requestIcon: { width: 50, height: 50, borderRadius: 17, alignItems: 'center', justifyContent: 'center' },
  requestCopy: { flex: 1, minWidth: 0 },
  requestMetaRow: { flexDirection: 'row', flexWrap: 'wrap', alignItems: 'center', gap: 6, marginTop: 8 },
  statusPill: { flexDirection: 'row', alignItems: 'center', gap: 5, paddingHorizontal: 8, paddingVertical: 4, borderRadius: 999 },
  statusDot: { width: 6, height: 6, borderRadius: 3 },
  commentCount: { flexDirection: 'row', alignItems: 'center', gap: 4 },
  emptyCard: { minHeight: 240, alignItems: 'center', justifyContent: 'center', gap: 9, padding: 24 },
  emptyIcon: { width: 64, height: 64, borderRadius: 22, alignItems: 'center', justifyContent: 'center', marginBottom: 3 },
  emptyText: { textAlign: 'center', maxWidth: 300 },
  emptyAction: { flexDirection: 'row', alignItems: 'center', gap: 7, paddingHorizontal: 13, paddingVertical: 9, borderRadius: 14, marginTop: 5 },
  modalBackdrop: { flex: 1, justifyContent: 'flex-end' },
  modalSheet: { maxHeight: '92%', borderBottomLeftRadius: 0, borderBottomRightRadius: 0 },
  modalContent: { paddingHorizontal: 20, paddingTop: 12, paddingBottom: 30 },
  modalHandle: { width: 44, height: 5, borderRadius: 999, alignSelf: 'center', marginBottom: 17 },
  modalScroll: { marginTop: 17 },
  fieldLabel: { marginTop: 14, marginBottom: 7, fontWeight: '700' },
  selectButton: { minHeight: 56, borderWidth: StyleSheet.hairlineWidth, borderRadius: 17, flexDirection: 'row', alignItems: 'center', gap: 10, paddingHorizontal: 11 },
  selectIcon: { width: 37, height: 37, borderRadius: 13, alignItems: 'center', justifyContent: 'center' },
  typePicker: { maxHeight: 300, marginTop: 7 },
  typePickerContent: { paddingHorizontal: 12 },
  typeOption: { minHeight: 49, flexDirection: 'row', alignItems: 'center', gap: 10 },
  input: { minHeight: 54, borderWidth: StyleSheet.hairlineWidth, borderRadius: 17, paddingHorizontal: 13 },
  textarea: { minHeight: 118, borderWidth: StyleSheet.hairlineWidth, borderRadius: 17, paddingHorizontal: 13, paddingVertical: 12 },
  filePicker: { minHeight: 72, borderWidth: StyleSheet.hairlineWidth, borderStyle: 'dashed', borderRadius: 17, flexDirection: 'row', alignItems: 'center', gap: 12, paddingHorizontal: 13 },
  fileSelected: { minHeight: 72, borderRadius: 17, flexDirection: 'row', alignItems: 'center', gap: 11, paddingHorizontal: 12 },
  fileIcon: { width: 43, height: 43, borderRadius: 15, alignItems: 'center', justifyContent: 'center' },
  fileCopy: { flex: 1, minWidth: 0 },
  removeFile: { width: 44, height: 44, borderRadius: 14, alignItems: 'center', justifyContent: 'center' },
  modalActions: { flexDirection: 'row', gap: 10, marginTop: 18 },
  modalSecondaryShell: { flex: 1 },
  modalSecondary: { minHeight: 56, borderRadius: 18, borderWidth: StyleSheet.hairlineWidth, alignItems: 'center', justifyContent: 'center' },
  modalPrimary: { flex: 1 },
  bottomSpacer: { height: 12 },
});
