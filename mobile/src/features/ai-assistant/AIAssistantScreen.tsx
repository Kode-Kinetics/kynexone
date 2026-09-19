import React, { useCallback, useRef, useState } from 'react';
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
import { aiApi } from '@/api/services';
import { useAuthStore } from '@/auth/authStore';
import { isManagerUser } from '@/navigation/routes';
import { useTheme } from '@/theme/ThemeProvider';
import {
  GlassSurface,
  LiquidBackdrop,
  MotionPressable,
  ScreenHero,
  SectionHeader,
} from '@/components/ui';
import type { AIMessage } from '@/types';

const employeeSuggestions = [
  { icon: 'calendar-outline' as const, question: 'How many leave days do I have?' },
  { icon: 'stats-chart-outline' as const, question: 'What is my attendance this month?' },
  { icon: 'wallet-outline' as const, question: 'When was my last payslip?' },
  { icon: 'document-text-outline' as const, question: 'Which documents are expiring soon?' },
  { icon: 'chatbubble-ellipses-outline' as const, question: 'Where is my HR request?' },
  { icon: 'book-outline' as const, question: 'What is the leave policy?' },
];

const managerSuggestions = [
  { icon: 'people-outline' as const, question: 'Who is absent today in my team?' },
  { icon: 'checkmark-done-outline' as const, question: 'Which approvals are pending?' },
  { icon: 'time-outline' as const, question: 'Show overtime trend for my team' },
  { icon: 'warning-outline' as const, question: 'Which employees have frequent late attendance?' },
  { icon: 'analytics-outline' as const, question: "Summarize this month's team attendance" },
];

export default function AIAssistantScreen() {
  const { user } = useAuthStore();
  const { theme } = useTheme();
  const [messages, setMessages] = useState<AIMessage[]>([]);
  const [input, setInput] = useState('');
  const [loading, setLoading] = useState(false);
  const scrollRef = useRef<ScrollView>(null);
  const manager = isManagerUser(user);
  const suggestions = manager ? managerSuggestions : employeeSuggestions;

  const send = useCallback(async (text: string) => {
    const question = text.trim();
    if (!question || loading) return;

    const userMessage: AIMessage = {
      id: `${Date.now()}-user`,
      role: 'user',
      content: question,
      timestamp: new Date().toISOString(),
    };
    setMessages((current) => [...current, userMessage]);
    setInput('');
    setLoading(true);
    setTimeout(() => scrollRef.current?.scrollToEnd({ animated: true }), 100);

    try {
      const response = await aiApi.ask({ question });
      setMessages((current) => [
        ...current,
        {
          id: `${Date.now()}-assistant`,
          role: 'assistant',
          content: response.answer,
          timestamp: new Date().toISOString(),
        },
      ]);
    } catch (error: any) {
      setMessages((current) => [
        ...current,
        {
          id: `${Date.now()}-error`,
          role: 'assistant',
          content: error?.response?.data?.message
            || "I couldn't complete that request. Please try again or contact HR for time-sensitive guidance.",
          timestamp: new Date().toISOString(),
        },
      ]);
    } finally {
      setLoading(false);
      setTimeout(() => scrollRef.current?.scrollToEnd({ animated: true }), 120);
    }
  }, [loading]);

  const firstName = (user?.fullName ?? user?.name ?? 'there').split(' ')[0];

  return (
    <KeyboardAvoidingView
      style={[styles.root, { backgroundColor: theme.colors.canvas }]}
      behavior={Platform.OS === 'ios' ? 'padding' : undefined}
      keyboardVerticalOffset={Platform.OS === 'ios' ? 8 : 0}
    >
      <LiquidBackdrop subtle />
      <ScrollView
        ref={scrollRef}
        contentContainerStyle={styles.content}
        keyboardShouldPersistTaps="handled"
        showsVerticalScrollIndicator={false}
      >
        <ScreenHero
          eyebrow="Private workforce assistant"
          title="KynexOne AI"
          subtitle="Grounded in your own authorized HR data"
          actions={
            <GlassSurface elevated={false} radius={999} contentStyle={styles.onlineBadge}>
              <View style={[styles.onlineDot, { backgroundColor: theme.colors.success }]} />
              <Text style={[theme.typography.micro, { color: theme.colors.success }]}>Available</Text>
            </GlassSurface>
          }
        />

        {messages.length === 0 ? (
          <>
            <View style={styles.section}>
              <GlassSurface
                radius={theme.radius.xxl}
                tintColor={theme.isDark ? 'rgba(35,78,167,0.30)' : 'rgba(255,255,255,0.52)'}
                contentStyle={styles.welcomeCard}
              >
                <View style={[styles.aiMark, { backgroundColor: `${theme.colors.primary}1F` }]}>
                  <Ionicons name="sparkles" size={28} color={theme.colors.primary} />
                </View>
                <Text style={[theme.typography.h2, styles.welcomeTitle, { color: theme.colors.text }]}>
                  Hi {firstName}, what can I help with?
                </Text>
                <Text style={[theme.typography.body, styles.welcomeBody, { color: theme.colors.textSecondary }]}>
                  Ask about leave, attendance, payslips, documents, approvals and the workforce information your account is permitted to view.
                </Text>
                <View style={[styles.safetyNote, { backgroundColor: `${theme.colors.warning}12` }]}>
                  <Ionicons name="shield-checkmark-outline" size={18} color={theme.colors.warning} />
                  <Text style={[theme.typography.caption, { color: theme.colors.textSecondary, flex: 1 }]}>
                    Responses are advisory. Confirm contractual, payroll or legal decisions with your authorized HR team.
                  </Text>
                </View>
              </GlassSurface>
            </View>

            <View style={styles.section}>
              <SectionHeader
                title={manager ? 'Manager prompts' : 'Suggested questions'}
                subtitle="Start with a common workforce question"
              />
              <View style={styles.suggestionGrid}>
                {suggestions.map((suggestion) => (
                  <MotionPressable
                    key={suggestion.question}
                    onPress={() => void send(suggestion.question)}
                    haptic="selection"
                    style={styles.suggestionShell}
                    contentStyle={styles.rounded}
                    accessibilityRole="button"
                    accessibilityLabel={suggestion.question}
                  >
                    <GlassSurface
                      elevated={false}
                      radius={theme.radius.xl}
                      style={styles.suggestionSurface}
                      contentStyle={styles.suggestionCard}
                    >
                      <View style={[styles.suggestionIcon, { backgroundColor: `${theme.colors.primary}18` }]}>
                        <Ionicons name={suggestion.icon} size={21} color={theme.colors.primary} />
                      </View>
                      <Text style={[theme.typography.bodyStrong, styles.suggestionText, { color: theme.colors.text }]}>
                        {suggestion.question}
                      </Text>
                      <Ionicons name="arrow-up-outline" size={15} color={theme.colors.textMuted} style={styles.suggestionArrow} />
                    </GlassSurface>
                  </MotionPressable>
                ))}
              </View>
            </View>
          </>
        ) : (
          <View style={styles.section}>
            <View style={styles.chatList}>
              {messages.map((message) => <ChatBubble key={message.id} message={message} />)}
              {loading ? <TypingIndicator /> : null}
            </View>
          </View>
        )}
        <View style={styles.chatBottomSpacer} />
      </ScrollView>

      <View style={[styles.composerSafeArea, { backgroundColor: theme.colors.canvas }]}> 
        <GlassSurface
          radius={theme.radius.xxl}
          style={styles.composerSurface}
          contentStyle={styles.composer}
          tintColor={theme.isDark ? 'rgba(12,28,57,0.92)' : 'rgba(255,255,255,0.88)'}
        >
          <TextInput
            value={input}
            onChangeText={setInput}
            placeholder="Ask about HR, payroll or attendance…"
            placeholderTextColor={theme.colors.textMuted}
            selectionColor={theme.colors.primary}
            multiline
            maxLength={800}
            style={[theme.typography.body, styles.input, { color: theme.colors.text }]}
            returnKeyType="send"
            onSubmitEditing={() => void send(input)}
          />
          <MotionPressable
            onPress={() => void send(input)}
            disabled={loading || !input.trim()}
            haptic="medium"
            contentStyle={[
              styles.sendButton,
              {
                backgroundColor: input.trim() && !loading
                  ? theme.colors.primary
                  : theme.colors.surfaceMuted,
              },
            ]}
            accessibilityLabel="Send message"
          >
            {loading ? (
              <ActivityIndicator size="small" color="#FFFFFF" />
            ) : (
              <Ionicons
                name="arrow-up"
                size={21}
                color={input.trim() ? '#FFFFFF' : theme.colors.textMuted}
              />
            )}
          </MotionPressable>
        </GlassSurface>
      </View>
    </KeyboardAvoidingView>
  );
}

function ChatBubble({ message }: { message: AIMessage }) {
  const { theme } = useTheme();
  const userMessage = message.role === 'user';

  return (
    <View style={[styles.messageWrap, { alignItems: userMessage ? 'flex-end' : 'flex-start' }]}>
      {!userMessage ? (
        <View style={styles.assistantLabel}>
          <View style={[styles.assistantMark, { backgroundColor: `${theme.colors.primary}1F` }]}>
            <Ionicons name="sparkles" size={14} color={theme.colors.primary} />
          </View>
          <Text style={[theme.typography.micro, { color: theme.colors.textMuted }]}>KynexOne AI</Text>
        </View>
      ) : null}
      {userMessage ? (
        <View style={[styles.userBubble, { backgroundColor: theme.colors.primary }]}> 
          <Text style={[theme.typography.body, { color: '#FFFFFF' }]}>{message.content}</Text>
        </View>
      ) : (
        <GlassSurface
          elevated={false}
          radius={20}
          style={styles.assistantBubble}
          contentStyle={styles.assistantBubbleContent}
        >
          <Text style={[theme.typography.body, { color: theme.colors.text }]}>{message.content}</Text>
        </GlassSurface>
      )}
      <Text style={[theme.typography.micro, styles.timestamp, { color: theme.colors.textMuted }]}>
        {new Date(message.timestamp).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}
      </Text>
    </View>
  );
}

function TypingIndicator() {
  const { theme } = useTheme();
  return (
    <View style={styles.typingWrap}>
      <View style={styles.assistantLabel}>
        <View style={[styles.assistantMark, { backgroundColor: `${theme.colors.primary}1F` }]}>
          <Ionicons name="sparkles" size={14} color={theme.colors.primary} />
        </View>
        <Text style={[theme.typography.micro, { color: theme.colors.textMuted }]}>Thinking securely…</Text>
      </View>
      <GlassSurface elevated={false} radius={20} style={styles.typingBubble} contentStyle={styles.typingContent}>
        {[0, 1, 2].map((index) => (
          <View
            key={index}
            style={[
              styles.typingDot,
              { backgroundColor: theme.colors.primary, opacity: 0.45 + index * 0.2 },
            ]}
          />
        ))}
      </GlassSurface>
    </View>
  );
}

const styles = StyleSheet.create({
  root: { flex: 1 },
  content: { flexGrow: 1, paddingBottom: 12 },
  onlineBadge: { flexDirection: 'row', alignItems: 'center', gap: 5, paddingHorizontal: 9, paddingVertical: 7 },
  onlineDot: { width: 7, height: 7, borderRadius: 4 },
  section: { paddingHorizontal: 16, marginTop: 16 },
  welcomeCard: { alignItems: 'center', padding: 22 },
  aiMark: { width: 64, height: 64, borderRadius: 22, alignItems: 'center', justifyContent: 'center' },
  welcomeTitle: { textAlign: 'center', marginTop: 16 },
  welcomeBody: { textAlign: 'center', maxWidth: 330, marginTop: 8 },
  safetyNote: { flexDirection: 'row', alignItems: 'flex-start', gap: 9, borderRadius: 16, padding: 12, marginTop: 18 },
  suggestionGrid: { flexDirection: 'row', flexWrap: 'wrap', gap: 10 },
  suggestionShell: { width: '48%', minHeight: 150 },
  rounded: { flex: 1, borderRadius: 24 },
  suggestionSurface: { flex: 1 },
  suggestionCard: { flex: 1, padding: 15 },
  suggestionIcon: { width: 42, height: 42, borderRadius: 15, alignItems: 'center', justifyContent: 'center' },
  suggestionText: { marginTop: 13, paddingRight: 14 },
  suggestionArrow: { position: 'absolute', top: 14, right: 14, transform: [{ rotate: '45deg' }] },
  chatList: { gap: 11 },
  messageWrap: { width: '100%' },
  assistantLabel: { flexDirection: 'row', alignItems: 'center', gap: 6, marginBottom: 5 },
  assistantMark: { width: 25, height: 25, borderRadius: 9, alignItems: 'center', justifyContent: 'center' },
  userBubble: { maxWidth: '84%', borderRadius: 20, borderBottomRightRadius: 6, paddingHorizontal: 14, paddingVertical: 11 },
  assistantBubble: { maxWidth: '88%' },
  assistantBubbleContent: { paddingHorizontal: 14, paddingVertical: 12 },
  timestamp: { marginTop: 4, marginHorizontal: 4 },
  typingWrap: { alignItems: 'flex-start' },
  typingBubble: { width: 72, height: 46 },
  typingContent: { flexDirection: 'row', alignItems: 'center', justifyContent: 'center', gap: 5 },
  typingDot: { width: 7, height: 7, borderRadius: 4 },
  chatBottomSpacer: { height: 8 },
  composerSafeArea: { paddingHorizontal: 12, paddingTop: 8, paddingBottom: Platform.OS === 'ios' ? 12 : 8 },
  composerSurface: { minHeight: 62 },
  composer: { flexDirection: 'row', alignItems: 'flex-end', gap: 8, paddingLeft: 15, paddingRight: 8, paddingVertical: 8 },
  input: { flex: 1, minHeight: 44, maxHeight: 118, paddingTop: 11, paddingBottom: 10 },
  sendButton: { width: 46, height: 46, borderRadius: 17, alignItems: 'center', justifyContent: 'center' },
});
