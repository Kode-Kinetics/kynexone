import React, { useCallback, useMemo, useRef, useState } from 'react';
import { RefreshControl, ScrollView, StyleSheet, Text, TextInput, View } from 'react-native';
import { useFocusEffect, useNavigation } from '@react-navigation/native';
import { Ionicons } from '@expo/vector-icons';
import { useTranslation } from 'react-i18next';
import * as DocumentPicker from 'expo-document-picker';
import { documentsApi, hrRequestsApi } from '@/api/adapters';
import { normalizePickedFile, type PickedFile } from '@/api/services';
import { useAuthStore } from '@/auth/authStore';
import { navigateTo, type AppRoute } from '@/navigation/routes';
import { FEATURES } from '@/config/features';
import { useTheme } from '@/theme/ThemeProvider';
import { useFormat } from '@/utils/format';
import { haptics } from '@/utils/haptics';
import {
  BottomSheet,
  DirectionalIcon,
  LiquidBackdrop,
  LiquidButton,
  MotionPressable,
  ScreenHero,
  SkeletonRow,
  StateCard,
  StatusPill,
  Surface,
} from '@/components/ui';
import type { HRRequest } from '@/types';

/**
 * Ticket categories raised from mobile. `category` is the value stored on the ticket (HR reads it on
 * the web, so it stays the backend's English category name); the screen shows `t('manager.hr.types.<key>')`.
 * Salary certificates, experience and bank letters are NOT tickets: they are issued from Letters.
 */
const TICKET_TYPES = [
  { key: 'NOC', category: 'No objection certificate', icon: 'shield-checkmark-outline' },
  { key: 'General', category: 'General HR ticket', icon: 'chatbubble-ellipses-outline' },
  { key: 'Complaint', category: 'Complaint or grievance', icon: 'warning-outline' },
  { key: 'LeaveEncashment', category: 'Leave encashment', icon: 'cash-outline' },
  { key: 'DocumentRequest', category: 'Document request', icon: 'folder-open-outline' },
] as const;

/** Categories older tickets may carry (letters raised as tickets before Letters existed). */
const LEGACY_CATEGORIES: Record<string, string> = {
  'salary certificate': 'SalaryCertificate',
  'experience letter': 'ExperienceLetter',
  'bank letter': 'BankLetter',
  salarycertificate: 'SalaryCertificate',
  experienceletter: 'ExperienceLetter',
  bankletter: 'BankLetter',
};

type TicketKey = (typeof TICKET_TYPES)[number]['key'];

function typeKeyOf(category: string): string | null {
  const v = category.trim().toLowerCase();
  const hit = TICKET_TYPES.find((x) => x.key.toLowerCase() === v || x.category.toLowerCase() === v);
  return hit?.key ?? LEGACY_CATEGORIES[v] ?? null;
}

export function useRequestTypeLabel() {
  const { t } = useTranslation();
  const tx = t as unknown as (k: string) => string;
  return (category: string) => {
    const key = typeKeyOf(category);
    return key ? tx(`manager.hr.types.${key}`) : category;
  };
}

export function useRequestStatus() {
  const { t } = useTranslation();
  const tx = t as unknown as (k: string) => string;
  return (status: HRRequest['status']) => {
    const tone = status === 'Resolved' ? 'success' : status === 'InProgress' ? 'info' : status === 'Open' ? 'warning' : 'neutral';
    return { tone: tone as 'success' | 'info' | 'warning' | 'neutral', label: tx(`manager.hr.status.${status}`) };
  };
}

export default function HRRequestsScreen() {
  const navigation = useNavigation<any>();
  const user = useAuthStore((s) => s.user);
  const { theme } = useTheme();
  const { t } = useTranslation();
  const f = useFormat();
  const typeLabel = useRequestTypeLabel();
  const statusOf = useRequestStatus();
  const [requests, setRequests] = useState<HRRequest[] | null>(null);
  const [loadFailed, setLoadFailed] = useState(false);
  const [refreshing, setRefreshing] = useState(false);
  const [composer, setComposer] = useState<TicketKey | null>(null);
  const [notice, setNotice] = useState<string | null>(null);

  const fetchRequests = useCallback(async () => {
    try {
      const data = await hrRequestsApi.getMy({ page: 1, limit: 50 });
      setRequests(data.items || []);
      setLoadFailed(false);
    } catch {
      setLoadFailed(true);
    } finally {
      setRefreshing(false);
    }
  }, []);

  useFocusEffect(
    useCallback(() => {
      void fetchRequests();
    }, [fetchRequests]),
  );

  const stats = useMemo(() => ({
    active: (requests ?? []).filter((r) => r.status === 'Open' || r.status === 'InProgress').length,
    resolved: (requests ?? []).filter((r) => r.status === 'Resolved' || r.status === 'Closed').length,
  }), [requests]);

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
              void fetchRequests();
            }}
            tintColor={theme.colors.accent}
          />
        }
      >
        <ScreenHero
          title={t('manager.hr.title')}
          subtitle={requests ? t('manager.hr.subtitle', { active: f.number(stats.active), resolved: f.number(stats.resolved) }) : undefined}
        />

        {notice ? (
          <View style={styles.section}>
            <Surface tintColor={theme.colors.status.success.tint} contentStyle={styles.banner}>
              <Ionicons accessible={false} name="checkmark-circle" size={22} color={theme.colors.status.success.fg} />
              <Text accessibilityLiveRegion="polite" accessibilityRole="alert" style={[theme.typography.subheadline, styles.flex, { color: theme.colors.text }]}>
                {notice}
              </Text>
              <MotionPressable accessibilityRole="button" accessibilityLabel={t('manager.approvals.outcome.dismiss')} onPress={() => setNotice(null)} contentStyle={styles.iconButton}>
                <Ionicons accessible={false} name="close" size={18} color={theme.colors.textMuted} />
              </MotionPressable>
            </Surface>
          </View>
        ) : null}

        <View style={styles.section}>
          <MotionPressable
            accessibilityRole="button"
            accessibilityLabel={`${t('manager.hr.lettersTitle')}. ${t('manager.hr.lettersBody')}`}
            onPress={() => navigateTo(navigation, 'Letters' as AppRoute, user)}
          >
            <Surface level={2} contentStyle={styles.letters}>
              <View style={[styles.iconBadge, { backgroundColor: theme.colors.surfaceInset }]}>
                <Ionicons accessible={false} name="document-text-outline" size={22} color={theme.colors.accent} />
              </View>
              <View style={styles.flex}>
                <Text style={[theme.typography.headline, { color: theme.colors.text }]}>{t('manager.hr.lettersTitle')}</Text>
                <Text style={[theme.typography.footnote, { color: theme.colors.textSecondary }]}>{t('manager.hr.lettersBody')}</Text>
                <Text style={[theme.typography.subheadline, styles.semibold, { color: theme.colors.accent }]}>{t('manager.hr.lettersCta')}</Text>
              </View>
              <DirectionalIcon name="chevron" size={18} color={theme.colors.textMuted} />
            </Surface>
          </MotionPressable>
        </View>

        <View style={styles.section}>
          <Text accessibilityRole="header" style={[theme.typography.title3, { color: theme.colors.text }]}>{t('manager.hr.askTitle')}</Text>
          <View style={styles.typeGrid}>
            {TICKET_TYPES.map((type) => (
              <MotionPressable
                key={type.key}
                accessibilityRole="button"
                onPress={() => setComposer(type.key)}
                style={styles.typeCell}
              >
                <Surface contentStyle={styles.typeCard}>
                  <Ionicons accessible={false} name={type.icon} size={20} color={theme.colors.accent} />
                  <Text style={[theme.typography.subheadline, styles.semibold, { color: theme.colors.text }]}>
                    {typeLabel(type.key)}
                  </Text>
                </Surface>
              </MotionPressable>
            ))}
          </View>
        </View>

        <View style={styles.section}>
          <Text accessibilityRole="header" style={[theme.typography.title3, { color: theme.colors.text }]}>{t('manager.hr.listTitle')}</Text>
          {requests === null && !loadFailed ? (
            <Surface contentStyle={styles.list}>
              <SkeletonRow />
              <SkeletonRow />
            </Surface>
          ) : loadFailed && !requests ? (
            <StateCard variant="error" title={t('manager.hr.errorTitle')} onRetry={() => void fetchRequests()} />
          ) : !requests?.length ? (
            <StateCard
              variant="empty"
              title={t('manager.hr.emptyTitle')}
              body={t('manager.hr.emptyBody')}
              action={{ label: t('manager.hr.newRequest'), onPress: () => setComposer('General') }}
            />
          ) : (
            <Surface contentStyle={styles.list}>
              {requests.map((r, i) => {
                const status = statusOf(r.status);
                return (
                  <MotionPressable
                    key={r.id}
                    accessibilityRole="button"
                    accessibilityLabel={`${r.subject}. ${typeLabel(r.requestType)}. ${status.label}`}
                    onPress={() => navigation.navigate('HRRequestDetail', { id: r.id })}
                  >
                    <View style={[styles.row, i < requests.length - 1 && { borderBottomColor: theme.colors.hairline, borderBottomWidth: StyleSheet.hairlineWidth }]}>
                      <View style={styles.flex}>
                        <Text numberOfLines={2} style={[theme.typography.headline, { color: theme.colors.text }]}>{r.subject}</Text>
                        <Text numberOfLines={1} style={[theme.typography.footnote, { color: theme.colors.textSecondary }]}>
                          {[typeLabel(r.requestType), r.createdAt ? t('manager.hr.created', { date: f.date(r.createdAt, 'medium') }) : null].filter(Boolean).join(' · ')}
                        </Text>
                        <View style={styles.pills}>
                          <StatusPill tone={status.tone} label={status.label} />
                          {r.slaBreached && (r.status === 'Open' || r.status === 'InProgress') ? (
                            <StatusPill tone="danger" label={t('manager.hr.breached')} />
                          ) : null}
                          {(r.commentsCount ?? 0) > 0 ? (
                            <Text style={[theme.typography.footnote, { color: theme.colors.textMuted }]}>
                              {t('manager.hr.replies', { n: f.number(r.commentsCount ?? 0) })}
                            </Text>
                          ) : null}
                        </View>
                      </View>
                      <DirectionalIcon name="chevron" size={16} color={theme.colors.textMuted} />
                    </View>
                  </MotionPressable>
                );
              })}
            </Surface>
          )}
        </View>
      </ScrollView>

      <ComposeSheet
        typeKey={composer}
        onChangeType={setComposer}
        onClose={() => setComposer(null)}
        onSent={() => {
          setComposer(null);
          setNotice(t('manager.hr.form.sent'));
          void fetchRequests();
        }}
      />
    </View>
  );
}

function ComposeSheet({
  typeKey,
  onChangeType,
  onClose,
  onSent,
}: {
  typeKey: TicketKey | null;
  onChangeType: (k: TicketKey) => void;
  onClose: () => void;
  onSent: () => void;
}) {
  const { theme } = useTheme();
  const { t } = useTranslation();
  const typeLabel = useRequestTypeLabel();
  const [subject, setSubject] = useState('');
  const [description, setDescription] = useState('');
  const [attachment, setAttachment] = useState<PickedFile | null>(null);
  const [errors, setErrors] = useState<{ subject?: boolean; description?: boolean; file?: boolean; submit?: boolean }>({});
  const [sending, setSending] = useState(false);
  const descriptionRef = useRef<TextInput>(null);

  const reset = () => {
    setSubject('');
    setDescription('');
    setAttachment(null);
    setErrors({});
  };

  const pickFile = async () => {
    try {
      const result = await DocumentPicker.getDocumentAsync({ copyToCacheDirectory: true });
      if (!result.canceled && result.assets?.[0]) {
        const a = result.assets[0];
        setAttachment(normalizePickedFile({ uri: a.uri, name: a.name, mimeType: a.mimeType, size: a.size }));
        setErrors((e) => ({ ...e, file: false }));
      }
    } catch {
      setErrors((e) => ({ ...e, file: true }));
    }
  };

  const submit = async () => {
    const type = TICKET_TYPES.find((x) => x.key === typeKey);
    if (!type) return;
    const missing = { subject: !subject.trim(), description: !description.trim() };
    if (missing.subject || missing.description) {
      setErrors(missing);
      haptics.error();
      return;
    }
    setSending(true);
    setErrors({});
    try {
      const uploaded = attachment
        ? await documentsApi.uploadDocument({ file: attachment, documentType: 'HR Request Attachment' })
        : null;
      await hrRequestsApi.create({
        requestType: type.category,
        subject: subject.trim(),
        description: description.trim(),
        attachmentDocumentId: uploaded?.id,
      });
      haptics.success();
      reset();
      onSent();
    } catch {
      haptics.error();
      setErrors({ submit: true });
    } finally {
      setSending(false);
    }
  };

  const inputStyle = (invalid?: boolean) => [
    theme.typography.callout,
    styles.input,
    { color: theme.colors.text, backgroundColor: theme.colors.surfaceInset, borderColor: invalid ? theme.colors.status.danger.fg : theme.colors.hairline },
  ];

  return (
    <BottomSheet
      visible={typeKey !== null}
      onClose={onClose}
      title={t('manager.hr.form.title')}
      subtitle={t('manager.hr.form.subtitle')}
      actions={
        <>
          {errors.submit ? (
            <Text accessibilityRole="alert" accessibilityLiveRegion="assertive" style={[theme.typography.footnote, { color: theme.colors.status.danger.fg }]}>
              {t('manager.hr.form.submitError')}
            </Text>
          ) : null}
          <LiquidButton label={t('manager.hr.form.submit')} icon="paper-plane-outline" loading={sending} onPress={() => void submit()} />
        </>
      }
    >
      <Text style={[theme.typography.footnote, styles.semibold, { color: theme.colors.textSecondary }]}>{t('manager.hr.form.type')}</Text>
      <View style={styles.typeChips}>
        {TICKET_TYPES.map((type) => {
          const on = type.key === typeKey;
          return (
            <MotionPressable
              key={type.key}
              haptic="selection"
              accessibilityRole="button"
              accessibilityState={{ selected: on }}
              onPress={() => onChangeType(type.key)}
              contentStyle={[
                styles.chip,
                { backgroundColor: on ? theme.colors.primaryFill : theme.colors.card, borderColor: on ? theme.colors.primaryFill : theme.colors.hairline },
              ]}
            >
              <Text style={[theme.typography.footnote, styles.semibold, { color: on ? theme.colors.onFill : theme.colors.textSecondary }]}>
                {typeLabel(type.key)}
              </Text>
            </MotionPressable>
          );
        })}
      </View>

      <Text style={[theme.typography.footnote, styles.semibold, { color: theme.colors.textSecondary }]}>{t('manager.hr.form.subject')}</Text>
      <TextInput
        value={subject}
        onChangeText={(v) => {
          setSubject(v);
          if (v.trim()) setErrors((e) => ({ ...e, subject: false }));
        }}
        placeholder={t('manager.hr.form.subjectPlaceholder')}
        accessibilityLabel={t('manager.hr.form.subject')}
        placeholderTextColor={theme.colors.textMuted}
        selectionColor={theme.colors.accent}
        returnKeyType="next"
        onSubmitEditing={() => descriptionRef.current?.focus()}
        maxLength={200}
        style={inputStyle(errors.subject)}
      />
      {errors.subject ? <FieldError text={t('manager.hr.form.subjectRequired')} /> : null}

      <Text style={[theme.typography.footnote, styles.semibold, { color: theme.colors.textSecondary }]}>{t('manager.hr.form.description')}</Text>
      <TextInput
        ref={descriptionRef}
        value={description}
        onChangeText={(v) => {
          setDescription(v);
          if (v.trim()) setErrors((e) => ({ ...e, description: false }));
        }}
        placeholder={t('manager.hr.form.descriptionPlaceholder')}
        accessibilityLabel={t('manager.hr.form.description')}
        placeholderTextColor={theme.colors.textMuted}
        selectionColor={theme.colors.accent}
        multiline
        textAlignVertical="top"
        maxLength={4000}
        style={[inputStyle(errors.description), styles.textarea]}
      />
      {errors.description ? <FieldError text={t('manager.hr.form.descriptionRequired')} /> : null}

      {FEATURES.FILE_UPLOAD ? (
        <>
          <Text style={[theme.typography.footnote, styles.semibold, { color: theme.colors.textSecondary }]}>{t('manager.hr.form.attachment')}</Text>
          {attachment ? (
            <Surface level={0} contentStyle={styles.fileRow}>
              <Ionicons accessible={false} name="document-attach-outline" size={20} color={theme.colors.accent} />
              <Text numberOfLines={1} style={[theme.typography.callout, styles.flex, { color: theme.colors.text }]}>{attachment.name}</Text>
              <MotionPressable
                accessibilityRole="button"
                accessibilityLabel={t('manager.hr.form.remove', { name: attachment.name })}
                onPress={() => setAttachment(null)}
                contentStyle={styles.iconButton}
              >
                <Ionicons accessible={false} name="close" size={18} color={theme.colors.status.danger.fg} />
              </MotionPressable>
            </Surface>
          ) : (
            <LiquidButton label={t('manager.hr.form.attach')} icon="attach-outline" variant="secondary" size="compact" onPress={() => void pickFile()} />
          )}
          {errors.file ? <FieldError text={t('manager.hr.form.fileError')} /> : null}
        </>
      ) : null}
    </BottomSheet>
  );
}

function FieldError({ text }: { text: string }) {
  const { theme } = useTheme();
  return (
    <Text accessibilityLiveRegion="polite" style={[theme.typography.footnote, { color: theme.colors.status.danger.fg }]}>
      {text}
    </Text>
  );
}

const styles = StyleSheet.create({
  root: { flex: 1 },
  flex: { flex: 1 },
  content: { paddingBottom: 24 },
  section: { paddingHorizontal: 16, marginBottom: 18, gap: 10 },
  semibold: { fontWeight: '600' },
  banner: { flexDirection: 'row', alignItems: 'center', gap: 10, paddingVertical: 8, paddingStart: 14, paddingEnd: 4 },
  iconButton: { width: 44, height: 44, alignItems: 'center', justifyContent: 'center' },
  letters: { flexDirection: 'row', alignItems: 'center', gap: 12, padding: 16 },
  iconBadge: { width: 44, height: 44, borderRadius: 12, alignItems: 'center', justifyContent: 'center' },
  typeGrid: { flexDirection: 'row', flexWrap: 'wrap', gap: 10 },
  typeCell: { flexBasis: '47%', flexGrow: 1 },
  typeCard: { padding: 14, gap: 8, minHeight: 88 },
  list: { paddingHorizontal: 14 },
  row: { flexDirection: 'row', alignItems: 'center', gap: 10, paddingVertical: 12, minHeight: 64 },
  pills: { flexDirection: 'row', flexWrap: 'wrap', alignItems: 'center', gap: 6, marginTop: 6 },
  typeChips: { flexDirection: 'row', flexWrap: 'wrap', gap: 8 },
  chip: { minHeight: 44, borderRadius: 22, borderWidth: StyleSheet.hairlineWidth, paddingHorizontal: 14, justifyContent: 'center' },
  input: { minHeight: 48, borderRadius: 14, borderWidth: 1, paddingHorizontal: 12, paddingVertical: 10, textAlign: 'auto' },
  textarea: { minHeight: 120 },
  fileRow: { flexDirection: 'row', alignItems: 'center', gap: 10, paddingStart: 12, paddingEnd: 4, minHeight: 52 },
});
