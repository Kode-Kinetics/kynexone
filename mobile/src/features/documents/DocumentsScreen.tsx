import React, { useCallback, useMemo, useState } from 'react';
import { Platform, RefreshControl, ScrollView, StyleSheet, Text, TextInput, View } from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import { useFocusEffect } from '@react-navigation/native';
import { useTranslation } from 'react-i18next';
import * as DocumentPicker from 'expo-document-picker';
import * as FileSystem from 'expo-file-system/legacy';
import * as Sharing from 'expo-sharing';
import DateTimePicker, { type DateTimePickerEvent } from '@react-native-community/datetimepicker';
import { documentsApi } from '@/api/adapters';
import { normalizePickedFile, type PickedFile } from '@/api/services';
import { FEATURES } from '@/config/features';
import { useTheme } from '@/theme/ThemeProvider';
import { useFormat } from '@/utils/format';
import { haptics } from '@/utils/haptics';
import {
  BottomSheet,
  GlassIconButton,
  LiquidBackdrop,
  LiquidButton,
  MotionPressable,
  ScreenHero,
  SkeletonRow,
  StateCard,
  StatusPill,
  Surface,
} from '@/components/ui';
import { daysBetween } from '@/features/approvals/teamModel';
import { useManagerText } from '@/features/approvals/useManagerText';
import type { EmployeeDocument } from '@/types';

/** Document types: `value` is what the API stores (English, read by HR on the web); `key` is the label. */
const DOCUMENT_TYPES = [
  { key: 'iqama', value: 'Iqama' },
  { key: 'nationalId', value: 'National ID' },
  { key: 'passport', value: 'Passport' },
  { key: 'visa', value: 'Visa' },
  { key: 'workPermit', value: 'Work Permit' },
  { key: 'contract', value: 'Contract' },
  { key: 'education', value: 'Educational Certificate' },
  { key: 'professional', value: 'Professional Certificate' },
  { key: 'medical', value: 'Medical Certificate' },
  { key: 'emiratesId', value: 'Emirates ID' },
  { key: 'other', value: 'Other' },
] as const;

const EXTRA_TYPE_KEYS: Record<string, string> = { 'hr request attachment': 'hrAttachment' };
const ATTENTION_DAYS = 60;

type Tone = 'success' | 'warning' | 'danger' | 'info' | 'neutral';

function useDocText() {
  const { t } = useTranslation();
  const tx = t as unknown as (k: string, o?: Record<string, unknown>) => string;
  return {
    type: (value: string) => {
      const v = value.trim().toLowerCase();
      const key = DOCUMENT_TYPES.find((d) => d.value.toLowerCase() === v)?.key ?? EXTRA_TYPE_KEYS[v];
      return key ? tx(`manager.docs.types.${key}`) : value;
    },
    review: (doc: EmployeeDocument): { tone: Tone; label: string } => {
      const v = (doc.verificationStatus ?? '').toLowerCase();
      if (v === 'approved' || v === 'verified') return { tone: 'success', label: tx('manager.docs.status.verified') };
      if (v === 'pending') return { tone: 'info', label: tx('manager.docs.status.pending') };
      if (v === 'rejected') return { tone: 'danger', label: tx('manager.docs.status.rejected') };
      return { tone: 'neutral', label: tx('manager.docs.status.uploaded') };
    },
  };
}

function localIsoDate(d: Date): string {
  const p = (n: number) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())}`;
}

export default function DocumentsScreen() {
  const { theme } = useTheme();
  const { t } = useTranslation();
  const f = useFormat();
  const docText = useDocText();
  const mtext = useManagerText();
  const [documents, setDocuments] = useState<EmployeeDocument[] | null>(null);
  const [loadFailed, setLoadFailed] = useState(false);
  const [refreshing, setRefreshing] = useState(false);
  const [uploadOpen, setUploadOpen] = useState(false);
  const [openingId, setOpeningId] = useState<string | null>(null);
  const [notice, setNotice] = useState<{ ok: boolean; text: string } | null>(null);
  const today = f.businessDate() ?? localIsoDate(new Date());

  const fetchDocuments = useCallback(async () => {
    try {
      const data = await documentsApi.getDocuments();
      setDocuments(Array.isArray(data) ? data : (data as { items?: EmployeeDocument[] }).items ?? []);
      setLoadFailed(false);
    } catch {
      setLoadFailed(true);
    } finally {
      setRefreshing(false);
    }
  }, []);

  useFocusEffect(
    useCallback(() => {
      void fetchDocuments();
    }, [fetchDocuments]),
  );

  const daysLeft = useCallback((doc: EmployeeDocument) => (doc.expiryDate ? daysBetween(today, doc.expiryDate) : null), [today]);
  const attention = useMemo(
    () => (documents ?? [])
      .filter((d) => {
        const left = daysLeft(d);
        return left !== null && left <= ATTENTION_DAYS;
      })
      .sort((a, b) => (daysLeft(a) ?? 0) - (daysLeft(b) ?? 0)),
    [daysLeft, documents],
  );
  const verified = (documents ?? []).filter((d) => docText.review(d).tone === 'success').length;

  const openDocument = async (doc: EmployeeDocument) => {
    if (!FEATURES.DOCUMENT_DOWNLOAD || openingId) return;
    setOpeningId(doc.id);
    setNotice(null);
    let uri: string | null = null;
    try {
      const { url, headers } = await documentsApi.downloadDocument(doc.id);
      const safeName = (doc.fileName ?? `document-${doc.id}`).replace(/[^\w.-]+/g, '_');
      const result = await FileSystem.downloadAsync(url, `${FileSystem.cacheDirectory}${safeName}`, { headers });
      uri = result.uri;
      if (result.status !== 200) throw new Error(String(result.status));
      if (await Sharing.isAvailableAsync()) await Sharing.shareAsync(result.uri);
    } catch {
      haptics.error();
      setNotice({ ok: false, text: t('manager.docs.openError') });
    } finally {
      // Personal documents never linger on the device after they are shared.
      if (uri) await FileSystem.deleteAsync(uri, { idempotent: true }).catch(() => undefined);
      setOpeningId(null);
    }
  };

  const expiryLine = (doc: EmployeeDocument) => {
    const left = daysLeft(doc);
    if (left === null || !doc.expiryDate) return null;
    if (left < 0) return { tone: 'danger' as const, text: t('manager.docs.expired', { date: f.date(doc.expiryDate, 'medium') }) };
    const days = mtext.days(left);
    return {
      tone: left <= ATTENTION_DAYS ? ('warning' as const) : ('neutral' as const),
      text: t('manager.docs.expires', { date: f.date(doc.expiryDate, 'medium'), left: days }),
    };
  };

  const renderRow = (doc: EmployeeDocument, last: boolean) => {
    const review = docText.review(doc);
    const expiry = expiryLine(doc);
    const name = docText.type(doc.documentType);
    return (
      <MotionPressable
        key={doc.id}
        accessibilityRole={FEATURES.DOCUMENT_DOWNLOAD ? 'button' : undefined}
        accessibilityLabel={[t('manager.docs.open', { name }), review.label, expiry?.text].filter(Boolean).join('. ')}
        accessibilityState={{ busy: openingId === doc.id }}
        disabled={!FEATURES.DOCUMENT_DOWNLOAD || openingId !== null}
        onPress={() => void openDocument(doc)}
      >
        <View style={[styles.row, !last && { borderBottomColor: theme.colors.hairline, borderBottomWidth: StyleSheet.hairlineWidth }]}>
          <Ionicons accessible={false} name="document-outline" size={22} color={theme.colors.accent} />
          <View style={styles.flex}>
            <Text style={[theme.typography.headline, { color: theme.colors.text }]}>{name}</Text>
            {doc.documentNumber ? (
              <Text style={[theme.typography.footnote, { color: theme.colors.textMuted }]}>{t('manager.docs.number', { number: doc.documentNumber })}</Text>
            ) : null}
            {expiry ? (
              <Text style={[theme.typography.footnote, { color: expiry.tone === 'neutral' ? theme.colors.textSecondary : theme.colors.status[expiry.tone].fg }]}>
                {expiry.text}
              </Text>
            ) : null}
            <View style={styles.pills}>
              <StatusPill tone={review.tone} label={review.label} />
            </View>
          </View>
          {FEATURES.DOCUMENT_DOWNLOAD ? (
            <Ionicons
              accessible={false}
              name={openingId === doc.id ? 'hourglass-outline' : 'share-outline'}
              size={20}
              color={theme.colors.textMuted}
            />
          ) : null}
        </View>
      </MotionPressable>
    );
  };

  return (
    <View style={[styles.root, { backgroundColor: theme.colors.canvas }]}>
      <LiquidBackdrop subtle />
      <ScrollView
        contentContainerStyle={styles.content}
        showsVerticalScrollIndicator={false}
        refreshControl={
          <RefreshControl
            refreshing={refreshing}
            onRefresh={() => {
              setRefreshing(true);
              void fetchDocuments();
            }}
            tintColor={theme.colors.accent}
          />
        }
      >
        <ScreenHero
          title={t('manager.docs.title')}
          subtitle={documents ? t('manager.docs.subtitle', { n: f.number(documents.length), verified: f.number(verified) }) : undefined}
          actions={
            FEATURES.FILE_UPLOAD ? (
              <GlassIconButton icon="cloud-upload-outline" label={t('manager.docs.upload')} accent onPress={() => setUploadOpen(true)} />
            ) : undefined
          }
        />

        {notice ? (
          <View style={styles.section}>
            <Text
              accessibilityRole="alert"
              accessibilityLiveRegion="polite"
              style={[theme.typography.subheadline, { color: notice.ok ? theme.colors.status.success.fg : theme.colors.status.danger.fg }]}
            >
              {notice.text}
            </Text>
          </View>
        ) : null}

        {documents === null && !loadFailed ? (
          <View style={styles.section}>
            <Surface contentStyle={styles.list}>
              <SkeletonRow />
              <SkeletonRow />
              <SkeletonRow />
            </Surface>
          </View>
        ) : loadFailed && !documents ? (
          <View style={styles.section}>
            <StateCard variant="error" title={t('manager.docs.errorTitle')} onRetry={() => void fetchDocuments()} />
          </View>
        ) : !documents?.length ? (
          <View style={styles.section}>
            <StateCard
              variant="empty"
              title={t('manager.docs.emptyTitle')}
              body={t('manager.docs.emptyBody')}
              action={FEATURES.FILE_UPLOAD ? { label: t('manager.docs.upload'), onPress: () => setUploadOpen(true) } : undefined}
            />
          </View>
        ) : (
          <>
            {attention.length ? (
              <View style={styles.section}>
                <Text accessibilityRole="header" style={[theme.typography.title3, { color: theme.colors.text }]}>{t('manager.docs.attention')}</Text>
                <Surface contentStyle={styles.list}>{attention.map((d, i) => renderRow(d, i === attention.length - 1))}</Surface>
              </View>
            ) : null}
            <View style={styles.section}>
              <Text accessibilityRole="header" style={[theme.typography.title3, { color: theme.colors.text }]}>{t('manager.docs.all')}</Text>
              <Surface contentStyle={styles.list}>{documents.map((d, i) => renderRow(d, i === documents.length - 1))}</Surface>
            </View>
          </>
        )}
      </ScrollView>

      <UploadSheet
        visible={uploadOpen}
        onClose={() => setUploadOpen(false)}
        onUploaded={() => {
          setUploadOpen(false);
          setNotice({ ok: true, text: t('manager.docs.uploaded') });
          void fetchDocuments();
        }}
      />
    </View>
  );
}

function UploadSheet({ visible, onClose, onUploaded }: { visible: boolean; onClose: () => void; onUploaded: () => void }) {
  const { theme } = useTheme();
  const { t } = useTranslation();
  const f = useFormat();
  const tx = t as unknown as (k: string, o?: Record<string, unknown>) => string;
  const [type, setType] = useState<string>(DOCUMENT_TYPES[0].value);
  const [number, setNumber] = useState('');
  const [expiry, setExpiry] = useState<string | null>(null);
  const [pickerOpen, setPickerOpen] = useState(false);
  const [file, setFile] = useState<PickedFile | null>(null);
  const [uploading, setUploading] = useState(false);
  const [error, setError] = useState<'file' | 'upload' | null>(null);

  const reset = () => {
    setType(DOCUMENT_TYPES[0].value);
    setNumber('');
    setExpiry(null);
    setPickerOpen(false);
    setFile(null);
    setError(null);
  };

  const pickFile = async () => {
    try {
      const result = await DocumentPicker.getDocumentAsync({ type: ['application/pdf', 'image/*'], copyToCacheDirectory: true });
      if (!result.canceled && result.assets?.[0]) {
        const a = result.assets[0];
        setFile(normalizePickedFile({ uri: a.uri, name: a.name, mimeType: a.mimeType, size: a.size }));
        setError(null);
      }
    } catch {
      setError('upload');
    }
  };

  const onDate = (event: DateTimePickerEvent, date?: Date) => {
    if (Platform.OS !== 'ios') setPickerOpen(false);
    if (event.type === 'set' && date) setExpiry(localIsoDate(date));
  };

  const submit = async () => {
    if (!file) {
      setError('file');
      haptics.error();
      return;
    }
    setUploading(true);
    setError(null);
    try {
      await documentsApi.uploadDocument({ file, documentType: type, expiryDate: expiry ?? undefined, documentNumber: number.trim() || undefined });
      haptics.success();
      reset();
      onUploaded();
    } catch {
      haptics.error();
      setError('upload');
    } finally {
      setUploading(false);
    }
  };

  return (
    <BottomSheet
      visible={visible}
      onClose={onClose}
      title={t('manager.docs.form.title')}
      subtitle={t('manager.docs.form.subtitle')}
      actions={
        <>
          {error === 'upload' ? (
            <Text accessibilityRole="alert" style={[theme.typography.footnote, { color: theme.colors.status.danger.fg }]}>
              {t('manager.docs.form.uploadError')}
            </Text>
          ) : null}
          <LiquidButton label={t('manager.docs.form.submit')} icon="cloud-upload-outline" loading={uploading} onPress={() => void submit()} />
        </>
      }
    >
      <Text style={[theme.typography.footnote, styles.semibold, { color: theme.colors.textSecondary }]}>{t('manager.docs.form.type')}</Text>
      <View style={styles.chips}>
        {DOCUMENT_TYPES.map((d) => {
          const on = d.value === type;
          return (
            <MotionPressable
              key={d.key}
              haptic="selection"
              accessibilityRole="button"
              accessibilityState={{ selected: on }}
              onPress={() => setType(d.value)}
              contentStyle={[
                styles.chip,
                { backgroundColor: on ? theme.colors.primaryFill : theme.colors.card, borderColor: on ? theme.colors.primaryFill : theme.colors.hairline },
              ]}
            >
              <Text style={[theme.typography.footnote, styles.semibold, { color: on ? theme.colors.onFill : theme.colors.textSecondary }]}>
                {tx(`manager.docs.types.${d.key}`)}
              </Text>
            </MotionPressable>
          );
        })}
      </View>

      <Text style={[theme.typography.footnote, styles.semibold, { color: theme.colors.textSecondary }]}>{t('manager.docs.form.number')}</Text>
      <TextInput
        value={number}
        onChangeText={setNumber}
        placeholder={t('manager.docs.form.numberPlaceholder')}
        accessibilityLabel={t('manager.docs.form.number')}
        placeholderTextColor={theme.colors.textMuted}
        selectionColor={theme.colors.accent}
        autoCapitalize="characters"
        autoCorrect={false}
        style={[theme.typography.callout, styles.input, { color: theme.colors.text, backgroundColor: theme.colors.surfaceInset, borderColor: theme.colors.hairline }]}
      />

      <Text style={[theme.typography.footnote, styles.semibold, { color: theme.colors.textSecondary }]}>{t('manager.docs.form.expiry')}</Text>
      <View style={styles.dateRow}>
        <LiquidButton
          label={expiry ? f.date(expiry, 'long') : t('manager.docs.form.chooseDate')}
          icon="calendar-outline"
          variant="secondary"
          size="compact"
          onPress={() => setPickerOpen((o) => !o)}
          style={styles.flex}
        />
        {expiry ? (
          <MotionPressable
            accessibilityRole="button"
            accessibilityLabel={t('manager.docs.form.clearDate')}
            onPress={() => setExpiry(null)}
            contentStyle={styles.iconButton}
          >
            <Ionicons accessible={false} name="close-circle" size={20} color={theme.colors.textMuted} />
          </MotionPressable>
        ) : null}
      </View>
      {pickerOpen ? (
        <DateTimePicker
          mode="date"
          display={Platform.OS === 'ios' ? 'inline' : 'default'}
          value={expiry ? new Date(`${expiry}T12:00:00`) : new Date()}
          onChange={onDate}
          accentColor={theme.colors.accent}
        />
      ) : null}

      <Text style={[theme.typography.footnote, styles.semibold, { color: theme.colors.textSecondary }]}>{t('manager.docs.form.file')}</Text>
      {file ? (
        <Surface level={0} contentStyle={styles.fileRow}>
          <Ionicons accessible={false} name="document-attach-outline" size={20} color={theme.colors.accent} />
          <Text numberOfLines={1} style={[theme.typography.callout, styles.flex, { color: theme.colors.text }]}>{file.name}</Text>
          <MotionPressable
            accessibilityRole="button"
            accessibilityLabel={t('manager.docs.form.remove', { name: file.name })}
            onPress={() => setFile(null)}
            contentStyle={styles.iconButton}
          >
            <Ionicons accessible={false} name="close" size={18} color={theme.colors.status.danger.fg} />
          </MotionPressable>
        </Surface>
      ) : (
        <LiquidButton
          label={t('manager.docs.form.chooseFile')}
          icon="folder-open-outline"
          variant="secondary"
          size="compact"
          accessibilityHint={t('manager.docs.form.fileHint')}
          onPress={() => void pickFile()}
        />
      )}
      {error === 'file' ? (
        <Text accessibilityLiveRegion="polite" style={[theme.typography.footnote, { color: theme.colors.status.danger.fg }]}>
          {t('manager.docs.form.fileRequired')}
        </Text>
      ) : null}
    </BottomSheet>
  );
}

const styles = StyleSheet.create({
  root: { flex: 1 },
  flex: { flex: 1 },
  content: { paddingBottom: 24 },
  section: { paddingHorizontal: 16, marginBottom: 18, gap: 10 },
  semibold: { fontWeight: '600' },
  list: { paddingHorizontal: 14 },
  row: { flexDirection: 'row', alignItems: 'flex-start', gap: 12, paddingVertical: 12, minHeight: 64 },
  pills: { flexDirection: 'row', flexWrap: 'wrap', gap: 6, marginTop: 4 },
  chips: { flexDirection: 'row', flexWrap: 'wrap', gap: 8 },
  chip: { minHeight: 44, borderRadius: 22, borderWidth: StyleSheet.hairlineWidth, paddingHorizontal: 14, justifyContent: 'center' },
  input: { minHeight: 48, borderRadius: 14, borderWidth: 1, paddingHorizontal: 12, paddingVertical: 10, textAlign: 'auto' },
  dateRow: { flexDirection: 'row', alignItems: 'center', gap: 6 },
  iconButton: { width: 44, height: 44, alignItems: 'center', justifyContent: 'center' },
  fileRow: { flexDirection: 'row', alignItems: 'center', gap: 10, paddingStart: 12, paddingEnd: 4, minHeight: 52 },
});
