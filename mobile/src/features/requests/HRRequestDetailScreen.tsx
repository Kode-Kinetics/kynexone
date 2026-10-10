import React, { useCallback, useEffect, useRef, useState } from 'react';
import { KeyboardAvoidingView, Platform, ScrollView, StyleSheet, Text, TextInput, View } from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import { useRoute } from '@react-navigation/native';
import { useTranslation } from 'react-i18next';
import { hrRequestsApi } from '@/api/adapters';
import type { HRRequest } from '@/types';
import { useAuthStore } from '@/auth/authStore';
import { useTheme } from '@/theme/ThemeProvider';
import { useFormat } from '@/utils/format';
import { haptics } from '@/utils/haptics';
import {
  LiquidBackdrop,
  MotionPressable,
  ScreenHero,
  SkeletonCard,
  StateCard,
  StatusPill,
  StickyFooter,
  Surface,
} from '@/components/ui';
import { useRequestStatus, useRequestSubject, useRequestTypeLabel } from './HRRequestsScreen';

const STEPS: HRRequest['status'][] = ['Open', 'InProgress', 'Resolved', 'Closed'];

/** Server responseStatus (English) → translation key. */
function responseKey(value?: string): 'closed' | 'responded' | 'overdue' | 'awaiting' | null {
  const v = (value ?? '').toLowerCase();
  if (!v) return null;
  if (v.startsWith('closed')) return 'closed';
  if (v.startsWith('responded')) return 'responded';
  if (v.startsWith('overdue')) return 'overdue';
  if (v.startsWith('awaiting')) return 'awaiting';
  return null;
}

export default function HRRequestDetailScreen() {
  const route = useRoute<any>();
  const { theme, reduceMotion } = useTheme();
  const { t } = useTranslation();
  const f = useFormat();
  const typeLabel = useRequestTypeLabel();
  const subjectLabel = useRequestSubject();
  const statusOf = useRequestStatus();
  const { user } = useAuthStore();
  const { id } = route.params as { id: string };
  const scrollRef = useRef<ScrollView>(null);

  const [request, setRequest] = useState<HRRequest | null>(null);
  const [state, setState] = useState<'loading' | 'ready' | 'failed'>('loading');
  const [comment, setComment] = useState('');
  const [sending, setSending] = useState(false);
  const [message, setMessage] = useState<{ ok: boolean; text: string } | null>(null);

  const load = useCallback(async () => {
    try {
      setRequest(await hrRequestsApi.getDetail(id));
      setState('ready');
    } catch {
      setState((s) => (s === 'ready' ? s : 'failed'));
    }
  }, [id]);

  useEffect(() => {
    void load();
  }, [load]);

  const send = async () => {
    const text = comment.trim();
    if (!text || sending) return;
    setSending(true);
    setMessage(null);
    try {
      await hrRequestsApi.addComment(id, text);
      setComment('');
      haptics.success();
      setMessage({ ok: true, text: t('manager.hr.detail.sent') });
      await load();
      requestAnimationFrame(() => scrollRef.current?.scrollToEnd({ animated: !reduceMotion }));
    } catch {
      haptics.error();
      setMessage({ ok: false, text: t('manager.hr.detail.sendError') });
    } finally {
      setSending(false);
    }
  };

  if (!request) {
    return (
      <View style={[styles.root, { backgroundColor: theme.colors.canvas }]}>
        <LiquidBackdrop subtle />
        <ScreenHero title={t('manager.hr.title')} />
        <View style={styles.content}>
          {state === 'failed' ? (
            <StateCard
              variant="error"
              title={t('manager.hr.detail.errorTitle')}
              onRetry={() => {
                setState('loading');
                void load();
              }}
            />
          ) : (
            <>
              <SkeletonCard lines={2} />
              <SkeletonCard lines={4} />
            </>
          )}
        </View>
      </View>
    );
  }

  const open = request.status === 'Open' || request.status === 'InProgress';
  const status = statusOf(request.status);
  const stepIndex = Math.max(0, STEPS.indexOf(request.status));
  const response = responseKey(request.responseStatus);
  const tx = t as unknown as (k: string) => string;

  return (
    <KeyboardAvoidingView style={[styles.root, { backgroundColor: theme.colors.canvas }]} behavior={Platform.OS === 'ios' ? 'padding' : undefined}>
      <LiquidBackdrop subtle />
      <ScrollView
        ref={scrollRef}
        contentContainerStyle={styles.scroll}
        keyboardShouldPersistTaps="handled"
        keyboardDismissMode={Platform.OS === 'ios' ? 'interactive' : 'on-drag'}
        showsVerticalScrollIndicator={false}
      >
        <ScreenHero
          eyebrow={request.ticketNumber ? t('manager.hr.detail.requestNo', { number: request.ticketNumber }) : typeLabel(request.requestType)}
          title={subjectLabel(request.subject)}
        />
        <View style={styles.content}>
          <View style={styles.pills}>
            <StatusPill tone={status.tone} label={status.label} />
            {request.slaBreached && open ? <StatusPill tone="danger" label={t('manager.hr.breached')} /> : null}
          </View>

          {request.status !== 'Cancelled' ? (
            <Surface contentStyle={styles.card}>
              <Text accessibilityRole="header" style={[theme.typography.headline, { color: theme.colors.text }]}>{t('manager.hr.detail.progress')}</Text>
              <View accessible accessibilityLabel={t('manager.hr.detail.progressA11y', { status: status.label })}>
                {STEPS.map((step, i) => {
                  const done = i < stepIndex;
                  const current = i === stepIndex;
                  return (
                    <View key={step} style={styles.step}>
                      <View style={styles.rail}>
                        <View
                          style={[
                            styles.dot,
                            { backgroundColor: current ? theme.colors.primaryFill : done ? theme.colors.status.success.fill : theme.colors.surfaceInset },
                          ]}
                        >
                          {done ? <Ionicons accessible={false} name="checkmark" size={13} color={theme.colors.onFill} /> : null}
                        </View>
                        {i < STEPS.length - 1 ? (
                          <View style={[styles.line, { backgroundColor: done ? theme.colors.status.success.fill : theme.colors.hairline }]} />
                        ) : null}
                      </View>
                      <View style={styles.stepCopy}>
                        <Text style={[theme.typography.callout, { color: done || current ? theme.colors.text : theme.colors.textMuted }]}>
                          {tx(`manager.hr.status.${step}`)}
                        </Text>
                        {current ? (
                          <Text style={[theme.typography.footnote, { color: theme.colors.accent }]}>{t('manager.hr.detail.current')}</Text>
                        ) : null}
                      </View>
                    </View>
                  );
                })}
              </View>
            </Surface>
          ) : null}

          <Surface contentStyle={styles.facts}>
            <Fact label={t('manager.hr.detail.type')} value={typeLabel(request.requestType)} />
            {request.createdAt ? <Fact label={t('manager.hr.detail.submitted')} value={f.dateTime(request.createdAt)} /> : null}
            {request.assignedTo ? <Fact label={t('manager.hr.detail.assignedTo')} value={request.assignedTo} /> : null}
            {response ? <Fact label={t('manager.hr.detail.response')} value={tx(`manager.hr.response.${response}`)} last /> : null}
          </Surface>

          {request.description ? (
            <Surface contentStyle={styles.card}>
              <Text accessibilityRole="header" style={[theme.typography.headline, { color: theme.colors.text }]}>{t('manager.hr.detail.details')}</Text>
              <Text style={[theme.typography.callout, { color: theme.colors.textSecondary }]}>{request.description}</Text>
            </Surface>
          ) : null}

          <Text accessibilityRole="header" style={[theme.typography.title3, { color: theme.colors.text }]}>{t('manager.hr.detail.conversation')}</Text>
          {request.comments?.length ? (
            <View style={styles.thread}>
              {request.comments.map((entry) => {
                const mine = String(entry.authorId ?? '') !== '' && String(entry.authorId) === String(user?.employeeId ?? '');
                const author = mine
                  ? t('manager.hr.detail.you')
                  : entry.authorRole === 'HR' && (!entry.authorName || entry.authorName === 'HR')
                    ? t('manager.hr.detail.hr')
                    : entry.authorName;
                return (
                  <View key={entry.id} style={[styles.bubbleWrap, mine ? styles.mine : styles.theirs]}>
                    <Surface
                      level={mine ? 0 : 1}
                      tintColor={mine ? theme.colors.status.info.tint : undefined}
                      radius={16}
                      contentStyle={styles.bubble}
                    >
                      <Text style={[theme.typography.footnote, styles.semibold, { color: mine ? theme.colors.textSecondary : theme.colors.accent }]}>
                        {author}
                      </Text>
                      <Text style={[theme.typography.callout, { color: theme.colors.text }]}>{entry.content ?? entry.message}</Text>
                    </Surface>
                    <Text style={[theme.typography.caption, styles.time, { color: theme.colors.textMuted }]}>{f.dateTime(entry.createdAt)}</Text>
                  </View>
                );
              })}
            </View>
          ) : (
            <Surface level={0} contentStyle={styles.card}>
              <Text style={[theme.typography.subheadline, { color: theme.colors.textSecondary }]}>{t('manager.hr.detail.noConversation')}</Text>
            </Surface>
          )}
          {!open ? (
            <Text style={[theme.typography.footnote, { color: theme.colors.textMuted }]}>{t('manager.hr.detail.closedNote')}</Text>
          ) : null}
        </View>
      </ScrollView>

      {open ? (
        <StickyFooter>
          {message ? (
            <Text
              accessibilityRole="alert"
              accessibilityLiveRegion="polite"
              style={[theme.typography.footnote, { color: message.ok ? theme.colors.status.success.fg : theme.colors.status.danger.fg }]}
            >
              {message.text}
            </Text>
          ) : null}
          <View style={styles.composer}>
            <TextInput
              value={comment}
              onChangeText={setComment}
              placeholder={t('manager.hr.detail.replyPlaceholder')}
              accessibilityLabel={t('manager.hr.detail.replyLabel')}
              placeholderTextColor={theme.colors.textMuted}
              selectionColor={theme.colors.accent}
              multiline
              maxLength={2000}
              style={[
                theme.typography.callout,
                styles.input,
                { color: theme.colors.text, backgroundColor: theme.colors.surfaceInset, borderColor: theme.colors.hairline },
              ]}
            />
            <MotionPressable
              accessibilityRole="button"
              accessibilityLabel={t('manager.hr.detail.send')}
              accessibilityState={{ disabled: sending || !comment.trim(), busy: sending }}
              disabled={sending || !comment.trim()}
              haptic="commit"
              onPress={() => void send()}
              contentStyle={[
                styles.send,
                { backgroundColor: comment.trim() ? theme.colors.primaryFill : theme.colors.surfaceInset },
              ]}
            >
              <Ionicons
                accessible={false}
                name="arrow-up"
                size={20}
                color={comment.trim() ? theme.colors.onFill : theme.colors.textMuted}
              />
            </MotionPressable>
          </View>
        </StickyFooter>
      ) : null}
    </KeyboardAvoidingView>
  );
}

function Fact({ label, value, last }: { label: string; value: string; last?: boolean }) {
  const { theme } = useTheme();
  return (
    <View accessible style={[styles.fact, !last && { borderBottomColor: theme.colors.hairline, borderBottomWidth: StyleSheet.hairlineWidth }]}>
      <Text style={[theme.typography.footnote, { color: theme.colors.textSecondary }]}>{label}</Text>
      <Text style={[theme.typography.callout, { color: theme.colors.text }]}>{value}</Text>
    </View>
  );
}

const styles = StyleSheet.create({
  root: { flex: 1 },
  scroll: { paddingBottom: 24 },
  content: { paddingHorizontal: 16, gap: 14 },
  pills: { flexDirection: 'row', flexWrap: 'wrap', gap: 6 },
  card: { padding: 16, gap: 8 },
  facts: { paddingHorizontal: 16 },
  fact: { paddingVertical: 10, gap: 2 },
  step: { flexDirection: 'row', minHeight: 44 },
  rail: { width: 30, alignItems: 'center' },
  dot: { width: 22, height: 22, borderRadius: 11, alignItems: 'center', justifyContent: 'center' },
  line: { width: 2, flex: 1, minHeight: 14 },
  stepCopy: { flex: 1, paddingBottom: 10, paddingStart: 6 },
  semibold: { fontWeight: '600' },
  thread: { gap: 10 },
  bubbleWrap: { maxWidth: '86%', gap: 3 },
  mine: { alignSelf: 'flex-end' },
  theirs: { alignSelf: 'flex-start' },
  bubble: { paddingHorizontal: 12, paddingVertical: 10, gap: 3 },
  time: { paddingHorizontal: 4 },
  composer: { flexDirection: 'row', alignItems: 'flex-end', gap: 8 },
  input: { flex: 1, minHeight: 44, maxHeight: 120, borderRadius: 14, borderWidth: StyleSheet.hairlineWidth, paddingHorizontal: 12, paddingVertical: 10, textAlign: 'auto' },
  send: { width: 44, height: 44, borderRadius: 22, alignItems: 'center', justifyContent: 'center' },
});
