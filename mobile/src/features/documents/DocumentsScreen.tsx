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
import { Ionicons } from '@expo/vector-icons';
import * as DocumentPicker from 'expo-document-picker';
import * as FileSystem from 'expo-file-system/legacy';
import * as Sharing from 'expo-sharing';
import { documentsApi } from '@/api/adapters';
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
import type { EmployeeDocument } from '@/types';

const documentTypes = [
  'Passport',
  'Visa',
  'Iqama',
  'Emirates ID',
  'Work Permit',
  'Educational Certificate',
  'Professional Certificate',
  'Medical Certificate',
  'Contract',
  'Other',
];

function daysUntilExpiry(date?: string | null): number | null {
  if (!date) return null;
  return Math.ceil((new Date(date).getTime() - Date.now()) / 86_400_000);
}

export default function DocumentsScreen() {
  const { theme } = useTheme();
  const [documents, setDocuments] = useState<EmployeeDocument[]>([]);
  const [loading, setLoading] = useState(true);
  const [refreshing, setRefreshing] = useState(false);
  const [uploadModal, setUploadModal] = useState(false);
  const [uploadData, setUploadData] = useState({
    documentType: documentTypes[0],
    expiryDate: '',
    documentNumber: '',
  });
  const [selectedFile, setSelectedFile] = useState<PickedFile | null>(null);
  const [uploading, setUploading] = useState(false);
  const [typePickerOpen, setTypePickerOpen] = useState(false);
  const [downloadingId, setDownloadingId] = useState<string | null>(null);

  const fetchDocuments = useCallback(async () => {
    try {
      const data = await documentsApi.getDocuments();
      setDocuments(Array.isArray(data) ? data : (data as { items?: EmployeeDocument[] }).items || []);
    } catch (error: any) {
      Alert.alert('Documents unavailable', error.message || 'Failed to load documents.');
    } finally {
      setLoading(false);
      setRefreshing(false);
    }
  }, []);

  useEffect(() => {
    void fetchDocuments();
  }, [fetchDocuments]);

  const expiring = useMemo(
    () => documents.filter((document) => {
      const days = daysUntilExpiry(document.expiryDate);
      return days !== null && days <= 60;
    }),
    [documents],
  );

  const verifiedCount = useMemo(
    () => documents.filter((document) => ['Verified', 'Approved'].includes(document.verificationStatus ?? document.status)).length,
    [documents],
  );

  const downloadDocument = async (document: EmployeeDocument) => {
    setDownloadingId(document.id);
    try {
      const { url, headers } = await documentsApi.downloadDocument(document.id);
      const safeName = (document.fileName ?? `document-${document.id}`).replace(/[^\w.\-]+/g, '_');
      const result = await FileSystem.downloadAsync(
        url,
        `${FileSystem.cacheDirectory}${safeName}`,
        { headers },
      );
      if (result.status !== 200) throw new Error(`Download failed (HTTP ${result.status})`);
      if (await Sharing.isAvailableAsync()) await Sharing.shareAsync(result.uri);
      else Alert.alert('Downloaded', `Saved to ${result.uri}`);
    } catch (error: any) {
      Alert.alert('Could not open document', error.message || 'Please try again.');
    } finally {
      setDownloadingId(null);
    }
  };

  const pickFile = async () => {
    try {
      const result = await DocumentPicker.getDocumentAsync({
        type: ['application/pdf', 'image/*'],
        copyToCacheDirectory: true,
      });
      if (!result.canceled && result.assets?.[0]) {
        const asset = result.assets[0];
        setSelectedFile(normalizePickedFile({
          uri: asset.uri,
          name: asset.name,
          mimeType: asset.mimeType,
          size: asset.size,
        }));
      }
    } catch {
      Alert.alert('File unavailable', 'Could not select this file.');
    }
  };

  const submitUpload = async () => {
    if (!selectedFile) {
      Alert.alert('File required', 'Select a PDF or image first.');
      return;
    }
    setUploading(true);
    try {
      await documentsApi.uploadDocument({
        file: selectedFile,
        documentType: uploadData.documentType,
        expiryDate: uploadData.expiryDate || undefined,
        documentNumber: uploadData.documentNumber.trim() || undefined,
      });
      setUploadModal(false);
      setSelectedFile(null);
      setUploadData({ documentType: documentTypes[0], expiryDate: '', documentNumber: '' });
      Alert.alert('Document uploaded', 'HR can now verify the new document.');
      await fetchDocuments();
    } catch (error: any) {
      Alert.alert('Upload failed', error.message || 'Please try again.');
    } finally {
      setUploading(false);
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
              void fetchDocuments();
            }}
            tintColor={theme.colors.primary}
            colors={[theme.colors.primary]}
          />
        }
        showsVerticalScrollIndicator={false}
      >
        <ScreenHero
          eyebrow="Employee records"
          title="Documents"
          subtitle={`${documents.length} record${documents.length === 1 ? '' : 's'} · ${verifiedCount} verified`}
          actions={
            FEATURES.FILE_UPLOAD ? (
              <GlassIconButton
                icon="cloud-upload-outline"
                label="Upload document"
                accent
                onPress={() => setUploadModal(true)}
              />
            ) : undefined
          }
        />

        {!loading ? (
          <View style={styles.section}>
            <ScrollView horizontal showsHorizontalScrollIndicator={false} contentContainerStyle={styles.metricRail}>
              <DocumentMetric label="Total" value={documents.length} icon="folder-open-outline" accent={theme.colors.primary} />
              <DocumentMetric label="Verified" value={verifiedCount} icon="shield-checkmark-outline" accent={theme.colors.success} />
              <DocumentMetric label="Attention" value={expiring.length} icon="warning-outline" accent={theme.colors.warning} />
            </ScrollView>
          </View>
        ) : null}

        {expiring.length ? (
          <View style={styles.section}>
            <SectionHeader title="Needs attention" subtitle="Expiring or expired documents" />
            <GlassSurface elevated={false} radius={theme.radius.xl} contentStyle={styles.alertCard}>
              {expiring.map((document, index) => (
                <ExpiryAlert
                  key={document.id}
                  document={document}
                  isLast={index === expiring.length - 1}
                  onPress={() => void downloadDocument(document)}
                />
              ))}
            </GlassSurface>
          </View>
        ) : null}

        <View style={styles.section}>
          <SectionHeader title="Document vault" subtitle="Secure employee records" />
          {loading ? (
            <GlassSurface radius={theme.radius.xl} contentStyle={styles.stateCard}>
              <ActivityIndicator color={theme.colors.primary} />
              <Text style={[theme.typography.caption, { color: theme.colors.textSecondary }]}>Loading document vault…</Text>
            </GlassSurface>
          ) : documents.length === 0 ? (
            <EmptyDocuments onUpload={FEATURES.FILE_UPLOAD ? () => setUploadModal(true) : undefined} />
          ) : (
            <View style={styles.list}>
              {documents.map((document) => (
                <DocumentCard
                  key={document.id}
                  document={document}
                  loading={downloadingId === document.id}
                  onOpen={() => void downloadDocument(document)}
                />
              ))}
            </View>
          )}
        </View>
        <View style={styles.bottomSpacer} />
      </ScrollView>

      <UploadDocumentModal
        visible={uploadModal}
        data={uploadData}
        file={selectedFile}
        typePickerOpen={typePickerOpen}
        uploading={uploading}
        onDataChange={setUploadData}
        onTypePickerToggle={() => setTypePickerOpen((current) => !current)}
        onTypeSelect={(documentType) => {
          setUploadData((current) => ({ ...current, documentType }));
          setTypePickerOpen(false);
        }}
        onPickFile={() => void pickFile()}
        onRemoveFile={() => setSelectedFile(null)}
        onClose={() => {
          setUploadModal(false);
          setTypePickerOpen(false);
        }}
        onSubmit={() => void submitUpload()}
      />
    </View>
  );
}

function DocumentMetric({
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

function ExpiryAlert({
  document,
  isLast,
  onPress,
}: {
  document: EmployeeDocument;
  isLast: boolean;
  onPress: () => void;
}) {
  const { theme } = useTheme();
  const days = daysUntilExpiry(document.expiryDate);
  const expired = days !== null && days < 0;
  const accent = expired ? theme.colors.danger : theme.colors.warning;
  return (
    <MotionPressable onPress={onPress} haptic="selection" contentStyle={styles.rounded}>
      <View
        style={[
          styles.alertRow,
          !isLast && { borderBottomColor: theme.colors.divider, borderBottomWidth: StyleSheet.hairlineWidth },
        ]}
      >
        <View style={[styles.alertIcon, { backgroundColor: `${accent}18` }]}>
          <Ionicons name={expired ? 'alert-circle-outline' : 'time-outline'} size={19} color={accent} />
        </View>
        <View style={styles.alertCopy}>
          <Text style={[theme.typography.bodyStrong, { color: theme.colors.text }]}>{document.documentType}</Text>
          <Text style={[theme.typography.caption, { color: accent, marginTop: 2 }]}>
            {expired ? 'Expired' : `${days} days remaining`} · {document.expiryDate ? formatDate(document.expiryDate, 'display') : 'No date'}
          </Text>
        </View>
        <Ionicons name="arrow-forward" size={17} color={theme.colors.textMuted} />
      </View>
    </MotionPressable>
  );
}

function DocumentCard({
  document,
  loading,
  onOpen,
}: {
  document: EmployeeDocument;
  loading: boolean;
  onOpen: () => void;
}) {
  const { theme } = useTheme();
  const status = document.verificationStatus ?? document.status;
  const statusMeta = getStatusMeta(status, theme);
  const expiryMeta = getExpiryMeta(document.expiryDate, theme);

  return (
    <MotionPressable
      onPress={FEATURES.DOCUMENT_DOWNLOAD ? onOpen : undefined}
      disabled={!FEATURES.DOCUMENT_DOWNLOAD || loading}
      haptic="selection"
      contentStyle={styles.rounded}
      accessibilityRole={FEATURES.DOCUMENT_DOWNLOAD ? 'button' : undefined}
      accessibilityLabel={`Open ${document.documentType}`}
    >
      <GlassSurface elevated={false} radius={theme.radius.xl} contentStyle={styles.documentCard}>
        <View style={[styles.documentIcon, { backgroundColor: `${theme.colors.primary}16` }]}>
          <Ionicons name={getDocumentIcon(document.documentType)} size={23} color={theme.colors.primary} />
        </View>
        <View style={styles.documentCopy}>
          <Text style={[theme.typography.bodyStrong, { color: theme.colors.text }]}>{document.documentType}</Text>
          {document.documentNumber ? (
            <Text style={[theme.typography.micro, { color: theme.colors.textMuted, marginTop: 3 }]}>
              #{document.documentNumber}
            </Text>
          ) : null}
          <View style={styles.documentMetaRow}>
            <StatusPill label={status || 'Uploaded'} color={statusMeta} />
            {expiryMeta ? <StatusPill label={expiryMeta.label} color={expiryMeta.color} /> : null}
          </View>
          {document.fileName ? (
            <Text numberOfLines={1} style={[theme.typography.micro, { color: theme.colors.textMuted, marginTop: 6 }]}>
              {document.fileName}
            </Text>
          ) : null}
        </View>
        {loading ? (
          <ActivityIndicator color={theme.colors.primary} size="small" />
        ) : (
          <View style={[styles.openButton, { backgroundColor: `${theme.colors.primary}15` }]}>
            <Ionicons name="open-outline" size={18} color={theme.colors.primary} />
          </View>
        )}
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

function EmptyDocuments({ onUpload }: { onUpload?: () => void }) {
  const { theme } = useTheme();
  return (
    <GlassSurface radius={theme.radius.xl} contentStyle={styles.emptyCard}>
      <View style={[styles.emptyIcon, { backgroundColor: `${theme.colors.primary}18` }]}>
        <Ionicons name="folder-open-outline" size={32} color={theme.colors.primary} />
      </View>
      <Text style={[theme.typography.h3, { color: theme.colors.text }]}>No documents yet</Text>
      <Text style={[theme.typography.caption, styles.emptyText, { color: theme.colors.textMuted }]}>
        Identity, visa, contract and certificate records will appear here after HR adds or verifies them.
      </Text>
      {onUpload ? (
        <MotionPressable
          onPress={onUpload}
          haptic="selection"
          contentStyle={[styles.emptyAction, { backgroundColor: `${theme.colors.primary}18` }]}
        >
          <Ionicons name="cloud-upload-outline" size={17} color={theme.colors.primary} />
          <Text style={[theme.typography.caption, { color: theme.colors.primary, fontWeight: '700' }]}>Upload document</Text>
        </MotionPressable>
      ) : null}
    </GlassSurface>
  );
}

function UploadDocumentModal({
  visible,
  data,
  file,
  typePickerOpen,
  uploading,
  onDataChange,
  onTypePickerToggle,
  onTypeSelect,
  onPickFile,
  onRemoveFile,
  onClose,
  onSubmit,
}: {
  visible: boolean;
  data: { documentType: string; expiryDate: string; documentNumber: string };
  file: PickedFile | null;
  typePickerOpen: boolean;
  uploading: boolean;
  onDataChange: React.Dispatch<React.SetStateAction<{ documentType: string; expiryDate: string; documentNumber: string }>>;
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
          <Text style={[theme.typography.h2, { color: theme.colors.text }]}>Upload document</Text>
          <Text style={[theme.typography.caption, { color: theme.colors.textMuted, marginTop: 5 }]}>
            PDF, JPG and PNG files are supported.
          </Text>

          <ScrollView showsVerticalScrollIndicator={false} keyboardShouldPersistTaps="handled" style={styles.modalScroll}>
            <Text style={[theme.typography.caption, styles.fieldLabel, { color: theme.colors.textSecondary }]}>Document type</Text>
            <MotionPressable
              onPress={onTypePickerToggle}
              haptic="selection"
              contentStyle={[styles.selectButton, { backgroundColor: theme.colors.surfaceSoft, borderColor: theme.colors.border }]}
            >
              <View style={[styles.selectIcon, { backgroundColor: `${theme.colors.primary}18` }]}>
                <Ionicons name="document-outline" size={18} color={theme.colors.primary} />
              </View>
              <Text style={[theme.typography.bodyStrong, { color: theme.colors.text, flex: 1 }]}>{data.documentType}</Text>
              <Ionicons name={typePickerOpen ? 'chevron-up' : 'chevron-down'} size={18} color={theme.colors.textMuted} />
            </MotionPressable>

            {typePickerOpen ? (
              <GlassSurface elevated={false} radius={theme.radius.lg} style={styles.typePicker} contentStyle={styles.typePickerContent}>
                {documentTypes.map((documentType, index) => (
                  <MotionPressable
                    key={documentType}
                    onPress={() => onTypeSelect(documentType)}
                    haptic="selection"
                    contentStyle={[
                      styles.typeOption,
                      index < documentTypes.length - 1 && {
                        borderBottomColor: theme.colors.divider,
                        borderBottomWidth: StyleSheet.hairlineWidth,
                      },
                    ]}
                  >
                    <Text
                      style={[
                        theme.typography.caption,
                        { color: data.documentType === documentType ? theme.colors.primary : theme.colors.text },
                      ]}
                    >
                      {documentType}
                    </Text>
                    {data.documentType === documentType ? (
                      <Ionicons name="checkmark" size={17} color={theme.colors.primary} />
                    ) : null}
                  </MotionPressable>
                ))}
              </GlassSurface>
            ) : null}

            <Text style={[theme.typography.caption, styles.fieldLabel, { color: theme.colors.textSecondary }]}>Document number</Text>
            <TextInput
              value={data.documentNumber}
              onChangeText={(documentNumber) => onDataChange((current) => ({ ...current, documentNumber }))}
              placeholder="Optional reference number"
              placeholderTextColor={theme.colors.textMuted}
              selectionColor={theme.colors.primary}
              style={[
                theme.typography.body,
                styles.input,
                { color: theme.colors.text, backgroundColor: theme.colors.surfaceSoft, borderColor: theme.colors.border },
              ]}
            />

            <Text style={[theme.typography.caption, styles.fieldLabel, { color: theme.colors.textSecondary }]}>Expiry date</Text>
            <TextInput
              value={data.expiryDate}
              onChangeText={(expiryDate) => onDataChange((current) => ({ ...current, expiryDate }))}
              placeholder="YYYY-MM-DD (optional)"
              placeholderTextColor={theme.colors.textMuted}
              selectionColor={theme.colors.primary}
              autoCapitalize="none"
              style={[
                theme.typography.body,
                styles.input,
                { color: theme.colors.text, backgroundColor: theme.colors.surfaceSoft, borderColor: theme.colors.border },
              ]}
            />

            <Text style={[theme.typography.caption, styles.fieldLabel, { color: theme.colors.textSecondary }]}>File</Text>
            {file ? (
              <View style={[styles.fileSelected, { backgroundColor: theme.colors.surfaceSoft }]}>
                <View style={[styles.fileIcon, { backgroundColor: `${theme.colors.primary}18` }]}>
                  <Ionicons name="document-attach-outline" size={21} color={theme.colors.primary} />
                </View>
                <View style={styles.fileCopy}>
                  <Text numberOfLines={1} style={[theme.typography.bodyStrong, { color: theme.colors.text }]}>{file.name}</Text>
                  <Text style={[theme.typography.micro, { color: theme.colors.textMuted, marginTop: 2 }]}>Ready to upload securely</Text>
                </View>
                <MotionPressable onPress={onRemoveFile} haptic="selection" contentStyle={styles.removeFile}>
                  <Ionicons name="close" size={19} color={theme.colors.danger} />
                </MotionPressable>
              </View>
            ) : (
              <MotionPressable
                onPress={onPickFile}
                haptic="selection"
                contentStyle={[styles.filePicker, { backgroundColor: theme.colors.surfaceSoft, borderColor: theme.colors.border }]}
              >
                <Ionicons name="cloud-upload-outline" size={28} color={theme.colors.primary} />
                <Text style={[theme.typography.bodyStrong, { color: theme.colors.text, marginTop: 8 }]}>Choose document</Text>
                <Text style={[theme.typography.micro, { color: theme.colors.textMuted, marginTop: 3 }]}>PDF or image</Text>
              </MotionPressable>
            )}
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
              label="Upload"
              icon="cloud-upload-outline"
              onPress={onSubmit}
              loading={uploading}
              disabled={uploading || !file}
              style={styles.modalPrimary}
            />
          </View>
        </GlassSurface>
      </View>
    </Modal>
  );
}

function getDocumentIcon(type: string): React.ComponentProps<typeof Ionicons>['name'] {
  const normalized = type.toLowerCase();
  if (normalized.includes('passport')) return 'book-outline';
  if (normalized.includes('visa') || normalized.includes('permit')) return 'earth-outline';
  if (normalized.includes('certificate')) return 'ribbon-outline';
  if (normalized.includes('contract')) return 'document-text-outline';
  if (normalized.includes('medical')) return 'medkit-outline';
  return 'document-outline';
}

function getStatusMeta(status: string, theme: ReturnType<typeof useTheme>['theme']) {
  if (['Verified', 'Approved'].includes(status)) return theme.colors.success;
  if (status === 'Pending') return theme.colors.warning;
  if (status === 'Rejected') return theme.colors.danger;
  if (status === 'Uploaded') return theme.colors.primary;
  return theme.colors.textMuted;
}

function getExpiryMeta(date: string | null | undefined, theme: ReturnType<typeof useTheme>['theme']) {
  const days = daysUntilExpiry(date);
  if (days === null) return null;
  if (days < 0) return { label: 'Expired', color: theme.colors.danger };
  if (days <= 30) return { label: `${days}d left`, color: theme.colors.danger };
  if (days <= 60) return { label: `${days}d left`, color: theme.colors.warning };
  return { label: 'Valid', color: theme.colors.success };
}

const styles = StyleSheet.create({
  root: { flex: 1 },
  content: { paddingBottom: 36 },
  section: { paddingHorizontal: 16, marginTop: 16 },
  metricRail: { gap: 9, paddingRight: 4 },
  metricCard: { width: 122, minHeight: 126 },
  metricContent: { padding: 14, justifyContent: 'space-between' },
  metricIcon: { width: 39, height: 39, borderRadius: 14, alignItems: 'center', justifyContent: 'center' },
  metricValue: { fontSize: 26, lineHeight: 30, fontWeight: '800', marginTop: 8 },
  alertCard: { paddingHorizontal: 14 },
  alertRow: { minHeight: 70, flexDirection: 'row', alignItems: 'center', gap: 11, paddingVertical: 10 },
  alertIcon: { width: 42, height: 42, borderRadius: 15, alignItems: 'center', justifyContent: 'center' },
  alertCopy: { flex: 1 },
  list: { gap: 9 },
  rounded: { borderRadius: 24 },
  stateCard: { minHeight: 180, alignItems: 'center', justifyContent: 'center', gap: 12, padding: 24 },
  documentCard: { minHeight: 104, flexDirection: 'row', alignItems: 'center', gap: 12, padding: 14 },
  documentIcon: { width: 50, height: 50, borderRadius: 17, alignItems: 'center', justifyContent: 'center' },
  documentCopy: { flex: 1, minWidth: 0 },
  documentMetaRow: { flexDirection: 'row', alignItems: 'center', flexWrap: 'wrap', gap: 6, marginTop: 7 },
  statusPill: { flexDirection: 'row', alignItems: 'center', gap: 5, paddingHorizontal: 8, paddingVertical: 4, borderRadius: 999 },
  statusDot: { width: 6, height: 6, borderRadius: 3 },
  openButton: { width: 41, height: 41, borderRadius: 15, alignItems: 'center', justifyContent: 'center' },
  emptyCard: { minHeight: 235, alignItems: 'center', justifyContent: 'center', gap: 9, padding: 24 },
  emptyIcon: { width: 64, height: 64, borderRadius: 22, alignItems: 'center', justifyContent: 'center', marginBottom: 3 },
  emptyText: { textAlign: 'center', maxWidth: 300 },
  emptyAction: { flexDirection: 'row', alignItems: 'center', gap: 7, paddingHorizontal: 13, paddingVertical: 9, borderRadius: 14, marginTop: 5 },
  modalBackdrop: { flex: 1, justifyContent: 'flex-end' },
  modalSheet: { maxHeight: '92%', borderBottomLeftRadius: 0, borderBottomRightRadius: 0 },
  modalContent: { paddingHorizontal: 20, paddingTop: 12, paddingBottom: 30 },
  modalHandle: { width: 44, height: 5, borderRadius: 999, alignSelf: 'center', marginBottom: 17 },
  modalScroll: { marginTop: 18 },
  fieldLabel: { marginTop: 14, marginBottom: 7, fontWeight: '700' },
  selectButton: { minHeight: 56, borderWidth: StyleSheet.hairlineWidth, borderRadius: 17, flexDirection: 'row', alignItems: 'center', gap: 10, paddingHorizontal: 11 },
  selectIcon: { width: 37, height: 37, borderRadius: 13, alignItems: 'center', justifyContent: 'center' },
  typePicker: { maxHeight: 260, marginTop: 7 },
  typePickerContent: { paddingHorizontal: 12 },
  typeOption: { minHeight: 47, flexDirection: 'row', alignItems: 'center', justifyContent: 'space-between', gap: 10 },
  input: { minHeight: 54, borderWidth: StyleSheet.hairlineWidth, borderRadius: 17, paddingHorizontal: 13 },
  filePicker: { minHeight: 130, borderWidth: StyleSheet.hairlineWidth, borderStyle: 'dashed', borderRadius: 18, alignItems: 'center', justifyContent: 'center', padding: 16 },
  fileSelected: { minHeight: 74, borderRadius: 17, flexDirection: 'row', alignItems: 'center', gap: 11, paddingHorizontal: 12 },
  fileIcon: { width: 43, height: 43, borderRadius: 15, alignItems: 'center', justifyContent: 'center' },
  fileCopy: { flex: 1, minWidth: 0 },
  removeFile: { width: 40, height: 40, borderRadius: 14, alignItems: 'center', justifyContent: 'center' },
  modalActions: { flexDirection: 'row', gap: 10, marginTop: 18 },
  modalSecondaryShell: { flex: 1 },
  modalSecondary: { minHeight: 56, borderRadius: 18, borderWidth: StyleSheet.hairlineWidth, alignItems: 'center', justifyContent: 'center' },
  modalPrimary: { flex: 1 },
  bottomSpacer: { height: 12 },
});
