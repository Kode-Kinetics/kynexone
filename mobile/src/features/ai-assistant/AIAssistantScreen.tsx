import React, { useCallback, useEffect, useRef, useState } from 'react';
import {
  ActivityIndicator,
  KeyboardAvoidingView,
  Platform,
  ScrollView,
  StyleSheet,
  Text,
  TextInput,
  View,
} from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import Animated, { FadeIn, FadeInDown, FadeOut } from 'react-native-reanimated';
import { useTranslation } from 'react-i18next';
import { aiApi } from '@/api/services';
import { useAuthStore } from '@/auth/authStore';
import { isManagerUser } from '@/navigation/routes';
import { useTheme } from '@/theme/ThemeProvider';
import { useFormat } from '@/utils/format';
import {
  DirectionalIcon,
  LiquidBackdrop,
  MotionPressable,
  ScreenHero,
  SectionHeader,
  Surface,
} from '@/components/ui';
import type { AIMessage } from '@/types';

// Reachable only while FEATURES.AI_ASSISTANT is on (the access policy withholds the surface
// otherwise). It makes no claim about what the answers are grounded in: that is the backend's to prove.

const EMPLOYEE_SUGGESTIONS = [
  { icon: 'calendar-outline', key: 'leaveDays' },
  { icon: 'stats-chart-outline', key: 'attendanceMonth' },
  { icon: 'wallet-outline', key: 'lastPayslip' },
  { icon: 'document-text-outline', key: 'expiringDocs' },
] as const;

const MANAGER_SUGGESTIONS = [
  { icon: 'people-outline', key: 'absentToday' },
  { icon: 'checkmark-done-outline', key: 'pendingApprovals' },
  { icon: 'time-outline', key: 'teamOvertime' },
] as const;

export default function AIAssistantScreen() {
  const { user } = useAuthStore();
  const { t } = useTranslation();
  const { theme, reduceMotion } = useTheme();
  const [messages, setMessages] = useState<AIMessage[]>([]);
  const [input, setInput] = useState('');
  const [loading, setLoading] = useState(false);
  const scrollRef = useRef<ScrollView>(null);
  const suggestions = isManagerUser(user) ? MANAGER_SUGGESTIONS : EMPLOYEE_SUGGESTIONS;

  const send = useCallback(async (text: string) => {
    const question = text.trim();
    if (!question || loading) return;
    setMessages((current) => [...current, {
      id: `${Date.now()}-user`, role: 'user', content: question, timestamp: new Date().toISOString(),
    }]);
    setInput('');
    setLoading(true);
    try {
      const response = await aiApi.ask({ question });
      setMessages((current) => [...current, {
        id: `${Date.now()}-assistant`, role: 'assistant', content: response.answer, timestamp: new Date().toISOString(),
      }]);
    } catch {
      setMessages((current) => [...current, {
        id: `${Date.now()}-error`, role: 'assistant', content: t('account.ai.failed'), timestamp: new Date().toISOString(),
      }]);
    } finally {
      setLoading(false);
    }
  }, [loading, t]);

  useEffect(() => {
    requestAnimationFrame(() => scrollRef.current?.scrollToEnd({ animated: !reduceMotion }));
  }, [loading, messages, reduceMotion]);

  return (
    <KeyboardAvoidingView
      style={[styles.root, { backgroundColor: theme.colors.canvas }]}
      behavior={Platform.OS === 'ios' ? 'padding' : undefined}
      keyboardVerticalOffset={Platform.OS === 'ios' ? 8 : 0}
    >
      <LiquidBackdrop subtle />
      <ScrollView ref={scrollRef} contentContainerStyle={styles.content} keyboardShouldPersistTaps="handled" showsVerticalScrollIndicator={false}>
        <ScreenHero eyebrow={t('account.ai.eyebrow')} title={t('account.ai.title')} subtitle={t('account.ai.subtitle')} />

        {messages.length === 0 ? (
          <>
            <View style={styles.section}>
              <Surface tintColor={theme.colors.status.warning.tint} contentStyle={styles.note}>
                <Ionicons accessible={false} name="information-circle-outline" size={20} color={theme.colors.status.warning.fg} />
                <Text style={[theme.typography.footnote, styles.flex, { color: theme.colors.text }]}>{t('account.ai.advisory')}</Text>
              </Surface>
            </View>
            <View style={styles.section}>
              <SectionHeader title={t('account.ai.suggested')} />
              <Surface contentStyle={styles.list}>
                {suggestions.map((s, index) => {
                  const question = t(`account.ai.questions.${s.key}`);
                  return (
                    <MotionPressable
                      key={s.key}
                      onPress={() => void send(question)}
                      accessibilityRole="button"
                      accessibilityLabel={question}
                      contentStyle={[
                        styles.suggestion,
                        index < suggestions.length - 1 && { borderBottomColor: theme.colors.hairline, borderBottomWidth: StyleSheet.hairlineWidth },
                      ]}
                    >
                      <Ionicons accessible={false} name={s.icon} size={20} color={theme.colors.accent} />
                      <Text style={[theme.typography.callout, styles.flex, { color: theme.colors.text }]}>{question}</Text>
                      <DirectionalIcon name="chevron" size={16} color={theme.colors.textMuted} />
                    </MotionPressable>
                  );
                })}
              </Surface>
            </View>
          </>
        ) : (
          <View style={[styles.section, styles.chat]}>
            {messages.map((message) => <ChatBubble key={message.id} message={message} reduceMotion={reduceMotion} />)}
            {loading ? (
              <Animated.View
                entering={reduceMotion ? undefined : FadeIn.duration(140)}
                exiting={reduceMotion ? undefined : FadeOut.duration(120)}
                accessibilityRole="progressbar"
                accessibilityLabel={t('account.ai.thinking')}
                accessibilityLiveRegion="polite"
                style={styles.thinking}
              >
                <ActivityIndicator size="small" color={theme.colors.accent} />
                <Text style={[theme.typography.footnote, { color: theme.colors.textSecondary }]}>{t('account.ai.thinking')}</Text>
              </Animated.View>
            ) : null}
          </View>
        )}
      </ScrollView>

      <View style={[styles.composerArea, { backgroundColor: theme.colors.chromeSolid, borderTopColor: theme.colors.hairline }]}>
        <View style={[styles.composer, { backgroundColor: theme.colors.surfaceInset }]}>
          <TextInput
            value={input}
            onChangeText={setInput}
            placeholder={t('account.ai.placeholder')}
            placeholderTextColor={theme.colors.textMuted}
            selectionColor={theme.colors.accent}
            multiline
            maxLength={800}
            style={[theme.typography.body, styles.input, { color: theme.colors.text }]}
            accessibilityLabel={t('account.ai.inputLabel')}
          />
          <MotionPressable
            onPress={() => void send(input)}
            disabled={loading || !input.trim()}
            haptic="commit"
            accessibilityRole="button"
            accessibilityLabel={t('account.ai.send')}
            contentStyle={[styles.send, { backgroundColor: input.trim() && !loading ? theme.colors.primaryFill : theme.colors.card }]}
          >
            <Ionicons accessible={false} name="arrow-up" size={21} color={input.trim() && !loading ? theme.colors.onFill : theme.colors.textMuted} />
          </MotionPressable>
        </View>
      </View>
    </KeyboardAvoidingView>
  );
}

function ChatBubble({ message, reduceMotion }: { message: AIMessage; reduceMotion: boolean }) {
  const { theme } = useTheme();
  const { t } = useTranslation();
  const f = useFormat();
  const mine = message.role === 'user';
  return (
    <Animated.View
      entering={reduceMotion ? undefined : FadeInDown.duration(200)}
      style={[styles.bubbleWrap, { alignItems: mine ? 'flex-end' : 'flex-start' }]}
    >
      <View
        accessible
        accessibilityLabel={mine ? message.content : t('account.ai.answerA11y', { text: message.content })}
        accessibilityLiveRegion={mine ? 'none' : 'polite'}
        style={[styles.bubble, { backgroundColor: mine ? theme.colors.primaryFill : theme.colors.card, borderColor: theme.colors.hairline }]}
      >
        <Text style={[theme.typography.body, { color: mine ? theme.colors.onFill : theme.colors.text }]}>{message.content}</Text>
      </View>
      <Text style={[theme.typography.caption, styles.time, { color: theme.colors.textMuted }]}>{f.time(message.timestamp)}</Text>
    </Animated.View>
  );
}

const styles = StyleSheet.create({
  root: { flex: 1 },
  content: { flexGrow: 1, paddingBottom: 12 },
  section: { paddingHorizontal: 16, marginTop: 12 },
  flex: { flex: 1, minWidth: 0 },
  note: { flexDirection: 'row', alignItems: 'flex-start', gap: 10, padding: 14 },
  list: { paddingHorizontal: 14 },
  suggestion: { flexDirection: 'row', alignItems: 'center', gap: 12, minHeight: 56, paddingVertical: 10 },
  chat: { gap: 12 },
  thinking: { flexDirection: 'row', alignItems: 'center', gap: 8 },
  bubbleWrap: { width: '100%' },
  bubble: { maxWidth: '86%', borderRadius: 18, borderWidth: StyleSheet.hairlineWidth, paddingHorizontal: 14, paddingVertical: 11 },
  time: { marginTop: 4, marginHorizontal: 4 },
  composerArea: { paddingHorizontal: 12, paddingTop: 8, paddingBottom: 10, borderTopWidth: StyleSheet.hairlineWidth },
  composer: { flexDirection: 'row', alignItems: 'flex-end', gap: 8, borderRadius: 22, paddingStart: 14, paddingEnd: 6, paddingVertical: 6 },
  input: { flex: 1, minHeight: 44, maxHeight: 118, paddingTop: 11, paddingBottom: 10 },
  send: { width: 44, height: 44, borderRadius: 22, alignItems: 'center', justifyContent: 'center' },
});
