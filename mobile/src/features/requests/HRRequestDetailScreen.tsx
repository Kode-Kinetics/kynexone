import React, { useCallback, useEffect, useRef, useState } from 'react';
import {
  ActivityIndicator,
  Alert,
  KeyboardAvoidingView,
  Platform,
  ScrollView,
  StyleSheet,
  Text,
  TextInput,
  View,
} from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import Animated, { FadeInDown, FadeOutUp } from 'react-native-reanimated';
import { useNavigation, useRoute } from '@react-navigation/native';
import { hrRequestsApi } from '@/api/adapters';
import type { HRRequest } from '@/types';
import { formatDate } from '@/utils/date';
import { useAuthStore } from '@/auth/authStore';
import {
  GlassSurface,
  LiquidBackdrop,
  MotionPressable,
  ScreenHero,
  SectionHeader,
} from '@/components/ui';
import { useTheme } from '@/theme/ThemeProvider';

const STATUS_STEPS = ['Open', 'InProgress', 'Resolved', 'Closed'];

export default function HRRequestDetailScreen() {
  const route = useRoute<any>();
  const navigation = useNavigation();
  const { theme, reduceMotion } = useTheme();
  const { user } = useAuthStore();
  const { id } = route.params as { id: string };
  const scrollRef = useRef<ScrollView>(null);

  const [request, setRequest] = useState<HRRequest | null>(null);
  const [loading, setLoading] = useState(true);
  const [comment, setComment] = useState('');
  const [sending, setSending] = useState(false);
  const [completionMessage, setCompletionMessage] = useState<string | null>(null);

  const load = useCallback(async () => {
    try {
      setRequest(await hrRequestsApi.getDetail(id));
    } catch (error: any) {
      Alert.alert('Could not load request', error?.message || 'Please try again.');
      navigation.goBack();
    } finally {
      setLoading(false);
    }
  }, [id, navigation]);

  useEffect(() => {
    void load();
  }, [load]);

  const sendComment = async () => {
    const message = comment.trim();
    if (!message || sending) return;

    setSending(true);
    try {
      await hrRequestsApi.addComment(id, message);
      setComment('');
      await load();
      setCompletionMessage('Comment sent.');
      requestAnimationFrame(() => {
        scrollRef.current?.scrollToEnd({ animated: !reduceMotion });
      });
    } catch (error: any) {
      Alert.alert('Could not send comment', error?.message || 'Please try again.');
    } finally {
      setSending(false);
    }
  };

  if (loading) {
    return (
      <View style={[styles.loadingRoot, { backgroundColor: theme.colors.canvas }]}>
        <LiquidBackdrop subtle />
        <GlassSurface
          accessibilityRole="progressbar"
          accessibilityLabel="Loading HR request"
          accessibilityLiveRegion="polite"
          radius={theme.radius.xl}
          contentStyle={styles.loadingCard}
        >
          <ActivityIndicator color={theme.colors.primary} />
          <Text style={[theme.typography.caption, { color: theme.colors.textSecondary }]}>
            Loading request…
          </Text>
        </GlassSurface>
      </View>
    );
  }

  if (!request) return null;

  const canComment = !['Closed', 'Resolved', 'Cancelled'].includes(request.status);

  return (
    <KeyboardAvoidingView
      style={[styles.root, { backgroundColor: theme.colors.canvas }]}
      behavior={Platform.OS === 'ios' ? 'padding' : 'height'}
    >
      <LiquidBackdrop subtle />
      <ScrollView
        ref={scrollRef}
        contentContainerStyle={[styles.scroll, canComment && styles.scrollWithComposer]}
        keyboardShouldPersistTaps="handled"
        keyboardDismissMode={Platform.OS === 'ios' ? 'interactive' : 'on-drag'}
        automaticallyAdjustKeyboardInsets={Platform.OS === 'ios'}
        showsVerticalScrollIndicator={false}
      >
        <ScreenHero
          eyebrow={request.ticketNumber ? 'Request #' + request.ticketNumber : 'HR request'}
          title={request.subject}
          subtitle={request.requestType}
          onBack={() => navigation.goBack()}
        />

        <View style={styles.content}>
          <GlassSurface elevated={false} radius={theme.radius.xl} contentStyle={styles.statusCard}>
            <SectionHeader title="Status" subtitle="Request progress" />
            <Timeline status={request.status} />
          </GlassSurface>

          <GlassSurface elevated={false} radius={theme.radius.xl} contentStyle={styles.detailsCard}>
            {[
              { label: 'Type', value: request.requestType },
              { label: 'Submitted', value: formatDate(request.createdAt, 'dateTime') },
              { label: 'Assigned to', value: request.assignedTo },
              { label: 'Response', value: request.responseStatus ?? request.slaStatus },
            ]
              .filter((row) => row.value)
              .map((row) => (
                <DetailRow key={row.label} label={row.label} value={String(row.value)} />
              ))}
          </GlassSurface>

          <GlassSurface elevated={false} radius={theme.radius.xl} contentStyle={styles.descriptionCard}>
            <Text style={[theme.typography.bodyStrong, { color: theme.colors.text }]}>
              Description
            </Text>
            <Text style={[theme.typography.body, styles.description, { color: theme.colors.textSecondary }]}>
              {request.description}
            </Text>
          </GlassSurface>

          {request.comments && request.comments.length > 0 ? (
            <View>
              <SectionHeader title="Conversation" subtitle={request.comments.length + ' messages'} />
              <View style={styles.thread}>
                {request.comments.map((entry) => {
                  const isMe = String(entry.authorId ?? '') === String(user?.employeeId ?? '');
                  return (
                    <View
                      key={entry.id}
                      style={[
                        styles.messageWrap,
                        isMe ? styles.messageWrapMine : styles.messageWrapOther,
                      ]}
                    >
                      <GlassSurface
                        elevated={false}
                        radius={18}
                        style={styles.messageSurface}
                        contentStyle={styles.message}
                        tintColor={
                          isMe
                            ? theme.isDark
                              ? 'rgba(47,107,255,0.28)'
                              : 'rgba(47,107,255,0.15)'
                            : undefined
                        }
                      >
                        {!isMe ? (
                          <Text style={[theme.typography.micro, styles.author, { color: theme.colors.primary }]}>
                            {entry.authorName}
                          </Text>
                        ) : null}
                        <Text style={[theme.typography.body, { color: theme.colors.text }]}>
                          {entry.content ?? entry.message}
                        </Text>
                      </GlassSurface>
                      <Text
                        style={[
                          theme.typography.micro,
                          styles.messageTime,
                          isMe && styles.messageTimeMine,
                          { color: theme.colors.textMuted },
                        ]}
                      >
                        {formatDate(entry.createdAt, 'time')}
                      </Text>
                    </View>
                  );
                })}
              </View>
            </View>
          ) : (
            <GlassSurface elevated={false} radius={theme.radius.xl} contentStyle={styles.emptyConversation}>
              <Ionicons name="chatbubbles-outline" size={22} color={theme.colors.textMuted} />
              <Text style={[theme.typography.caption, { color: theme.colors.textSecondary }]}>
                No conversation yet.
              </Text>
            </GlassSurface>
          )}
        </View>
      </ScrollView>

      {canComment ? (
        <View
          style={[
            styles.composerShell,
            {
              backgroundColor: theme.colors.canvas,
              borderTopColor: theme.colors.divider,
            },
          ]}
        >
          {completionMessage ? (
            <Animated.View
              entering={reduceMotion ? undefined : FadeInDown.duration(200)}
              exiting={reduceMotion ? undefined : FadeOutUp.duration(140)}
              style={styles.completionWrap}
            >
              <View
                accessibilityRole="alert"
                accessibilityLiveRegion="polite"
                style={[styles.completionBanner, { backgroundColor: theme.colors.surfaceStrong }]}
              >
                <Ionicons name="checkmark-circle" size={20} color={theme.colors.success} />
                <Text style={[theme.typography.caption, styles.completionText, { color: theme.colors.text }]}>
                  {completionMessage}
                </Text>
                <MotionPressable
                  accessibilityRole="button"
                  accessibilityLabel="Dismiss comment confirmation"
                  onPress={() => setCompletionMessage(null)}
                  haptic="selection"
                  contentStyle={styles.completionDismiss}
                >
                  <Ionicons name="close" size={17} color={theme.colors.textMuted} />
                </MotionPressable>
              </View>
            </Animated.View>
          ) : null}
          <GlassSurface
            elevated={false}
            radius={22}
            style={styles.composerSurface}
            contentStyle={styles.composer}
          >
            <TextInput
              value={comment}
              onChangeText={setComment}
              placeholder="Add a comment…"
              placeholderTextColor={theme.colors.textMuted}
              multiline
              maxLength={2000}
              accessibilityLabel="Comment"
              style={[
                theme.typography.body,
                styles.commentInput,
                { color: theme.colors.text },
              ]}
            />
            <MotionPressable
              accessibilityRole="button"
              accessibilityLabel="Send comment"
              accessibilityState={{ disabled: sending || !comment.trim(), busy: sending }}
              onPress={() => void sendComment()}
              disabled={sending || !comment.trim()}
              haptic="medium"
              contentStyle={[
                styles.sendButton,
                {
                  backgroundColor: comment.trim()
                    ? theme.colors.primary
                    : theme.colors.surfaceMuted,
                },
              ]}
            >
              {sending ? (
                <ActivityIndicator color="#FFFFFF" size="small" />
              ) : (
                <Ionicons name="arrow-up" size={20} color="#FFFFFF" />
              )}
            </MotionPressable>
          </GlassSurface>
        </View>
      ) : null}
    </KeyboardAvoidingView>
  );
}

function Timeline({ status }: { status: string }) {
  const { theme } = useTheme();
  const currentIndex = STATUS_STEPS.indexOf(status);
  const normalizedIndex = currentIndex >= 0 ? currentIndex : 0;

  return (
    <View
      accessible
      accessibilityLabel={`Current request status: ${humanizeStatus(status)}`}
      accessibilityLiveRegion="polite"
      style={styles.timeline}
    >
      {STATUS_STEPS.map((step, index) => {
        const complete = index < normalizedIndex;
        const active = index === normalizedIndex;
        const reached = index <= normalizedIndex;
        return (
          <View key={step} style={styles.timelineItem}>
            <View style={styles.timelineRail}>
              <View
                style={[
                  styles.timelineDot,
                  {
                    backgroundColor: active
                      ? theme.colors.primary
                      : complete
                        ? theme.colors.success
                        : theme.colors.surfaceMuted,
                  },
                ]}
              >
                <Ionicons
                  name={complete ? 'checkmark' : active ? 'ellipse' : 'ellipse-outline'}
                  size={complete ? 14 : 10}
                  color={reached ? '#FFFFFF' : theme.colors.textMuted}
                />
              </View>
              {index < STATUS_STEPS.length - 1 ? (
                <View
                  style={[
                    styles.timelineLine,
                    {
                      backgroundColor: complete
                        ? theme.colors.success
                        : theme.colors.divider,
                    },
                  ]}
                />
              ) : null}
            </View>
            <View style={styles.timelineCopy}>
              <Text
                style={[
                  theme.typography.bodyStrong,
                  { color: reached ? theme.colors.text : theme.colors.textMuted },
                ]}
              >
                {humanizeStatus(step)}
              </Text>
              {active ? (
                <Text style={[theme.typography.micro, { color: theme.colors.primary }]}>
                  Current status
                </Text>
              ) : null}
            </View>
          </View>
        );
      })}
    </View>
  );
}

function DetailRow({ label, value }: { label: string; value: string }) {
  const { theme } = useTheme();
  return (
    <View style={[styles.detailRow, { borderBottomColor: theme.colors.divider }]}>
      <Text style={[theme.typography.caption, styles.detailLabel, { color: theme.colors.textSecondary }]}>
        {label}
      </Text>
      <Text style={[theme.typography.caption, styles.detailValue, { color: theme.colors.text }]}>
        {value}
      </Text>
    </View>
  );
}

function humanizeStatus(status: string) {
  return status.replace(/([a-z])([A-Z])/g, '$1 $2');
}

const styles = StyleSheet.create({
  root: { flex: 1 },
  loadingRoot: { flex: 1, alignItems: 'center', justifyContent: 'center', padding: 24 },
  loadingCard: { minWidth: 220, minHeight: 130, alignItems: 'center', justifyContent: 'center', gap: 12 },
  scroll: { paddingBottom: 34 },
  scrollWithComposer: { paddingBottom: 118 },
  content: { paddingHorizontal: 16, paddingTop: 12, gap: 14 },
  statusCard: { padding: 16 },
  timeline: { paddingTop: 2 },
  timelineItem: { minHeight: 52, flexDirection: 'row' },
  timelineRail: { width: 34, alignItems: 'center' },
  timelineDot: {
    width: 28,
    height: 28,
    borderRadius: 14,
    alignItems: 'center',
    justifyContent: 'center',
  },
  timelineLine: { width: 2, flex: 1, minHeight: 20 },
  timelineCopy: { flex: 1, minWidth: 0, paddingTop: 3, paddingBottom: 13, gap: 1 },
  detailsCard: { paddingHorizontal: 15, paddingVertical: 5 },
  detailRow: {
    minHeight: 48,
    borderBottomWidth: StyleSheet.hairlineWidth,
    flexDirection: 'row',
    alignItems: 'center',
    gap: 14,
    paddingVertical: 9,
  },
  detailLabel: { flexShrink: 0 },
  detailValue: { flex: 1, textAlign: 'right', fontWeight: '600' },
  descriptionCard: { padding: 16 },
  description: { marginTop: 7, lineHeight: 21 },
  thread: { gap: 10 },
  messageWrap: { maxWidth: '86%' },
  messageWrapMine: { alignSelf: 'flex-end' },
  messageWrapOther: { alignSelf: 'flex-start' },
  messageSurface: { maxWidth: '100%' },
  message: { paddingHorizontal: 13, paddingVertical: 11, gap: 4 },
  author: { fontWeight: '800' },
  messageTime: { marginTop: 3, marginLeft: 5 },
  messageTimeMine: { alignSelf: 'flex-end', marginRight: 5 },
  emptyConversation: {
    minHeight: 74,
    flexDirection: 'row',
    alignItems: 'center',
    justifyContent: 'center',
    gap: 8,
  },
  composerShell: {
    position: 'absolute',
    left: 0,
    right: 0,
    bottom: 0,
    borderTopWidth: StyleSheet.hairlineWidth,
    paddingHorizontal: 12,
    paddingTop: 8,
    paddingBottom: Platform.OS === 'ios' ? 24 : 12,
  },
  composerSurface: { width: '100%' },
  completionWrap: { paddingBottom: 7 },
  completionBanner: { minHeight: 44, borderRadius: 16, flexDirection: 'row', alignItems: 'center', gap: 9, paddingLeft: 12, paddingRight: 4 },
  completionText: { flex: 1 },
  completionDismiss: { width: 44, height: 44, alignItems: 'center', justifyContent: 'center' },
  composer: {
    minHeight: 54,
    flexDirection: 'row',
    alignItems: 'flex-end',
    gap: 8,
    paddingLeft: 14,
    paddingRight: 7,
    paddingVertical: 7,
  },
  commentInput: { flex: 1, minHeight: 40, maxHeight: 104, paddingVertical: 9 },
  sendButton: {
    width: 44,
    height: 44,
    borderRadius: 17,
    alignItems: 'center',
    justifyContent: 'center',
  },
});
