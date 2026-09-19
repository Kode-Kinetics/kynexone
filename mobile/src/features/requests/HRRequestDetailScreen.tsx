import React, { useState, useEffect, useRef, useCallback } from 'react';
import {
  View, Text, ScrollView, TouchableOpacity, TextInput,
  ActivityIndicator, Alert, KeyboardAvoidingView, Platform,
} from 'react-native';
import { useRoute, useNavigation } from '@react-navigation/native';
import { hrRequestsApi } from '@/api/adapters';
import { HRRequest } from '@/types';
import { formatDate } from '@/utils/date';
import { useAuthStore } from '@/auth/authStore';
import { COLORS } from '@/config';

const STATUS_STEPS = ['Open', 'InProgress', 'Resolved', 'Closed'];

function Timeline({ status }: { status: string }) {
  const currentIdx = STATUS_STEPS.indexOf(status);
  return (
    <View style={{ flexDirection: 'row', alignItems: 'center', marginVertical: 16 }}>
      {STATUS_STEPS.map((step, i) => {
        const done = i <= currentIdx;
        const active = i === currentIdx;
        return (
          <React.Fragment key={step}>
            <View style={{ alignItems: 'center' }}>
              <View style={{
                width: 28, height: 28, borderRadius: 14,
                backgroundColor: done ? (active ? COLORS.blue : '#34D399') : '#E5E7EB',
                alignItems: 'center', justifyContent: 'center',
              }}>
                <Text style={{ color: done ? '#fff' : '#9CA3AF', fontSize: 12, fontWeight: '700' }}>
                  {done && !active ? '✓' : `${i + 1}`}
                </Text>
              </View>
              <Text style={{
                fontSize: 10, color: done ? (active ? COLORS.blue : '#374151') : '#9CA3AF',
                fontWeight: active ? '700' : '400', marginTop: 4, textAlign: 'center', width: 60,
              }}>
                {step}
              </Text>
            </View>
            {i < STATUS_STEPS.length - 1 && (
              <View style={{
                flex: 1, height: 2, marginHorizontal: 2, marginBottom: 16,
                backgroundColor: i < currentIdx ? '#34D399' : '#E5E7EB',
              }} />
            )}
          </React.Fragment>
        );
      })}
    </View>
  );
}

export default function HRRequestDetailScreen() {
  const route = useRoute<any>();
  const navigation = useNavigation();
  const { user } = useAuthStore();
  const { id } = route.params as { id: string };
  const scrollRef = useRef<ScrollView>(null);

  const [request, setRequest] = useState<HRRequest | null>(null);
  const [loading, setLoading] = useState(true);
  const [comment, setComment] = useState('');
  const [sending, setSending] = useState(false);

  const load = useCallback(async () => {
    try {
      const data = await hrRequestsApi.getDetail(id);
      setRequest(data);
    } catch (e: any) {
      Alert.alert('Error', e.message || 'Failed to load request');
      navigation.goBack();
    } finally {
      setLoading(false);
    }
  }, [id, navigation]);

  useEffect(() => {
    void load();
  }, [load]);

  const sendComment = async () => {
    if (!comment.trim()) return;
    setSending(true);
    try {
      await hrRequestsApi.addComment(id, comment.trim());
      setComment('');
      await load();
      setTimeout(() => scrollRef.current?.scrollToEnd({ animated: true }), 200);
    } catch (e: any) {
      Alert.alert('Error', e.message || 'Failed to send comment');
    } finally {
      setSending(false);
    }
  };

  if (loading) {
    return (
      <View style={{ flex: 1, alignItems: 'center', justifyContent: 'center', backgroundColor: COLORS.background }}>
        <ActivityIndicator color={COLORS.blue} size="large" />
      </View>
    );
  }

  if (!request) return null;

  return (
    <KeyboardAvoidingView
      style={{ flex: 1, backgroundColor: COLORS.background }}
      behavior={Platform.OS === 'ios' ? 'padding' : undefined}
      keyboardVerticalOffset={Platform.OS === 'ios' ? 90 : 0}
    >
      {/* Header */}
      <View style={{ backgroundColor: COLORS.navy, paddingTop: 56, paddingBottom: 16, paddingHorizontal: 20 }}>
        <TouchableOpacity onPress={() => navigation.goBack()} style={{ marginBottom: 10 }}>
          <Text style={{ color: 'rgba(255,255,255,0.7)', fontSize: 14 }}>← Back</Text>
        </TouchableOpacity>
        <Text style={{ color: '#fff', fontSize: 18, fontWeight: '700' }} numberOfLines={2}>{request.subject}</Text>
        {request.ticketNumber && (
          <Text style={{ color: 'rgba(255,255,255,0.6)', fontSize: 12, marginTop: 2 }}>#{request.ticketNumber}</Text>
        )}
      </View>

      <ScrollView ref={scrollRef} contentContainerStyle={{ padding: 16, paddingBottom: 24 }}>
        {/* Status timeline */}
        <View style={{ backgroundColor: '#fff', borderRadius: 14, padding: 16, marginBottom: 14 }}>
          <Text style={{ fontSize: 14, fontWeight: '600', color: '#374151', marginBottom: 4 }}>Status</Text>
          <Timeline status={request.status} />
        </View>

        {/* Details */}
        <View style={{
          backgroundColor: '#fff', borderRadius: 14, padding: 16, marginBottom: 14,
          shadowColor: '#000', shadowOffset: { width: 0, height: 1 }, shadowOpacity: 0.06, shadowRadius: 4, elevation: 2,
        }}>
          {[
            { label: 'Type', value: request.requestType },
            { label: 'Submitted', value: formatDate(request.createdAt, 'dateTime') },
            { label: 'Assigned To', value: request.assignedTo },
            { label: 'Response', value: request.responseStatus ?? request.slaStatus },
          ].filter((r) => r.value).map((row) => (
            <View key={row.label} style={{ flexDirection: 'row', justifyContent: 'space-between', paddingVertical: 8, borderBottomWidth: 1, borderBottomColor: '#F3F4F6' }}>
              <Text style={{ fontSize: 13, color: '#6B7280' }}>{row.label}</Text>
              <Text style={{ fontSize: 13, fontWeight: '500', color: '#111827', flex: 1, textAlign: 'right' }}>{row.value}</Text>
            </View>
          ))}
        </View>

        {/* Description */}
        <View style={{ backgroundColor: '#fff', borderRadius: 14, padding: 16, marginBottom: 14 }}>
          <Text style={{ fontSize: 13, fontWeight: '600', color: '#374151', marginBottom: 6 }}>Description</Text>
          <Text style={{ fontSize: 14, color: '#374151', lineHeight: 20 }}>{request.description}</Text>
        </View>

        {/* Comment thread */}
        {request.comments && request.comments.length > 0 && (
          <View style={{ marginBottom: 14 }}>
            <Text style={{ fontSize: 13, fontWeight: '600', color: '#6B7280', marginBottom: 10 }}>COMMENTS</Text>
            {request.comments.map((c) => {
              const isMe = c.authorId === user?.employeeId;
              return (
                <View key={c.id} style={{
                  alignSelf: isMe ? 'flex-end' : 'flex-start',
                  maxWidth: '80%', marginBottom: 10,
                }}>
                  <View style={{
                    backgroundColor: isMe ? COLORS.blue : '#fff',
                    borderRadius: 14,
                    borderBottomRightRadius: isMe ? 4 : 14,
                    borderBottomLeftRadius: isMe ? 14 : 4,
                    padding: 12,
                    shadowColor: '#000', shadowOffset: { width: 0, height: 1 }, shadowOpacity: 0.06, shadowRadius: 4, elevation: 2,
                  }}>
                    {!isMe && (
                      <Text style={{ fontSize: 11, fontWeight: '700', color: COLORS.blue, marginBottom: 4 }}>
                        {c.authorName}
                      </Text>
                    )}
                    <Text style={{ fontSize: 14, color: isMe ? '#fff' : '#374151', lineHeight: 20 }}>
                      {c.content ?? c.message}
                    </Text>
                  </View>
                  <Text style={{
                    fontSize: 11, color: '#9CA3AF', marginTop: 3,
                    alignSelf: isMe ? 'flex-end' : 'flex-start',
                  }}>
                    {formatDate(c.createdAt, 'time')}
                  </Text>
                </View>
              );
            })}
          </View>
        )}
      </ScrollView>

      {/* Comment input */}
      {request.status !== 'Closed' && request.status !== 'Resolved' && request.status !== 'Cancelled' && (
        <View style={{
          flexDirection: 'row', alignItems: 'flex-end', padding: 12, gap: 10,
          backgroundColor: '#fff', borderTopWidth: 1, borderTopColor: '#E5E7EB',
        }}>
          <TextInput
            value={comment}
            onChangeText={setComment}
            placeholder="Add a comment..."
            multiline
            style={{
              flex: 1, borderWidth: 1, borderColor: '#E5E7EB', borderRadius: 20,
              paddingHorizontal: 14, paddingVertical: 10, fontSize: 14, maxHeight: 100,
            }}
          />
          <TouchableOpacity
            onPress={sendComment}
            disabled={sending || !comment.trim()}
            style={{
              width: 40, height: 40, borderRadius: 20,
              backgroundColor: comment.trim() ? COLORS.blue : '#E5E7EB',
              alignItems: 'center', justifyContent: 'center',
            }}
          >
            {sending ? (
              <ActivityIndicator color="#fff" size="small" />
            ) : (
              <Text style={{ color: '#fff', fontSize: 16 }}>➤</Text>
            )}
          </TouchableOpacity>
        </View>
      )}
    </KeyboardAvoidingView>
  );
}
