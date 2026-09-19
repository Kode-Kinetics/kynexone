import React, { useState, useEffect, useCallback } from 'react';
import {
  View, Text, ScrollView, TouchableOpacity,
  ActivityIndicator, Alert, RefreshControl, Modal, TextInput,
} from 'react-native';
import * as DocumentPicker from 'expo-document-picker';
import * as FileSystem from 'expo-file-system/legacy';
import * as Sharing from 'expo-sharing';
import { documentsApi } from '@/api/adapters';
import { normalizePickedFile, type PickedFile } from '@/api/services';
import { EmployeeDocument } from '@/types';
import { formatDate } from '@/utils/date';
import { COLORS } from '@/config';
import { FEATURES } from '@/config/features';

const DOC_TYPES = [
  'Passport', 'Visa', 'Iqama', 'Emirates ID', 'Work Permit',
  'Educational Certificate', 'Professional Certificate', 'Medical Certificate',
  'Contract', 'Other',
];

function daysUntilExpiry(date?: string | null): number | null {
  if (!date) return null;
  return Math.ceil((new Date(date).getTime() - Date.now()) / 86400000);
}

function ExpiryChip({ date }: { date?: string | null }) {
  const days = daysUntilExpiry(date);
  if (days === null) return null;
  if (days < 0) return (
    <View style={{ backgroundColor: '#FEF2F2', borderRadius: 6, paddingHorizontal: 7, paddingVertical: 2 }}>
      <Text style={{ color: '#DC2626', fontSize: 11, fontWeight: '600' }}>EXPIRED</Text>
    </View>
  );
  if (days <= 30) return (
    <View style={{ backgroundColor: '#FEF2F2', borderRadius: 6, paddingHorizontal: 7, paddingVertical: 2 }}>
      <Text style={{ color: '#DC2626', fontSize: 11, fontWeight: '600' }}>{days}d left</Text>
    </View>
  );
  if (days <= 60) return (
    <View style={{ backgroundColor: '#FFF7ED', borderRadius: 6, paddingHorizontal: 7, paddingVertical: 2 }}>
      <Text style={{ color: '#C2410C', fontSize: 11, fontWeight: '600' }}>{days}d left</Text>
    </View>
  );
  return (
    <View style={{ backgroundColor: '#F0FDF4', borderRadius: 6, paddingHorizontal: 7, paddingVertical: 2 }}>
      <Text style={{ color: '#15803D', fontSize: 11, fontWeight: '600' }}>Valid</Text>
    </View>
  );
}

function StatusChip({ status }: { status: string }) {
  const map: Record<string, { bg: string; text: string }> = {
    Verified:  { bg: '#F0FDF4', text: '#15803D' },
    Approved:  { bg: '#F0FDF4', text: '#15803D' },
    Pending:   { bg: '#FEF9C3', text: '#A16207' },
    Rejected:  { bg: '#FEF2F2', text: '#DC2626' },
    Uploaded:  { bg: '#EFF6FF', text: '#2563EB' },
  };
  const s = map[status] ?? { bg: '#F3F4F6', text: '#6B7280' };
  return (
    <View style={{ backgroundColor: s.bg, borderRadius: 6, paddingHorizontal: 7, paddingVertical: 2 }}>
      <Text style={{ color: s.text, fontSize: 11, fontWeight: '600' }}>{status}</Text>
    </View>
  );
}

export default function DocumentsScreen() {
  const [documents, setDocuments] = useState<EmployeeDocument[]>([]);
  const [loading, setLoading] = useState(true);
  const [refreshing, setRefreshing] = useState(false);
  const [uploadModal, setUploadModal] = useState(false);
  const [uploadData, setUploadData] = useState({
    documentType: DOC_TYPES[0],
    expiryDate: '',
    documentNumber: '',
  });
  const [selectedFile, setSelectedFile] = useState<PickedFile | null>(null);
  const [uploading, setUploading] = useState(false);
  const [typePickerOpen, setTypePickerOpen] = useState(false);
  const [downloadingId, setDownloadingId] = useState<string | null>(null);

  const downloadDocument = async (doc: EmployeeDocument) => {
    setDownloadingId(doc.id);
    try {
      const { url, headers } = await documentsApi.downloadDocument(doc.id);
      const safeName = (doc.fileName ?? `document-${doc.id}`).replace(/[^\w.\-]+/g, '_');
      const result = await FileSystem.downloadAsync(url, `${FileSystem.cacheDirectory}${safeName}`, { headers });
      if (result.status !== 200) throw new Error(`Download failed (HTTP ${result.status})`);
      if (await Sharing.isAvailableAsync()) await Sharing.shareAsync(result.uri);
      else Alert.alert('Downloaded', `Saved to ${result.uri}`);
    } catch (e: any) {
      Alert.alert('Error', e.message || 'Could not open the document');
    } finally {
      setDownloadingId(null);
    }
  };

  const fetchDocuments = useCallback(async () => {
    try {
      const data = await documentsApi.getDocuments();
      setDocuments(Array.isArray(data) ? data : (data as any).items || []);
    } catch (e: any) {
      Alert.alert('Error', e.message || 'Failed to load documents');
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => { fetchDocuments(); }, [fetchDocuments]);

  const onRefresh = async () => {
    setRefreshing(true);
    await fetchDocuments();
    setRefreshing(false);
  };

  // Expiry alerts
  const expiring = documents.filter((d) => {
    const days = daysUntilExpiry(d.expiryDate);
    return days !== null && days <= 60;
  });

  const pickFile = async () => {
    try {
      const result = await DocumentPicker.getDocumentAsync({
        type: ['application/pdf', 'image/*'],
        copyToCacheDirectory: true,
      });
      if (!result.canceled && result.assets?.[0]) {
        const asset = result.assets[0];
        setSelectedFile(normalizePickedFile({
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

  const submitUpload = async () => {
    if (!selectedFile) return Alert.alert('Missing file', 'Please select a file to upload');
    setUploading(true);
    try {
      await documentsApi.uploadDocument({
        file: selectedFile,
        documentType: uploadData.documentType,
        expiryDate: uploadData.expiryDate || undefined,
        documentNumber: uploadData.documentNumber.trim() || undefined,
      });
      setUploadModal(false);
      setSelectedFile(null);
      setUploadData({ documentType: DOC_TYPES[0], expiryDate: '', documentNumber: '' });
      Alert.alert('Uploaded', 'Document uploaded successfully');
      fetchDocuments();
    } catch (e: any) {
      Alert.alert('Error', e.message || 'Upload failed');
    } finally {
      setUploading(false);
    }
  };

  return (
    <View style={{ flex: 1, backgroundColor: COLORS.background }}>
      {/* Header */}
      <View style={{ backgroundColor: COLORS.navy, paddingTop: 56, paddingBottom: 16, paddingHorizontal: 20 }}>
        <View style={{ flexDirection: 'row', justifyContent: 'space-between', alignItems: 'flex-end' }}>
          <View>
            <Text style={{ color: '#fff', fontSize: 22, fontWeight: '700' }}>Documents</Text>
            <Text style={{ color: 'rgba(255,255,255,0.6)', fontSize: 13, marginTop: 2 }}>
              Manage your identity documents
            </Text>
          </View>
          {FEATURES.FILE_UPLOAD && (
          <TouchableOpacity
            onPress={() => setUploadModal(true)}
            style={{ backgroundColor: COLORS.blue, borderRadius: 10, paddingHorizontal: 14, paddingVertical: 8 }}
          >
            <Text style={{ color: '#fff', fontWeight: '700', fontSize: 13 }}>+ Upload</Text>
          </TouchableOpacity>
          )}
        </View>
      </View>

      {loading ? (
        <ActivityIndicator color={COLORS.blue} style={{ marginTop: 60 }} />
      ) : (
        <ScrollView
          refreshControl={<RefreshControl refreshing={refreshing} onRefresh={onRefresh} />}
          contentContainerStyle={{ padding: 16, paddingBottom: 40 }}
        >
          {/* Expiry alerts */}
          {expiring.length > 0 && (
            <View style={{
              backgroundColor: '#FFF7ED', borderRadius: 12, padding: 14, marginBottom: 16,
              borderLeftWidth: 4, borderLeftColor: '#F59E0B',
            }}>
              <Text style={{ fontWeight: '700', color: '#92400E', fontSize: 14, marginBottom: 6 }}>
                ⚠️ {expiring.length} document(s) need attention
              </Text>
              {expiring.map((d) => {
                const days = daysUntilExpiry(d.expiryDate);
                return (
                  <Text key={d.id} style={{ color: '#92400E', fontSize: 13 }}>
                    • {d.documentType}: {days !== null && days < 0 ? 'Expired' : `${days} days left`}
                  </Text>
                );
              })}
            </View>
          )}

          {!FEATURES.FILE_UPLOAD && (
            <View style={{ backgroundColor: '#EFF6FF', borderRadius: 12, padding: 12, marginBottom: 14 }}>
              <Text style={{ color: '#1E40AF', fontSize: 13 }}>
                To add or replace a document, use the KynexOne web portal or send it to HR. Uploading from mobile is coming soon.
              </Text>
            </View>
          )}

          {documents.length === 0 ? (
            <View style={{ alignItems: 'center', marginTop: 60 }}>
              <Text style={{ fontSize: 40 }}>📄</Text>
              <Text style={{ color: '#374151', fontSize: 16, fontWeight: '600', marginTop: 12 }}>No documents</Text>
              <Text style={{ color: '#9CA3AF', fontSize: 13, marginTop: 4, textAlign: 'center' }}>
                Documents on your HR file appear here.
              </Text>
              {FEATURES.FILE_UPLOAD && (
              <TouchableOpacity
                onPress={() => setUploadModal(true)}
                style={{ backgroundColor: COLORS.blue, borderRadius: 10, paddingHorizontal: 20, paddingVertical: 10, marginTop: 16 }}
              >
                <Text style={{ color: '#fff', fontWeight: '700' }}>Upload Document</Text>
              </TouchableOpacity>
              )}
            </View>
          ) : (
            documents.map((doc) => (
              <View key={doc.id} style={{
                backgroundColor: '#fff', borderRadius: 14, padding: 16, marginBottom: 12,
                shadowColor: '#000', shadowOffset: { width: 0, height: 1 }, shadowOpacity: 0.06, shadowRadius: 4, elevation: 2,
              }}>
                <View style={{ flexDirection: 'row', justifyContent: 'space-between', alignItems: 'flex-start' }}>
                  <View style={{ flex: 1 }}>
                    <Text style={{ fontSize: 15, fontWeight: '700', color: '#111827' }}>{doc.documentType}</Text>
                    {doc.documentNumber && (
                      <Text style={{ fontSize: 13, color: '#6B7280', marginTop: 2 }}>#{doc.documentNumber}</Text>
                    )}
                  </View>
                  <View style={{ flexDirection: 'row', gap: 6 }}>
                    <StatusChip status={doc.verificationStatus ?? doc.status} />
                    <ExpiryChip date={doc.expiryDate} />
                  </View>
                </View>
                <View style={{ flexDirection: 'row', gap: 16, marginTop: 10 }}>
                  {doc.issueDate && (
                    <Text style={{ fontSize: 12, color: '#9CA3AF' }}>
                      Issued: {formatDate(doc.issueDate, 'display')}
                    </Text>
                  )}
                  {doc.expiryDate && (
                    <Text style={{ fontSize: 12, color: '#9CA3AF' }}>
                      Expires: {formatDate(doc.expiryDate, 'display')}
                    </Text>
                  )}
                </View>
                {doc.fileName && (
                  <TouchableOpacity
                    disabled={!FEATURES.DOCUMENT_DOWNLOAD || downloadingId === doc.id}
                    onPress={() => downloadDocument(doc)}
                    style={{ flexDirection: 'row', alignItems: 'center', marginTop: 8, gap: 6 }}
                  >
                    <Text style={{ fontSize: 12 }}>📎</Text>
                    <Text style={{ fontSize: 12, color: COLORS.blue }}>{doc.fileName}</Text>
                    {FEATURES.DOCUMENT_DOWNLOAD && (
                      downloadingId === doc.id
                        ? <ActivityIndicator size="small" color={COLORS.blue} />
                        : <Text style={{ fontSize: 12, color: COLORS.blue, fontWeight: '600' }}>· Open</Text>
                    )}
                  </TouchableOpacity>
                )}
              </View>
            ))
          )}
        </ScrollView>
      )}

      {/* Upload modal */}
      <Modal visible={uploadModal} animationType="slide" transparent>
        <View style={{ flex: 1, backgroundColor: 'rgba(0,0,0,0.5)', justifyContent: 'flex-end' }}>
          <ScrollView
            style={{ backgroundColor: '#fff', borderTopLeftRadius: 24, borderTopRightRadius: 24 }}
            contentContainerStyle={{ padding: 24, paddingBottom: 40 }}
            keyboardShouldPersistTaps="handled"
          >
            <Text style={{ fontSize: 18, fontWeight: '700', color: '#111827', marginBottom: 4 }}>Upload Document</Text>
            <Text style={{ fontSize: 13, color: '#6B7280', marginBottom: 20 }}>
              Supported formats: PDF, JPG, PNG
            </Text>

            {/* Document type */}
            <Text style={{ fontSize: 13, fontWeight: '600', color: '#374151', marginBottom: 6 }}>Document Type *</Text>
            <TouchableOpacity
              onPress={() => setTypePickerOpen(!typePickerOpen)}
              style={{
                borderWidth: 1, borderColor: '#D1D5DB', borderRadius: 10,
                paddingHorizontal: 14, paddingVertical: 12, marginBottom: 4,
                flexDirection: 'row', justifyContent: 'space-between', alignItems: 'center',
              }}
            >
              <Text style={{ fontSize: 15, color: '#111827' }}>{uploadData.documentType}</Text>
              <Text style={{ color: '#9CA3AF' }}>▼</Text>
            </TouchableOpacity>
            {typePickerOpen && (
              <View style={{
                borderWidth: 1, borderColor: '#D1D5DB', borderRadius: 10, marginBottom: 12,
                maxHeight: 200, overflow: 'hidden',
              }}>
                <ScrollView>
                  {DOC_TYPES.map((t) => (
                    <TouchableOpacity
                      key={t}
                      onPress={() => { setUploadData((prev) => ({ ...prev, documentType: t })); setTypePickerOpen(false); }}
                      style={{
                        paddingHorizontal: 14, paddingVertical: 12,
                        backgroundColor: uploadData.documentType === t ? '#EFF6FF' : '#fff',
                        borderBottomWidth: 1, borderBottomColor: '#F3F4F6',
                      }}
                    >
                      <Text style={{ color: uploadData.documentType === t ? COLORS.blue : '#374151' }}>{t}</Text>
                    </TouchableOpacity>
                  ))}
                </ScrollView>
              </View>
            )}

            {/* Expiry date */}
            <Text style={{ fontSize: 13, fontWeight: '600', color: '#374151', marginBottom: 6, marginTop: 8 }}>
              Expiry Date (optional)
            </Text>
            <TextInput
              value={uploadData.expiryDate}
              onChangeText={(v) => setUploadData((prev) => ({ ...prev, expiryDate: v }))}
              placeholder="YYYY-MM-DD"
              style={{
                borderWidth: 1, borderColor: '#D1D5DB', borderRadius: 10,
                paddingHorizontal: 14, paddingVertical: 12, fontSize: 15, marginBottom: 14,
              }}
            />

            {/* Document number */}
            <Text style={{ fontSize: 13, fontWeight: '600', color: '#374151', marginBottom: 6 }}>Document Number (optional)</Text>
            <TextInput
              value={uploadData.documentNumber}
              onChangeText={(v) => setUploadData((prev) => ({ ...prev, documentNumber: v }))}
              placeholder="Passport, visa, permit or certificate number"
              style={{
                borderWidth: 1, borderColor: '#D1D5DB', borderRadius: 10,
                paddingHorizontal: 14, paddingVertical: 12, fontSize: 15, marginBottom: 14, minHeight: 80,
              }}
            />

            {/* File picker */}
            <TouchableOpacity
              onPress={pickFile}
              style={{
                borderWidth: 2, borderColor: selectedFile ? COLORS.blue : '#D1D5DB',
                borderStyle: 'dashed', borderRadius: 12, padding: 16,
                alignItems: 'center', marginBottom: 20,
                backgroundColor: selectedFile ? '#EFF6FF' : '#FAFAFA',
              }}
            >
              <Text style={{ fontSize: 24, marginBottom: 4 }}>📁</Text>
              <Text style={{ color: selectedFile ? COLORS.blue : '#9CA3AF', fontSize: 13, fontWeight: selectedFile ? '600' : '400' }}>
                {selectedFile ? selectedFile.name : 'Tap to select file'}
              </Text>
            </TouchableOpacity>

            {/* Actions */}
            <View style={{ flexDirection: 'row', gap: 12 }}>
              <TouchableOpacity
                onPress={() => { setUploadModal(false); setSelectedFile(null); }}
                style={{ flex: 1, borderWidth: 1, borderColor: '#D1D5DB', borderRadius: 12, padding: 14, alignItems: 'center' }}
              >
                <Text style={{ color: '#374151', fontWeight: '600' }}>Cancel</Text>
              </TouchableOpacity>
              <TouchableOpacity
                onPress={submitUpload}
                disabled={uploading || !selectedFile}
                style={{
                  flex: 1, borderRadius: 12, padding: 14, alignItems: 'center',
                  backgroundColor: (!selectedFile || uploading) ? '#93C5FD' : COLORS.blue,
                }}
              >
                {uploading ? <ActivityIndicator color="#fff" /> : (
                  <Text style={{ color: '#fff', fontWeight: '700' }}>Upload</Text>
                )}
              </TouchableOpacity>
            </View>
          </ScrollView>
        </View>
      </Modal>
    </View>
  );
}
