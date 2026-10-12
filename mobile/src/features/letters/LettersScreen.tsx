import React, { useCallback, useMemo, useState } from 'react';
import { RefreshControl, ScrollView, StyleSheet, Text, View } from 'react-native';
import { useFocusEffect } from '@react-navigation/native';
import { Ionicons } from '@expo/vector-icons';
import * as Sharing from 'expo-sharing';
import { useTranslation } from 'react-i18next';
import { lettersApi } from '@/api/letters';
import {
  BottomSheet,
  GlassTextField,
  LiquidBackdrop,
  LiquidButton,
  MotionPressable,
  ScreenHero,
  SegmentedControl,
  SkeletonCard,
  StateCard,
  StatusPill,
  StickyFooter,
  Surface,
} from '@/components/ui';
import { useTheme } from '@/theme/ThemeProvider';
import { useFormat } from '@/utils/format';
import { haptics } from '@/utils/haptics';
import {
  ADDRESSEE_MAX,
  LETTER_LANGUAGES,
  PURPOSE_MAX,
  letterServerError,
  letterStatus,
  letterTypeName,
  openLetterTypes,
  validateLetterDraft,
  type LetterDraft,
  type LetterLanguage,
  type LetterRequest,
  type LetterServerError,
  type LetterType,
} from './letterRequest';

type LoadState =
  | { kind: 'loading' }
  | { kind: 'ready'; types: LetterType[]; requests: LetterRequest[] }
  | { kind: 'notLinked'; message: string | null }
  | { kind: 'error' };

const EMPTY_DRAFT: LetterDraft = { letterType: null, language: 'bilingual', purpose: '', addresseeName: '' };

function failure(error: unknown): { status?: number; code?: string; message?: string; messageAr?: string } {
  const e = error as { status?: number; response?: { status?: number; data?: any } };
  const data = e?.response?.data;
  return {
    status: e?.response?.status ?? e?.status,
    code: typeof data?.code === 'string' ? data.code : undefined,
    message: typeof data?.message === 'string' ? data.message : undefined,
    messageAr: typeof data?.messageAr === 'string' ? data.messageAr : undefined,
  };
}

export default function LettersScreen() {
  const { t, i18n } = useTranslation();
  const { theme } = useTheme();
  const f = useFormat();
  const language: 'en' | 'ar' = i18n.language?.startsWith('ar') ? 'ar' : 'en';
  const [state, setState] = useState<LoadState>({ kind: 'loading' });
  const [refreshing, setRefreshing] = useState(false);
  const [sheetOpen, setSheetOpen] = useState(false);
  const [draft, setDraft] = useState<LetterDraft>(EMPTY_DRAFT);
  const [submitting, setSubmitting] = useState(false);
  const [sheetError, setSheetError] = useState<LetterServerError | null>(null);
  const [showValidation, setShowValidation] = useState(false);
  const [sentType, setSentType] = useState<string | null>(null);
  const [downloadingId, setDownloadingId] = useState<string | null>(null);
  const [rowError, setRowError] = useState<{ id: string; key: LetterServerError } | null>(null);

  const load = useCallback(async (mode: 'initial' | 'refresh' = 'initial') => {
    if (mode === 'refresh') setRefreshing(true);
    try {
      const [types, requests] = await Promise.all([lettersApi.getTypes(), lettersApi.getRequests()]);
      setState({ kind: 'ready', types, requests });
    } catch (error) {
      const fail = failure(error);
      if (fail.status === 400) {
        setState({ kind: 'notLinked', message: (language === 'ar' ? fail.messageAr : fail.message) ?? null });
      } else {
        setState((current) => (current.kind === 'ready' ? current : { kind: 'error' }));
      }
    } finally {
      setRefreshing(false);
    }
  }, [language]);

  useFocusEffect(useCallback(() => {
    void load();
  }, [load]));

  const types = useMemo(() => (state.kind === 'ready' ? state.types : []), [state]);
  const requests = useMemo(() => (state.kind === 'ready' ? state.requests : []), [state]);
  const open = useMemo(() => openLetterTypes(requests), [requests]);
  const validation = validateLetterDraft(draft, types, open);

  const nameOf = useCallback((letterType: string) => {
    const configured = letterTypeName(letterType, types, language);
    if (configured) return configured;
    const known = ['SalaryCertificate', 'SalaryTransferLetter', 'EmploymentVerification', 'ExperienceCertificate', 'AppointmentLetter'];
    return known.includes(letterType)
      ? t(`account.letters.types.${letterType as 'SalaryCertificate'}`)
      : t('account.letters.types.other');
  }, [language, t, types]);

  const openSheet = () => {
    const firstAvailable = types.find((type) => !open.has(type.letterType));
    setDraft({ ...EMPTY_DRAFT, letterType: types.length === 1 && firstAvailable ? firstAvailable.letterType : null });
    setSheetError(null);
    setShowValidation(false);
    setSheetOpen(true);
  };

  const submit = async () => {
    setShowValidation(true);
    if (!validation.ok || submitting) {
      haptics.error();
      return;
    }
    setSubmitting(true);
    setSheetError(null);
    haptics.commit();
    try {
      const created = await lettersApi.create(draft);
      haptics.success();
      setSheetOpen(false);
      setSentType(created.letterType);
      setState((current) => (current.kind === 'ready'
        ? { ...current, requests: [created, ...current.requests.filter((r) => r.id !== created.id)] }
        : current));
      void load('refresh');
    } catch (error) {
      const fail = failure(error);
      haptics.error();
      setSheetError(letterServerError(fail.status, fail.code));
      if (fail.code === 'duplicate_pending_request') void load('refresh');
    } finally {
      setSubmitting(false);
    }
  };

  const download = async (request: LetterRequest, retried = false): Promise<void> => {
    if (downloadingId && !retried) return;
    setDownloadingId(request.id);
    setRowError(null);
    let uri: string | null = null;
    try {
      uri = await lettersApi.downloadPdf(request);
      if (await Sharing.isAvailableAsync()) {
        await Sharing.shareAsync(uri, {
          mimeType: 'application/pdf',
          UTI: 'com.adobe.pdf',
          dialogTitle: nameOf(request.letterType),
        });
      } else {
        setRowError({ id: request.id, key: 'generic' });
      }
    } catch (error) {
      const fail = failure(error);
      if (fail.status === 401 && !retried) {
        // The download bypasses the API client; one ordinary request refreshes an expired token.
        await lettersApi.getRequests().catch(() => undefined);
        await lettersApi.deleteLocalCopy(uri);
        uri = null;
        return await download(request, true);
      }
      haptics.error();
      setRowError({ id: request.id, key: fail.status === 409 ? 'reprintUnavailable' : letterServerError(fail.status, fail.code) });
    } finally {
      // The letter is shared, never kept on the device.
      await lettersApi.deleteLocalCopy(uri);
      setDownloadingId(null);
    }
  };

  const canRequest = state.kind === 'ready' && types.some((type) => !open.has(type.letterType));

  const languageSegments = LETTER_LANGUAGES.map((key) => ({ key, label: t(`account.letters.language.${key}`) }));

  const renderBody = () => {
    if (state.kind === 'loading') {
      return (
        <View style={styles.list}>
          <SkeletonCard lines={2} />
          <SkeletonCard lines={2} />
        </View>
      );
    }
    if (state.kind === 'error') {
      return <StateCard variant="error" style={styles.state} onRetry={() => void load()} />;
    }
    if (state.kind === 'notLinked') {
      return (
        <StateCard
          variant="permission"
          style={styles.state}
          title={t('account.letters.notLinkedTitle')}
          body={state.message ?? t('account.letters.notLinkedBody')}
        />
      );
    }
    if (types.length === 0 && requests.length === 0) {
      return (
        <StateCard
          variant="empty"
          style={styles.state}
          title={t('account.letters.noneConfiguredTitle')}
          body={t('account.letters.noneConfiguredBody')}
        />
      );
    }
    return (
      <View style={styles.list}>
        {sentType ? (
          <Surface tintColor={theme.colors.status.success.tint} contentStyle={styles.notice}>
            <Ionicons accessible={false} name="checkmark-circle" size={22} color={theme.colors.status.success.fg} />
            <Text
              accessibilityLiveRegion="polite"
              style={[theme.typography.subheadline, styles.flex, { color: theme.colors.text }]}
            >
              {t('account.letters.sent', { letter: nameOf(sentType) })}
            </Text>
          </Surface>
        ) : null}

        <Text accessibilityRole="header" style={[theme.typography.headline, styles.sectionTitle, { color: theme.colors.text }]}>
          {t('account.letters.yourRequests')}
        </Text>

        {requests.length === 0 ? (
          <StateCard
            variant="empty"
            title={t('account.letters.emptyTitle')}
            body={t('account.letters.emptyBody')}
            action={canRequest ? { label: t('account.letters.request'), onPress: openSheet } : undefined}
          />
        ) : (
          requests.map((request) => {
            const status = letterStatus(request);
            const issued = status.key === 'issued';
            const languageLabel = (LETTER_LANGUAGES as readonly string[]).includes(request.language)
              ? t(`account.letters.language.${request.language as LetterLanguage}`)
              : null;
            const meta = [t('account.letters.requestedOn', { date: f.date(request.createdAtUtc, 'medium') }), languageLabel]
              .filter(Boolean)
              .join(' · ');
            return (
              <Surface key={request.id} contentStyle={styles.row} testID={`letter-${request.id}`}>
                <View style={styles.rowTop}>
                  <View style={styles.flex}>
                    <Text style={[theme.typography.headline, { color: theme.colors.text }]}>{nameOf(request.letterType)}</Text>
                    <Text style={[theme.typography.footnote, styles.meta, { color: theme.colors.textSecondary }]}>{meta}</Text>
                  </View>
                  <StatusPill tone={status.tone} label={t(`account.letters.status.${status.key}`)} />
                </View>

                {request.addresseeName ? (
                  <Text style={[theme.typography.footnote, { color: theme.colors.textSecondary }]}>
                    {t('account.letters.addressedTo', { name: request.addresseeName })}
                  </Text>
                ) : null}

                {status.key === 'pending' ? (
                  <Text style={[theme.typography.footnote, { color: theme.colors.textMuted }]}>
                    {t('account.letters.pendingHint')}
                  </Text>
                ) : null}

                {status.key === 'declined' ? (
                  <Text style={[theme.typography.footnote, { color: theme.colors.textSecondary }]}>
                    {request.decisionNote
                      ? t('account.letters.hrNote', { note: request.decisionNote })
                      : t('account.letters.declinedNoNote')}
                  </Text>
                ) : null}

                {issued ? (
                  <View style={styles.issued}>
                    <Text selectable style={[theme.typography.footnote, { color: theme.colors.textSecondary }]}>
                      {[
                        request.referenceNumber ? t('account.letters.reference', { ref: request.referenceNumber }) : null,
                        request.decidedAtUtc ? t('account.letters.issuedOn', { date: f.date(request.decidedAtUtc, 'medium') }) : null,
                      ].filter(Boolean).join(' · ')}
                    </Text>
                    <LiquidButton
                      label={t('account.letters.download')}
                      icon="download-outline"
                      variant="secondary"
                      size="compact"
                      loading={downloadingId === request.id}
                      disabled={downloadingId !== null && downloadingId !== request.id}
                      onPress={() => void download(request)}
                      accessibilityHint={t('account.letters.downloadHint')}
                    />
                  </View>
                ) : null}

                {rowError?.id === request.id ? (
                  <Text accessibilityRole="alert" style={[theme.typography.footnote, { color: theme.colors.status.danger.fg }]}>
                    {t(`account.letters.errors.${rowError.key}`)}
                  </Text>
                ) : null}
              </Surface>
            );
          })
        )}
      </View>
    );
  };

  return (
    <View style={[styles.root, { backgroundColor: theme.colors.canvas }]}>
      <LiquidBackdrop subtle />
      <ScrollView
        contentContainerStyle={styles.content}
        refreshControl={
          <RefreshControl
            refreshing={refreshing}
            onRefresh={() => void load('refresh')}
            tintColor={theme.colors.accent}
            colors={[theme.colors.accent]}
          />
        }
        showsVerticalScrollIndicator={false}
      >
        <ScreenHero
          eyebrow={t('account.letters.eyebrow')}
          title={t('account.letters.title')}
          subtitle={t('account.letters.subtitle')}
        />
        {renderBody()}
      </ScrollView>

      {state.kind === 'ready' && types.length > 0 ? (
        <StickyFooter>
          <LiquidButton
            label={t('account.letters.request')}
            icon="document-text-outline"
            onPress={openSheet}
            disabled={!canRequest}
            accessibilityHint={canRequest ? undefined : t('account.letters.allOpenHint')}
            testID="letters-request"
          />
          {!canRequest ? (
            <Text style={[theme.typography.footnote, styles.center, { color: theme.colors.textSecondary }]}>
              {t('account.letters.allOpenHint')}
            </Text>
          ) : null}
        </StickyFooter>
      ) : null}

      <BottomSheet
        visible={sheetOpen}
        onClose={() => setSheetOpen(false)}
        title={t('account.letters.sheetTitle')}
        subtitle={t('account.letters.sheetSubtitle')}
        actions={
          <LiquidButton
            label={t('account.letters.send')}
            icon="paper-plane-outline"
            onPress={() => void submit()}
            loading={submitting}
            disabled={submitting}
            testID="letters-send"
          />
        }
      >
        <Text style={[theme.typography.subheadline, styles.label, { color: theme.colors.text }]}>
          {t('account.letters.whichLetter')}
        </Text>
        <View accessibilityRole="radiogroup" style={styles.options}>
          {types.map((type) => {
            const selected = draft.letterType === type.letterType;
            const taken = open.has(type.letterType);
            return (
              <MotionPressable
                key={type.letterType}
                accessibilityRole="radio"
                accessibilityState={{ selected, disabled: taken }}
                accessibilityHint={taken ? t('account.letters.alreadyOpen') : undefined}
                disabled={taken}
                haptic="selection"
                onPress={() => setDraft((d) => ({ ...d, letterType: type.letterType }))}
                contentStyle={[
                  styles.option,
                  {
                    borderColor: selected ? theme.colors.accent : theme.colors.hairline,
                    backgroundColor: selected ? theme.colors.surfaceInset : theme.colors.card,
                  },
                ]}
              >
                <Ionicons
                  accessible={false}
                  name={selected ? 'radio-button-on' : 'radio-button-off'}
                  size={22}
                  color={selected ? theme.colors.accent : theme.colors.textMuted}
                />
                <View style={styles.flex}>
                  <Text style={[theme.typography.callout, { color: theme.colors.text }]}>
                    {letterTypeName(type.letterType, types, language)}
                  </Text>
                  {taken ? (
                    <Text style={[theme.typography.footnote, { color: theme.colors.textMuted }]}>
                      {t('account.letters.alreadyOpen')}
                    </Text>
                  ) : null}
                </View>
              </MotionPressable>
            );
          })}
        </View>
        {showValidation && validation.errors.letterType ? (
          <Text accessibilityRole="alert" style={[theme.typography.footnote, { color: theme.colors.status.danger.fg }]}>
            {t(`account.letters.validation.${validation.errors.letterType}`)}
          </Text>
        ) : null}

        <Text style={[theme.typography.subheadline, styles.label, { color: theme.colors.text }]}>
          {t('account.letters.languageLabel')}
        </Text>
        <SegmentedControl
          segments={languageSegments}
          value={draft.language}
          onChange={(language) => setDraft((d) => ({ ...d, language }))}
        />

        <GlassTextField
          label={t('account.letters.addresseeLabel')}
          icon="business-outline"
          value={draft.addresseeName}
          onChangeText={(addresseeName) => setDraft((d) => ({ ...d, addresseeName }))}
          placeholder={t('account.letters.addresseePlaceholder')}
          maxLength={ADDRESSEE_MAX}
          autoCorrect={false}
          containerStyle={styles.field}
          error={showValidation && validation.errors.addresseeName ? t('account.letters.validation.tooLong', { max: ADDRESSEE_MAX }) : undefined}
        />
        <GlassTextField
          label={t('account.letters.purposeLabel')}
          icon="create-outline"
          value={draft.purpose}
          onChangeText={(purpose) => setDraft((d) => ({ ...d, purpose }))}
          placeholder={t('account.letters.purposePlaceholder')}
          maxLength={PURPOSE_MAX}
          multiline
          textAlignVertical="top"
          containerStyle={styles.field}
          error={showValidation && validation.errors.purpose ? t('account.letters.validation.tooLong', { max: PURPOSE_MAX }) : undefined}
        />
        <Text style={[theme.typography.footnote, styles.counter, { color: theme.colors.textMuted }]}>
          {t('account.letters.charactersLeft', { count: PURPOSE_MAX - draft.purpose.length })}
        </Text>

        {sheetError ? (
          <Surface tintColor={theme.colors.status.danger.tint} contentStyle={styles.notice}>
            <Ionicons accessible={false} name="alert-circle-outline" size={20} color={theme.colors.status.danger.fg} />
            <Text accessibilityRole="alert" style={[theme.typography.footnote, styles.flex, { color: theme.colors.text }]}>
              {t(`account.letters.errors.${sheetError}`)}
            </Text>
          </Surface>
        ) : null}
      </BottomSheet>
    </View>
  );
}

const styles = StyleSheet.create({
  root: { flex: 1 },
  content: { paddingBottom: 24 },
  list: { paddingHorizontal: 16, gap: 12 },
  state: { marginHorizontal: 16 },
  sectionTitle: { marginTop: 4 },
  row: { padding: 16, gap: 8 },
  rowTop: { flexDirection: 'row', alignItems: 'flex-start', gap: 12 },
  meta: { marginTop: 2 },
  issued: { gap: 10, marginTop: 2 },
  notice: { flexDirection: 'row', alignItems: 'center', gap: 10, padding: 14 },
  flex: { flex: 1, minWidth: 0 },
  center: { textAlign: 'center' },
  label: { fontWeight: '600', marginTop: 4 },
  options: { gap: 8 },
  option: {
    flexDirection: 'row',
    alignItems: 'center',
    gap: 12,
    minHeight: 52,
    paddingHorizontal: 14,
    paddingVertical: 10,
    borderRadius: 14,
    borderWidth: 1,
  },
  field: { marginBottom: 0 },
  counter: { textAlign: 'auto', marginTop: -6 },
});
