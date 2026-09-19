import React, { useState, useEffect, useCallback } from 'react';
import {
  View, Text, ScrollView, TouchableOpacity,
  ActivityIndicator, Alert, RefreshControl, TextInput, Modal,
} from 'react-native';
import { useNavigation } from '@react-navigation/native';
import * as DocumentPicker from 'expo-document-picker';
import { documentsApi, hrRequestsApi } from '@/api/adapters';
import { normalizePickedFile, type PickedFile } from '@/api/services';
import { HRRequest } from '@/types';
import { formatDate } from '@/utils/date';
import { COLORS } from '@/config';
import { FEATURES } from '@/config/features';

const REQUEST_TYPES = [
  { key: 'SalaryCertificate', label: 'Salary Certificate', emoji: '📄' },
  { key: 'ExperienceLetter', label: 'Experience Letter', emoji: '📋' },
  { key: 'NOC', label: 'NOC / No Objection', emoji: '✅' },
  { key: 'Complaint', label: 'Complaint / Grievance', emoji: '⚠️' },
  { key: 'General', label: 'General HR Ticket', emoji: '📝' },
  { key: 'BankLetter', label: 'Bank Letter', emoji: '🏦' },
  { key: 'LeaveEncashment', label: 'Leave Encashment', emoji: '💰' },
  { key: 'DocumentRequest', label: 'Document Request', emoji: '📁' },
];

function SLABadge({ slaStatus }: { slaStatus?: string }) {
  if (!slaStatus) return null;
  const colors: Record<string, { bg: string; text: string }> = {
    OnTime:    { bg: '#F0FDF4', text: '#15803D' },
    AtRisk:    { bg: '#FFF7ED', text: '#C2410C' },
    Breached:  { bg: '#FEF2F2', text: '#DC2626' },
  };
  const s = colors[slaStatus] ?? colors['OnTime'];
  return (
    <View style={{ backgroundColor: s.bg, borderRadius: 6, paddingHorizontal: 7, paddingVertical: 2 }}>
      <Text style={{ color: s.text, fontSize: 10, fontWeight: '700' }}>{slaStatus}</Text>
    </View>
  );
}

function StatusChip({ status }: { status: string }) {
  const map: Record<string, { bg: string; text: string }> = {
    Open:        { bg: '#EFF6FF', text: '#2563EB' },
    InProgress:  { bg: '#FFF7ED', text: '#C2410C' },
    Resolved:    { bg: '#F0FDF4', text: '#15803D' },
    Closed:      { bg: '#F3F4F6', text: '#6B7280' },
    Cancelled:   { bg: '#F3F4F6', text: '#9CA3AF' },
  };
  const s = map[status] ?? map['Open'];
  return (
    <View style={{ backgroundColor: s.bg, borderRadius: 6, paddingHorizontal: 7, paddingVertical: 2 }}>
      <Text style={{ color: s.text, fontSize: 11, fontWeight: '600' }}>{status}</Text>
    </View>
  );
}

export default function HRRequestsScreen() {
  const navigation = useNavigation<any>();
  const [requests, setRequests] = useState<HRRequest[]>([]);
  const [loading, setLoading] = useState(true);
  const [refreshing, setRefreshing] = useState(false);
  const [createModal, setCreateModal] = useState(false);
  const [submitting, setSubmitting] = useState(false);
  const [form, setForm] = useState({
    requestType: REQUEST_TYPES[0].key,
    subject: '',
    description: '',
  });
  const [attachment, setAttachment] = useState<PickedFile | null>(null);
  const [typePickerOpen, setTypePickerOpen] = useState(false);

  const fetchRequests = useCallback(async () => {
    try {
      const data = await hrRequestsApi.getMy({ page: 1, limit: 50 });
      setRequests(data.items || []);
    } catch (e: any) {
      Alert.alert('Error', e.message || 'Failed to load requests');
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => { fetchRequests(); }, [fetchRequests]);

  const onRefresh = async () => {
    setRefreshing(true);
    await fetchRequests();
    setRefreshing(false);
  };

  const pickFile = async () => {
    try {
      const result = await DocumentPicker.getDocumentAsync({ copyToCacheDirectory: true });
      if (!result.canceled && result.assets?.[0]) {
        const asset = result.assets[0];
        setAttachment(normalizePickedFile({
          uri: asset.uri,
          name: asset.name,
          mimeType: asset.mimeType,
          size: asset.size,
        }));
      }
    } catch {
      Alert.alert('Error', 'Failed to pick file');
    }
  };

  const submitRequest = async () => {
    if (!form.subject.trim()) return Alert.alert('Missing subject', 'Please enter a subject');
    if (!form.description.trim()) return Alert.alert('Missing description', 'Please describe your request');
    setSubmitting(true);
    try {
      const uploaded = attachment
        ? await documentsApi.uploadDocument({
            file: attachment,
            documentType: 'HR Request Attachment',
          })
        : null;
      await hrRequestsApi.create({
        // Stored as the ticket's categoryName, which HR reads on the web — send the label.
        requestType: REQUEST_TYPES.find((t) => t.key === form.requestType)?.label ?? form.requestType,
        subject: form.subject,
        description: form.description,
        attachmentDocumentId: uploaded?.id,
      });
      setCreateModal(false);
      setForm({ requestType: REQUEST_TYPES[0].key, subject: '', description: '' });
      setAttachment(null);
      Alert.alert('Submitted', 'Your HR request has been submitted');
      fetchRequests();
    } catch (e: any) {
      Alert.alert('Error', e.message || 'Failed to submit request');
    } finally {
      setSubmitting(false);
    }
  };

  const selectedType = REQUEST_TYPES.find((t) => t.key === form.requestType) ?? REQUEST_TYPES[0];

  return (
    <View style={{ flex: 1, backgroundColor: COLORS.background }}>
      {/* Header */}
      <View style={{ backgroundColor: COLORS.navy, paddingTop: 56, paddingBottom: 16, paddingHorizontal: 20 }}>
        <View style={{ flexDirection: 'row', justifyContent: 'space-between', alignItems: 'flex-end' }}>
          <View>
            <Text style={{ color: '#fff', fontSize: 22, fontWeight: '700' }}>HR Requests</Text>
            <Text style={{ color: 'rgba(255,255,255,0.6)', fontSize: 13, marginTop: 2 }}>
              Certificates, letters & support
            </Text>
          </View>
          <TouchableOpacity
            onPress={() => setCreateModal(true)}
            style={{ backgroundColor: COLORS.blue, borderRadius: 10, paddingHorizontal: 14, paddingVertical: 8 }}
          >
            <Text style={{ color: '#fff', fontWeight: '700', fontSize: 13 }}>+ New</Text>
          </TouchableOpacity>
        </View>
      </View>

      {loading ? (
        <ActivityIndicator color={COLORS.blue} style={{ marginTop: 60 }} />
      ) : (
        <ScrollView
          refreshControl={<RefreshControl refreshing={refreshing} onRefresh={onRefresh} />}
          contentContainerStyle={{ padding: 16, paddingBottom: 40 }}
        >
          {/* Quick type buttons */}
          <ScrollView horizontal showsHorizontalScrollIndicator={false} style={{ marginBottom: 16 }}>
            {REQUEST_TYPES.slice(0, 5).map((rt) => (
              <TouchableOpacity
                key={rt.key}
                onPress={() => { setForm((prev) => ({ ...prev, requestType: rt.key })); setCreateModal(true); }}
                style={{
                  backgroundColor: '#fff', borderRadius: 12, padding: 12, marginRight: 10,
                  alignItems: 'center', minWidth: 80,
                  shadowColor: '#000', shadowOffset: { width: 0, height: 1 }, shadowOpacity: 0.06, shadowRadius: 4, elevation: 2,
                }}
              >
                <Text style={{ fontSize: 24 }}>{rt.emoji}</Text>
                <Text style={{ fontSize: 11, color: '#374151', marginTop: 4, textAlign: 'center', fontWeight: '500' }}>
                  {rt.label}
                </Text>
              </TouchableOpacity>
            ))}
          </ScrollView>

          {requests.length === 0 ? (
            <View style={{ alignItems: 'center', marginTop: 40 }}>
              <Text style={{ fontSize: 40 }}>🎫</Text>
              <Text style={{ color: '#374151', fontSize: 16, fontWeight: '600', marginTop: 12 }}>No requests yet</Text>
              <Text style={{ color: '#9CA3AF', fontSize: 13, marginTop: 4, textAlign: 'center' }}>
                Submit a salary certificate, NOC or any HR support request
              </Text>
              <TouchableOpacity
                onPress={() => setCreateModal(true)}
                style={{ backgroundColor: COLORS.blue, borderRadius: 10, paddingHorizontal: 20, paddingVertical: 10, marginTop: 16 }}
              >
                <Text style={{ color: '#fff', fontWeight: '700' }}>Create Request</Text>
              </TouchableOpacity>
            </View>
          ) : (
            <>
              <Text style={{ fontSize: 13, fontWeight: '600', color: '#6B7280', marginBottom: 12 }}>
                MY REQUESTS ({requests.length})
              </Text>
              {requests.map((req) => (
                <TouchableOpacity
                  key={req.id}
                  onPress={() => navigation.navigate('HRRequestDetail', { id: req.id })}
                  style={{
                    backgroundColor: '#fff', borderRadius: 14, padding: 16, marginBottom: 12,
                    shadowColor: '#000', shadowOffset: { width: 0, height: 1 }, shadowOpacity: 0.06, shadowRadius: 4, elevation: 2,
                  }}
                >
                  <View style={{ flexDirection: 'row', justifyContent: 'space-between', alignItems: 'flex-start' }}>
                    <View style={{ flex: 1, marginRight: 12 }}>
                      <Text style={{ fontSize: 15, fontWeight: '700', color: '#111827' }} numberOfLines={1}>
                        {req.subject}
                      </Text>
                      <Text style={{ fontSize: 12, color: '#9CA3AF', marginTop: 2 }}>
                        {REQUEST_TYPES.find((t) => t.key === req.requestType)?.label ?? req.requestType}
                        {req.ticketNumber ? ` · #${req.ticketNumber}` : ''}
                      </Text>
                    </View>
                    <View style={{ gap: 4, alignItems: 'flex-end' }}>
                      <StatusChip status={req.status} />
                      <SLABadge slaStatus={req.slaStatus} />
                    </View>
                  </View>
                  <View style={{ flexDirection: 'row', justifyContent: 'space-between', marginTop: 10 }}>
                    <Text style={{ fontSize: 12, color: '#9CA3AF' }}>
                      {formatDate(req.createdAt, 'display')}
                    </Text>
                    {req.commentsCount !== undefined && req.commentsCount > 0 && (
                      <Text style={{ fontSize: 12, color: '#6B7280' }}>💬 {req.commentsCount}</Text>
                    )}
                  </View>
                </TouchableOpacity>
              ))}
            </>
          )}
        </ScrollView>
      )}

      {/* Create modal */}
      <Modal visible={createModal} animationType="slide" transparent>
        <View style={{ flex: 1, backgroundColor: 'rgba(0,0,0,0.5)', justifyContent: 'flex-end' }}>
          <ScrollView
            style={{ backgroundColor: '#fff', borderTopLeftRadius: 24, borderTopRightRadius: 24, maxHeight: '90%' }}
            contentContainerStyle={{ padding: 24, paddingBottom: 40 }}
            keyboardShouldPersistTaps="handled"
          >
            <Text style={{ fontSize: 18, fontWeight: '700', color: '#111827', marginBottom: 20 }}>New HR Request</Text>

            {/* Type */}
            <Text style={{ fontSize: 13, fontWeight: '600', color: '#374151', marginBottom: 6 }}>Request Type *</Text>
            <TouchableOpacity
              onPress={() => setTypePickerOpen(!typePickerOpen)}
              style={{
                borderWidth: 1, borderColor: '#D1D5DB', borderRadius: 10,
                paddingHorizontal: 14, paddingVertical: 12, marginBottom: 4,
                flexDirection: 'row', alignItems: 'center', gap: 8,
              }}
            >
              <Text style={{ fontSize: 16 }}>{selectedType.emoji}</Text>
              <Text style={{ flex: 1, fontSize: 15, color: '#111827' }}>{selectedType.label}</Text>
              <Text style={{ color: '#9CA3AF' }}>▼</Text>
            </TouchableOpacity>
            {typePickerOpen && (
              <View style={{
                borderWidth: 1, borderColor: '#D1D5DB', borderRadius: 10, marginBottom: 12,
              }}>
                {REQUEST_TYPES.map((rt) => (
                  <TouchableOpacity
                    key={rt.key}
                    onPress={() => { setForm((p) => ({ ...p, requestType: rt.key })); setTypePickerOpen(false); }}
                    style={{
                      flexDirection: 'row', alignItems: 'center', gap: 10,
                      paddingHorizontal: 14, paddingVertical: 12,
                      backgroundColor: form.requestType === rt.key ? '#EFF6FF' : '#fff',
                      borderBottomWidth: 1, borderBottomColor: '#F3F4F6',
                    }}
                  >
                    <Text style={{ fontSize: 16 }}>{rt.emoji}</Text>
                    <Text style={{ color: form.requestType === rt.key ? COLORS.blue : '#374151' }}>{rt.label}</Text>
                  </TouchableOpacity>
                ))}
              </View>
            )}

            {/* Subject */}
            <Text style={{ fontSize: 13, fontWeight: '600', color: '#374151', marginTop: 10, marginBottom: 6 }}>Subject *</Text>
            <TextInput
              value={form.subject}
              onChangeText={(v) => setForm((p) => ({ ...p, subject: v }))}
              placeholder="Brief summary of your request"
              style={{
                borderWidth: 1, borderColor: '#D1D5DB', borderRadius: 10,
                paddingHorizontal: 14, paddingVertical: 12, fontSize: 15, marginBottom: 14,
              }}
            />

            {/* Description */}
            <Text style={{ fontSize: 13, fontWeight: '600', color: '#374151', marginBottom: 6 }}>Description *</Text>
            <TextInput
              value={form.description}
              onChangeText={(v) => setForm((p) => ({ ...p, description: v }))}
              placeholder="Provide details about your request..."
              multiline
              numberOfLines={5}
              textAlignVertical="top"
              style={{
                borderWidth: 1, borderColor: '#D1D5DB', borderRadius: 10,
                paddingHorizontal: 14, paddingVertical: 12, fontSize: 15, minHeight: 120, marginBottom: 14,
              }}
            />

            {/* Attachment — hidden until the storage upload flow exists (FEATURES.FILE_UPLOAD);
                ESSHRRequestCreateDto has no attachment field today. */}
            {FEATURES.FILE_UPLOAD && (
            <TouchableOpacity
              onPress={pickFile}
              style={{
                borderWidth: 1, borderColor: attachment ? COLORS.blue : '#D1D5DB',
                borderStyle: 'dashed', borderRadius: 10, padding: 12,
                alignItems: 'center', marginBottom: 20,
              }}
            >
              <Text style={{ color: attachment ? COLORS.blue : '#9CA3AF', fontSize: 13 }}>
                {attachment ? `📎 ${attachment.name}` : '📎 Attach document (optional)'}
              </Text>
            </TouchableOpacity>
            )}

            {/* Actions */}
            <View style={{ flexDirection: 'row', gap: 12 }}>
              <TouchableOpacity
                onPress={() => setCreateModal(false)}
                style={{ flex: 1, borderWidth: 1, borderColor: '#D1D5DB', borderRadius: 12, padding: 14, alignItems: 'center' }}
              >
                <Text style={{ color: '#374151', fontWeight: '600' }}>Cancel</Text>
              </TouchableOpacity>
              <TouchableOpacity
                onPress={submitRequest}
                disabled={submitting}
                style={{ flex: 1, backgroundColor: submitting ? '#93C5FD' : COLORS.blue, borderRadius: 12, padding: 14, alignItems: 'center' }}
              >
                {submitting ? <ActivityIndicator color="#fff" /> : (
                  <Text style={{ color: '#fff', fontWeight: '700' }}>Submit</Text>
                )}
              </TouchableOpacity>
            </View>
          </ScrollView>
        </View>
      </Modal>
    </View>
  );
}
