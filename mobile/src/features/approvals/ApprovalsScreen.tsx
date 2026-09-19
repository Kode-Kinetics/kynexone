// ============================================================
// ZAYRA MOBILE — Approval Center Screen
// ============================================================

import React, { useState, useEffect, useCallback, useMemo } from 'react';
import {
  View, Text, ScrollView, TouchableOpacity, StyleSheet,
  RefreshControl, ActivityIndicator, Alert, TextInput,
  Modal, Platform,
} from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import { approvalsApi } from '@/api/services';
import { formatDate } from '@/utils/date';
import { COLORS } from '@/config';
import type { ApprovalItem, ApprovalItemType } from '@/types';
import { useAuthStore } from '@/auth/authStore';
import { hasEffectivePermission } from '@/auth/accessPolicy';

interface Props {
  navigation: any;
  route?: { params?: { filterType?: ApprovalItemType } };
}

export default function ApprovalsScreen({ navigation, route }: Props) {
  const user = useAuthStore((state) => state.user);
  const canDecide = user?.accessMode !== 'ReadOnlyAuditor'
    && hasEffectivePermission(user, 'approvals.decide');
  const [tab, setTab] = useState<'PENDING' | 'HISTORY'>('PENDING');
  const [items, setItems] = useState<ApprovalItem[]>([]);
  const [loading, setLoading] = useState(true);
  const [refreshing, setRefreshing] = useState(false);
  const [filterType, setFilterType] = useState<ApprovalItemType | 'ALL'>(
    route?.params?.filterType ?? 'ALL'
  );

  // Action modal
  const [modalVisible, setModalVisible] = useState(false);
  const [selectedItem, setSelectedItem] = useState<ApprovalItem | null>(null);
  const [modalAction, setModalAction] = useState<'APPROVE' | 'REJECT' | 'SEND_BACK'>('APPROVE');
  const [comment, setComment] = useState('');
  const [actionLoading, setActionLoading] = useState(false);

  const load = useCallback(async (silent = false) => {
    if (!silent) setLoading(true);
    try {
      if (tab === 'PENDING') {
        const data = await approvalsApi.getPendingApprovals();
        setItems(data);
      } else {
        const data = await approvalsApi.getApprovalHistory();
        setItems(data.data);
      }
    } catch (err) {
      console.error('[Approvals]', err);
    } finally {
      setLoading(false);
      setRefreshing(false);
    }
  }, [tab]);

  useEffect(() => {
    void load();
  }, [load]);

  const filteredItems = useMemo(
    () => filterType === 'ALL' ? items : items.filter((item) => item.type === filterType),
    [filterType, items]
  );

  const onRefresh = useCallback(() => {
    setRefreshing(true);
    void load(true);
  }, [load]);

  function openAction(item: ApprovalItem, action: 'APPROVE' | 'REJECT' | 'SEND_BACK') {
    if (!canDecide) return;
    if (action === 'APPROVE' && !item.canApprove) return;
    if (action === 'REJECT' && !item.canReject) return;
    if (action === 'SEND_BACK' && !item.canSendBack) return;
    setSelectedItem(item);
    setModalAction(action);
    setComment('');
    setModalVisible(true);
  }

  async function submitAction() {
    if (!selectedItem) return;
    if ((modalAction === 'REJECT' || modalAction === 'SEND_BACK') && !comment.trim()) {
      Alert.alert('Required', 'Please provide a reason.');
      return;
    }

    setActionLoading(true);
    try {
      if (modalAction === 'APPROVE') {
        await approvalsApi.approve(selectedItem.taskId, comment || undefined);
      } else if (modalAction === 'REJECT') {
        await approvalsApi.reject(selectedItem.taskId, comment);
      } else {
        await approvalsApi.sendBack(selectedItem.taskId, comment);
      }
      setModalVisible(false);
      Alert.alert('Success', `Request ${modalAction.toLowerCase().replace('_', ' ')}d.`);
      load(true);
    } catch (err: any) {
      Alert.alert('Error', err?.response?.data?.message ?? 'Action failed. Please try again.');
    } finally {
      setActionLoading(false);
    }
  }

  const TYPE_CONFIG: Record<string, { label: string; icon: string; color: string }> = {
    LEAVE: { label: 'Leave', icon: 'airplane-outline', color: COLORS.blue },
    OVERTIME: { label: 'Overtime', icon: 'time-outline', color: '#7C3AED' },
    ATTENDANCE_CORRECTION: { label: 'Attendance', icon: 'create-outline', color: COLORS.warning },
    RECRUITMENT_REQUISITION: { label: 'Recruitment', icon: 'briefcase-outline', color: COLORS.success },
    HR_REQUEST: { label: 'HR Request', icon: 'help-circle-outline', color: '#0EA5E9' },
    LOAN: { label: 'Loan', icon: 'card-outline', color: '#EC4899' },
    ADVANCE: { label: 'Advance', icon: 'cash-outline', color: '#EC4899' },
    PAYROLL: { label: 'Payroll', icon: 'document-text-outline', color: COLORS.success },
    APPRAISAL: { label: 'Appraisal', icon: 'star-outline', color: COLORS.warning },
  };

  const allTypes = ['ALL', ...Object.keys(TYPE_CONFIG)] as const;

  return (
    <View style={styles.container}>
      {/* Header */}
      <View style={styles.header}>
        <TouchableOpacity onPress={() => navigation.goBack()}>
          <Ionicons name="arrow-back" size={24} color={COLORS.text} />
        </TouchableOpacity>
        <Text style={styles.headerTitle}>Approval Center</Text>
        <View style={{ width: 24 }} />
      </View>

      {/* Tabs */}
      <View style={styles.tabs}>
        {(['PENDING', 'HISTORY'] as const).map((t) => (
          <TouchableOpacity
            key={t}
            style={[styles.tab, tab === t && styles.tabActive]}
            onPress={() => setTab(t)}
          >
            <Text style={[styles.tabText, tab === t && styles.tabTextActive]}>
              {t === 'PENDING' ? `Pending${items.length > 0 && tab === 'PENDING' ? ` (${items.length})` : ''}` : 'History'}
            </Text>
          </TouchableOpacity>
        ))}
      </View>

      {/* Type filter */}
      <ScrollView horizontal showsHorizontalScrollIndicator={false} style={styles.filterBar}>
        {allTypes.map((type) => {
          const cfg = type === 'ALL' ? null : TYPE_CONFIG[type];
          const isActive = filterType === type;
          return (
            <TouchableOpacity
              key={type}
              style={[styles.filterChip, isActive && styles.filterChipActive]}
              onPress={() => setFilterType(type as any)}
            >
              {cfg && (
                <Ionicons
                  name={cfg.icon as any}
                  size={13}
                  color={isActive ? '#fff' : COLORS.muted}
                />
              )}
              <Text style={[styles.filterChipText, isActive && styles.filterChipTextActive]}>
                {type === 'ALL' ? 'All' : cfg?.label ?? type}
              </Text>
            </TouchableOpacity>
          );
        })}
      </ScrollView>

      {/* List */}
      {loading ? (
        <View style={styles.centered}>
          <ActivityIndicator color={COLORS.blue} size="large" />
        </View>
      ) : (
        <ScrollView
          style={styles.list}
          contentContainerStyle={styles.listContent}
          refreshControl={
            <RefreshControl refreshing={refreshing} onRefresh={onRefresh} tintColor={COLORS.blue} />
          }
          showsVerticalScrollIndicator={false}
        >
          {filteredItems.length === 0 ? (
            <View style={styles.emptyState}>
              <Ionicons name="checkmark-done-circle-outline" size={56} color={COLORS.muted} />
              <Text style={styles.emptyTitle}>
                {tab === 'PENDING' ? 'All caught up!' : 'No history yet'}
              </Text>
              <Text style={styles.emptySubtitle}>
                {tab === 'PENDING' ? 'No pending approvals at this time.' : 'Completed approvals will appear here.'}
              </Text>
            </View>
          ) : (
            filteredItems.map((item) => {
              const cfg = TYPE_CONFIG[item.type] ?? { label: item.type, icon: 'document-outline', color: COLORS.muted };
              return (
                <View key={item.id} style={styles.approvalCard}>
                  {/* Card header */}
                  <View style={styles.cardHeader}>
                    <View style={[styles.typeIcon, { backgroundColor: `${cfg.color}15` }]}>
                      <Ionicons name={cfg.icon as any} size={18} color={cfg.color} />
                    </View>
                    <View style={{ flex: 1 }}>
                      <Text style={styles.cardType}>{cfg.label}</Text>
                      <Text style={styles.cardTitle} numberOfLines={1}>{item.title}</Text>
                    </View>
                    <UrgencyBadge urgency={item.urgency} />
                  </View>

                  {/* Summary */}
                  <Text style={styles.cardSummary}>{item.summary}</Text>

                  {/* Requester */}
                  <View style={styles.requesterRow}>
                    <Ionicons name="person-outline" size={13} color={COLORS.muted} />
                    <Text style={styles.requesterText}>{item.requestedBy}</Text>
                    <View style={styles.dotSep} />
                    <Text style={styles.timeText}>
                      {formatDate(item.requestedAt, 'relative')}
                    </Text>
                  </View>

                  {/* Detail rows */}
                  {Object.entries(item.details).slice(0, 3).map(([key, val]) => (
                    <View key={key} style={styles.detailRow}>
                      <Text style={styles.detailKey}>
                        {key.replace(/([A-Z])/g, ' $1').trim()}:
                      </Text>
                      <Text style={styles.detailVal}>{String(val)}</Text>
                    </View>
                  ))}

                  {/* Actions (pending only) */}
                  {tab === 'PENDING' && canDecide && (
                    <View style={styles.cardActions}>
                      {item.canSendBack && (
                        <TouchableOpacity
                          style={styles.sendBackBtn}
                          onPress={() => openAction(item, 'SEND_BACK')}
                        >
                          <Ionicons name="return-up-back-outline" size={14} color={COLORS.muted} />
                          <Text style={styles.sendBackBtnText}>Return</Text>
                        </TouchableOpacity>
                      )}
                      <View style={styles.cardActionsFlex} />
                      {item.canReject && (
                        <TouchableOpacity
                          style={styles.rejectBtn}
                          onPress={() => openAction(item, 'REJECT')}
                        >
                          <Text style={styles.rejectBtnText}>Reject</Text>
                        </TouchableOpacity>
                      )}
                      {item.canApprove && (
                        <TouchableOpacity
                          style={styles.approveBtn}
                          onPress={() => openAction(item, 'APPROVE')}
                        >
                          <Ionicons name="checkmark" size={14} color="#fff" />
                          <Text style={styles.approveBtnText}>Approve</Text>
                        </TouchableOpacity>
                      )}
                    </View>
                  )}
                </View>
              );
            })
          )}
          <View style={{ height: 40 }} />
        </ScrollView>
      )}

      {/* Action Modal */}
      <Modal visible={modalVisible} transparent animationType="slide">
        <View style={styles.modalOverlay}>
          <View style={styles.modalContent}>
            <View style={styles.modalHandle} />
            <Text style={styles.modalTitle}>
              {modalAction === 'APPROVE'
                ? '✓ Confirm Approval'
                : modalAction === 'REJECT'
                ? '✗ Reject Request'
                : '↩ Return Request'}
            </Text>
            {selectedItem && (
              <Text style={styles.modalSubtitle}>{selectedItem.title}</Text>
            )}

            <Text style={styles.commentLabel}>
              {modalAction === 'APPROVE' ? 'Comment (optional)' : 'Reason (required)'}
            </Text>
            <TextInput
              style={styles.commentInput}
              placeholder={
                modalAction === 'APPROVE'
                  ? 'Add a comment...'
                  : 'Provide a reason...'
              }
              placeholderTextColor={COLORS.muted}
              value={comment}
              onChangeText={setComment}
              multiline
              numberOfLines={3}
              textAlignVertical="top"
            />

            <View style={styles.modalActions}>
              <TouchableOpacity
                style={styles.modalCancelBtn}
                onPress={() => setModalVisible(false)}
              >
                <Text style={styles.modalCancelText}>Cancel</Text>
              </TouchableOpacity>
              <TouchableOpacity
                style={[
                  styles.modalConfirmBtn,
                  modalAction === 'REJECT' && styles.modalRejectConfirmBtn,
                  actionLoading && { opacity: 0.6 },
                ]}
                onPress={submitAction}
                disabled={actionLoading}
              >
                {actionLoading ? (
                  <ActivityIndicator size="small" color="#fff" />
                ) : (
                  <Text style={styles.modalConfirmText}>
                    {modalAction === 'APPROVE'
                      ? 'Approve'
                      : modalAction === 'REJECT'
                      ? 'Reject'
                      : 'Return'}
                  </Text>
                )}
              </TouchableOpacity>
            </View>
          </View>
        </View>
      </Modal>
    </View>
  );
}

function UrgencyBadge({ urgency }: { urgency: 'LOW' | 'MEDIUM' | 'HIGH' }) {
  const cfg = {
    LOW: { color: COLORS.success, label: 'Low' },
    MEDIUM: { color: COLORS.warning, label: 'Med' },
    HIGH: { color: COLORS.error, label: 'High' },
  }[urgency];

  return (
    <View style={[styles.urgencyBadge, { backgroundColor: `${cfg.color}15` }]}>
      <Text style={[styles.urgencyText, { color: cfg.color }]}>{cfg.label}</Text>
    </View>
  );
}

const styles = StyleSheet.create({
  container: { flex: 1, backgroundColor: COLORS.bg },
  header: {
    flexDirection: 'row', alignItems: 'center', justifyContent: 'space-between',
    paddingHorizontal: 16, paddingTop: Platform.OS === 'ios' ? 56 : 40, paddingBottom: 16,
    backgroundColor: COLORS.card,
    shadowColor: '#000', shadowOffset: { width: 0, height: 1 }, shadowOpacity: 0.05, shadowRadius: 3, elevation: 2,
  },
  headerTitle: { fontSize: 17, fontWeight: '700', color: COLORS.text },
  tabs: { flexDirection: 'row', backgroundColor: COLORS.card, paddingHorizontal: 16, paddingBottom: 12 },
  tab: { flex: 1, paddingVertical: 8, alignItems: 'center', borderBottomWidth: 2, borderBottomColor: 'transparent' },
  tabActive: { borderBottomColor: COLORS.blue },
  tabText: { fontSize: 15, fontWeight: '600', color: COLORS.muted },
  tabTextActive: { color: COLORS.blue },
  filterBar: { paddingHorizontal: 16, paddingVertical: 10, maxHeight: 52 },
  filterChip: {
    flexDirection: 'row', alignItems: 'center', gap: 5,
    paddingHorizontal: 12, paddingVertical: 6, borderRadius: 20,
    backgroundColor: COLORS.card, borderWidth: 1, borderColor: COLORS.border, marginRight: 8,
  },
  filterChipActive: { backgroundColor: COLORS.blue, borderColor: COLORS.blue },
  filterChipText: { fontSize: 12, fontWeight: '600', color: COLORS.muted },
  filterChipTextActive: { color: '#fff' },
  list: { flex: 1 },
  listContent: { padding: 16 },
  centered: { flex: 1, alignItems: 'center', justifyContent: 'center' },
  emptyState: { alignItems: 'center', paddingTop: 60 },
  emptyTitle: { fontSize: 18, fontWeight: '700', color: COLORS.text, marginTop: 16 },
  emptySubtitle: { fontSize: 14, color: COLORS.muted, marginTop: 6, textAlign: 'center' },
  approvalCard: {
    backgroundColor: COLORS.card, borderRadius: 14, padding: 16, marginBottom: 12,
    shadowColor: '#000', shadowOffset: { width: 0, height: 2 }, shadowOpacity: 0.06, shadowRadius: 6, elevation: 2,
  },
  cardHeader: { flexDirection: 'row', alignItems: 'center', gap: 12, marginBottom: 10 },
  typeIcon: { width: 40, height: 40, borderRadius: 12, alignItems: 'center', justifyContent: 'center' },
  cardType: { fontSize: 11, color: COLORS.muted, fontWeight: '600', textTransform: 'uppercase', letterSpacing: 0.5 },
  cardTitle: { fontSize: 15, fontWeight: '700', color: COLORS.text },
  urgencyBadge: { paddingHorizontal: 8, paddingVertical: 3, borderRadius: 20 },
  urgencyText: { fontSize: 11, fontWeight: '700' },
  cardSummary: { fontSize: 14, color: COLORS.textSecondary, marginBottom: 10, lineHeight: 20 },
  requesterRow: { flexDirection: 'row', alignItems: 'center', gap: 5, marginBottom: 10 },
  requesterText: { fontSize: 13, color: COLORS.muted },
  dotSep: { width: 3, height: 3, borderRadius: 1.5, backgroundColor: COLORS.muted },
  timeText: { fontSize: 13, color: COLORS.muted },
  detailRow: { flexDirection: 'row', gap: 6, marginBottom: 4 },
  detailKey: { fontSize: 13, color: COLORS.muted, fontWeight: '500', flex: 1 },
  detailVal: { fontSize: 13, color: COLORS.text, fontWeight: '600' },
  cardActions: { flexDirection: 'row', alignItems: 'center', marginTop: 14, paddingTop: 12, borderTopWidth: 1, borderTopColor: COLORS.border },
  cardActionsFlex: { flex: 1 },
  sendBackBtn: { flexDirection: 'row', alignItems: 'center', gap: 4, paddingVertical: 8, paddingHorizontal: 10 },
  sendBackBtnText: { fontSize: 13, color: COLORS.muted, fontWeight: '600' },
  rejectBtn: { paddingVertical: 8, paddingHorizontal: 16, borderRadius: 10, borderWidth: 1.5, borderColor: COLORS.error, marginRight: 8 },
  rejectBtnText: { fontSize: 14, fontWeight: '700', color: COLORS.error },
  approveBtn: { flexDirection: 'row', alignItems: 'center', gap: 5, paddingVertical: 8, paddingHorizontal: 16, borderRadius: 10, backgroundColor: COLORS.success },
  approveBtnText: { fontSize: 14, fontWeight: '700', color: '#fff' },
  // Modal
  modalOverlay: { flex: 1, backgroundColor: 'rgba(0,0,0,0.5)', justifyContent: 'flex-end' },
  modalContent: { backgroundColor: COLORS.card, borderTopLeftRadius: 20, borderTopRightRadius: 20, padding: 24, paddingBottom: 40 },
  modalHandle: { width: 40, height: 4, backgroundColor: COLORS.border, borderRadius: 2, alignSelf: 'center', marginBottom: 20 },
  modalTitle: { fontSize: 18, fontWeight: '800', color: COLORS.text, marginBottom: 4 },
  modalSubtitle: { fontSize: 14, color: COLORS.muted, marginBottom: 20 },
  commentLabel: { fontSize: 13, fontWeight: '600', color: COLORS.text, marginBottom: 8 },
  commentInput: {
    borderWidth: 1, borderColor: COLORS.border, borderRadius: 12, padding: 14,
    fontSize: 15, color: COLORS.text, minHeight: 90, marginBottom: 20,
  },
  modalActions: { flexDirection: 'row', gap: 12 },
  modalCancelBtn: { flex: 1, paddingVertical: 14, borderRadius: 12, backgroundColor: COLORS.bg, alignItems: 'center' },
  modalCancelText: { fontSize: 15, fontWeight: '600', color: COLORS.text },
  modalConfirmBtn: { flex: 1, paddingVertical: 14, borderRadius: 12, backgroundColor: COLORS.success, alignItems: 'center' },
  modalRejectConfirmBtn: { backgroundColor: COLORS.error },
  modalConfirmText: { fontSize: 15, fontWeight: '700', color: '#fff' },
});
