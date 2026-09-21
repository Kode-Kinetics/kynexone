import React, { useState, useRef, useCallback } from 'react';
import {
  View, Text, ScrollView, TouchableOpacity, TextInput,
  ActivityIndicator, KeyboardAvoidingView, Platform,
} from 'react-native';
import { aiApi } from '@/api/services';
import { AIMessage } from '@/types';
import { useAuthStore } from '@/auth/authStore';
import { COLORS } from '@/config';

const EMPLOYEE_SUGGESTIONS = [
  'How many leave days do I have?',
  'What is my attendance this month?',
  'When was my last payslip?',
  'Which documents are expiring soon?',
  'Where is my HR request?',
  'What is the leave policy?',
];

const MANAGER_SUGGESTIONS = [
  'Who is absent today in my team?',
  'Which approvals are pending?',
  'Show overtime trend for my team',
  'Which employees have frequent late attendance?',
  'Summarize this month\'s team attendance',
];

function ChatBubble({ message }: { message: AIMessage }) {
  const isUser = message.role === 'user';
  return (
    <View style={{
      alignSelf: isUser ? 'flex-end' : 'flex-start',
      maxWidth: '85%', marginBottom: 12,
    }}>
      {!isUser && (
        <View style={{ flexDirection: 'row', alignItems: 'center', gap: 6, marginBottom: 4 }}>
          <View style={{
            width: 22, height: 22, borderRadius: 11,
            backgroundColor: COLORS.blue, alignItems: 'center', justifyContent: 'center',
          }}>
            <Text style={{ fontSize: 10, color: '#fff', fontWeight: '700' }}>Z</Text>
          </View>
          <Text style={{ fontSize: 11, color: '#6B7280', fontWeight: '600' }}>KynexOne AI</Text>
        </View>
      )}
      <View style={{
        padding: 12,
        backgroundColor: isUser ? COLORS.blue : '#fff',
        borderRadius: 16,
        borderBottomRightRadius: isUser ? 4 : 16,
        borderBottomLeftRadius: isUser ? 16 : 4,
        shadowColor: '#000', shadowOffset: { width: 0, height: 1 }, shadowOpacity: 0.06, shadowRadius: 4, elevation: 2,
      }}>
        <Text style={{ fontSize: 14, color: isUser ? '#fff' : '#111827', lineHeight: 20 }}>
          {message.content}
        </Text>
      </View>
      <Text style={{ fontSize: 10, color: '#9CA3AF', marginTop: 2, alignSelf: isUser ? 'flex-end' : 'flex-start' }}>
        {new Date(message.timestamp).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })}
      </Text>
    </View>
  );
}

function TypingIndicator() {
  return (
    <View style={{ alignSelf: 'flex-start', marginBottom: 12 }}>
      <View style={{ flexDirection: 'row', alignItems: 'center', gap: 6, marginBottom: 4 }}>
        <View style={{
          width: 22, height: 22, borderRadius: 11,
          backgroundColor: COLORS.blue, alignItems: 'center', justifyContent: 'center',
        }}>
          <Text style={{ fontSize: 10, color: '#fff', fontWeight: '700' }}>Z</Text>
        </View>
        <Text style={{ fontSize: 11, color: '#6B7280', fontWeight: '600' }}>KynexOne AI</Text>
      </View>
      <View style={{
        backgroundColor: '#fff', borderRadius: 16, borderBottomLeftRadius: 4,
        padding: 14, flexDirection: 'row', gap: 4, alignItems: 'center',
        shadowColor: '#000', shadowOffset: { width: 0, height: 1 }, shadowOpacity: 0.06, shadowRadius: 4, elevation: 2,
      }}>
        {[0, 1, 2].map((i) => (
          <View key={i} style={{ width: 6, height: 6, borderRadius: 3, backgroundColor: COLORS.blue, opacity: 0.5 + i * 0.25 }} />
        ))}
      </View>
    </View>
  );
}

export default function AIAssistantScreen() {
  const { user } = useAuthStore();
  const [messages, setMessages] = useState<AIMessage[]>([]);
  const [input, setInput] = useState('');
  const [loading, setLoading] = useState(false);
  const scrollRef = useRef<ScrollView>(null);

  const isManager = ['MANAGER', 'SUPERVISOR', 'HR', 'SUPER_ADMIN'].includes(user?.role ?? '');
  const suggestions = isManager ? MANAGER_SUGGESTIONS : EMPLOYEE_SUGGESTIONS;

  const send = useCallback(async (text: string) => {
    if (!text.trim() || loading) return;
    const userMsg: AIMessage = {
      id: Date.now().toString(),
      role: 'user',
      content: text.trim(),
      timestamp: new Date().toISOString(),
    };
    setMessages((prev) => [...prev, userMsg]);
    setInput('');
    setLoading(true);
    setTimeout(() => scrollRef.current?.scrollToEnd({ animated: true }), 100);

    try {
      // Backend DTO is ESSAIQuestionDto(string Question) — single-turn, no history.
      const data = await aiApi.ask({ question: text.trim() });
      const aiMsg: AIMessage = {
        id: (Date.now() + 1).toString(),
        role: 'assistant',
        content: data.answer,
        timestamp: new Date().toISOString(),
      };
      setMessages((prev) => [...prev, aiMsg]);
    } catch (e: any) {
      const errMsg: AIMessage = {
        id: (Date.now() + 1).toString(),
        role: 'assistant',
        content: e?.response?.data?.message || "I'm sorry, I couldn't process your request. Please try again.",
        timestamp: new Date().toISOString(),
      };
      setMessages((prev) => [...prev, errMsg]);
    } finally {
      setLoading(false);
      setTimeout(() => scrollRef.current?.scrollToEnd({ animated: true }), 100);
    }
  }, [loading]);

  const isEmpty = messages.length === 0;

  return (
    <KeyboardAvoidingView
      style={{ flex: 1, backgroundColor: COLORS.background }}
      behavior={Platform.OS === 'ios' ? 'padding' : undefined}
      keyboardVerticalOffset={Platform.OS === 'ios' ? 90 : 0}
    >
      {/* Header */}
      <View style={{ backgroundColor: COLORS.navy, paddingTop: 56, paddingBottom: 16, paddingHorizontal: 20 }}>
        <View style={{ flexDirection: 'row', alignItems: 'center', gap: 12 }}>
          <View style={{
            width: 42, height: 42, borderRadius: 21,
            backgroundColor: COLORS.blue, alignItems: 'center', justifyContent: 'center',
          }}>
            <Text style={{ fontSize: 20 }}>🤖</Text>
          </View>
          <View>
            <Text style={{ color: '#fff', fontSize: 18, fontWeight: '700' }}>KynexOne AI Assistant</Text>
            <View style={{ flexDirection: 'row', alignItems: 'center', gap: 5, marginTop: 2 }}>
              <View style={{ width: 6, height: 6, borderRadius: 3, backgroundColor: COLORS.emerald }} />
              <Text style={{ color: 'rgba(255,255,255,0.6)', fontSize: 11 }}>Always available</Text>
            </View>
          </View>
        </View>
      </View>

      {/* Chat area */}
      <ScrollView
        ref={scrollRef}
        contentContainerStyle={{ padding: 16, paddingBottom: 16, flexGrow: 1 }}
        keyboardShouldPersistTaps="handled"
      >
        {/* Welcome / suggestions */}
        {isEmpty && (
          <View style={{ flex: 1 }}>
            <View style={{
              backgroundColor: '#fff', borderRadius: 16, padding: 20, marginBottom: 20,
              shadowColor: '#000', shadowOffset: { width: 0, height: 1 }, shadowOpacity: 0.06, shadowRadius: 4, elevation: 2,
            }}>
              <Text style={{ fontSize: 16, fontWeight: '700', color: '#111827', marginBottom: 6 }}>
                Hi {user?.fullName?.split(' ')[0] ?? 'there'}! 👋
              </Text>
              <Text style={{ fontSize: 14, color: '#6B7280', lineHeight: 20 }}>
                I'm your AI HR assistant. I can answer questions about your attendance, leave, payslips, documents and more.
              </Text>
              <View style={{
                backgroundColor: '#FFF7ED', borderRadius: 10, padding: 10, marginTop: 12,
                flexDirection: 'row', gap: 6,
              }}>
                <Text style={{ fontSize: 13 }}>⚠️</Text>
                <Text style={{ fontSize: 12, color: '#92400E', flex: 1 }}>
                  AI responses are advisory only. Please verify important information through official HR channels.
                </Text>
              </View>
            </View>

            <Text style={{ fontSize: 12, fontWeight: '700', color: '#6B7280', marginBottom: 10, textTransform: 'uppercase' }}>
              Suggested Questions
            </Text>
            {suggestions.map((s) => (
              <TouchableOpacity
                key={s}
                onPress={() => send(s)}
                style={{
                  backgroundColor: '#fff', borderRadius: 12, padding: 14, marginBottom: 8,
                  flexDirection: 'row', alignItems: 'center', justifyContent: 'space-between',
                  shadowColor: '#000', shadowOffset: { width: 0, height: 1 }, shadowOpacity: 0.04, shadowRadius: 3, elevation: 1,
                  borderWidth: 1, borderColor: '#E5E7EB',
                }}
              >
                <Text style={{ fontSize: 14, color: '#374151', flex: 1 }}>{s}</Text>
                <Text style={{ color: COLORS.blue, fontSize: 16 }}>›</Text>
              </TouchableOpacity>
            ))}
          </View>
        )}

        {/* Messages */}
        {messages.map((msg) => (
          <ChatBubble key={msg.id} message={msg} />
        ))}
        {loading && <TypingIndicator />}
      </ScrollView>

      {/* Input area */}
      <View style={{
        flexDirection: 'row', alignItems: 'flex-end', padding: 12, gap: 10,
        backgroundColor: '#fff', borderTopWidth: 1, borderTopColor: '#E5E7EB',
      }}>
        <TextInput
          value={input}
          onChangeText={setInput}
          placeholder="Ask anything about HR, payroll, attendance..."
          multiline
          returnKeyType="send"
          onSubmitEditing={() => send(input)}
          style={{
            flex: 1, borderWidth: 1, borderColor: '#E5E7EB', borderRadius: 22,
            paddingHorizontal: 16, paddingVertical: 10, fontSize: 14, maxHeight: 100,
            backgroundColor: '#FAFAFA',
          }}
        />
        <TouchableOpacity
          onPress={() => send(input)}
          disabled={loading || !input.trim()}
          style={{
            width: 44, height: 44, borderRadius: 22,
            backgroundColor: input.trim() && !loading ? COLORS.blue : '#E5E7EB',
            alignItems: 'center', justifyContent: 'center',
          }}
        >
          {loading ? (
            <ActivityIndicator color="#fff" size="small" />
          ) : (
            <Text style={{ fontSize: 18, color: '#fff' }}>➤</Text>
          )}
        </TouchableOpacity>
      </View>
    </KeyboardAvoidingView>
  );
}
