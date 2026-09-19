import React, { useCallback, useEffect, useMemo, useState } from 'react';
import {
  ActivityIndicator,
  Alert,
  Modal,
  RefreshControl,
  ScrollView,
  StyleSheet,
  Text,
  TextInput,
  View,
} from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import { approvalsApi } from '@/api/services';
import { formatDate } from '@/utils/date';
import { useTheme } from '@/theme/ThemeProvider';
import {
  GlassIconButton,
  GlassSurface,
  LiquidBackdrop,
  LiquidButton,
  MotionPressable,
  ScreenHero,
  SectionHeader,
} from '@/components/ui';
import type { ApprovalItem, ApprovalItemType } from '@/types';

interface Props {
  navigation: any;
  route?: { params?: { filterType?: ApprovalItemType } };
}

type ApprovalTab = 'PENDING' | 'HISTORY';
type ApprovalAction = 'APPROVE' | 'REJECT' | 'SEND_BACK';

export default function ApprovalsScreen({ navigation, route }: Props) {
  const { theme } = useTheme();
  const [tab, setTab] = useState<ApprovalTab>('PENDING');
  const [items, setItems] = useState<ApprovalItem[]>([]);
  const [loading, setLoading] = useState(true);
  const [refreshing, setRefreshing] = useState(false);
  const [filterType, setFilterType] = useState<ApprovalItemType | 'ALL'>(
    route?.params?.filterType ?? 'ALL',
  );
  const [modalVisible, setModalVisible] = useState(false);
  const [selectedItem, setSelectedItem] = useState<ApprovalItem | null>(null);
  const [modalAction, setModalAction] = useState<ApprovalAction>('APPROVE');
  const [comment, setComment] = useState('');
  const [actionLoading, setActionLoading] = useState(false);

  const typeConfig = useMemo<Record<string, {
    label: string;
    icon: React.ComponentProps<typeof Ionicons>['name'];
    color: string;
  }>>(() => ({
    LEAVE: { label: 'Leave', icon: 'airplane-outline', color: theme.colors.primary },
    OVERTIME: { label: 'Overtime', icon: 'time-outline', color: theme.colors.violet },
    ATTENDANCE_CORRECTION: { label: 'Attendance', icon: 'create-outline', color: theme.colors.warning },
    RECRUITMENT_REQUISITION: { label: 'Recruitment', icon: 'briefcase-outline', color: theme.colors.success },
    HR_REQUEST: { label: 'HR request', icon: 'help-circle-outline', color: theme.colors.cyan },
    LOAN: { label: 'Loan', icon: 'card-outline', color: '#EC4899' },
    ADVANCE: { label: 'Advance', icon: 'cash-outline', color: '#EC4899' },
    PAYROLL: { label: 'Payroll', icon: 'document-text-outline', color: theme.colors.success },
    APPRAISAL: { label: 'Appraisal', icon: 'star-outline', color: theme.colors.warning },
  }), [theme]);

  const load = useCallback(async (silent = false) => {
    if (!silent) setLoading(true);
    try {
      if (tab === 'PENDING') {
        setItems(await approvalsApi.getPendingApprovals());
      } else {
        const history = await approvalsApi.getApprovalHistory();
        setItems(history.data);
      }
    } catch (error: any) {
      Alert.alert('Approvals unavailable', error?.response?.data?.message ?? 'Could not load approvals.');
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
    [filterType, items],
  );

  const pendingByType = useMemo(() => items.reduce<Record<string, number>>((counts, item) => {
    counts[item.type] = (counts[item.type] ?? 0) + 1;
    return counts;
  }, {}), [items]);

  const openAction = (item: ApprovalItem, action: ApprovalAction) => {
    if (action === 'APPROVE' && !item.canApprove) return;
    if (action === 'REJECT' && !item.canReject) return;
    if (action === 'SEND_BACK' && !item.canSendBack) return;
    setSelectedItem(item);
    setModalAction(action);
    setComment('');
    setModalVisible(true);
  };

  const submitAction = async () => {
    if (!selectedItem) return;
    if ((modalAction === 'REJECT' || modalAction === 'SEND_BACK') && !comment.trim()) {
      Alert.alert('Reason required', 'Add a clear reason before continuing.');
      return;
    }

    setActionLoading(true);
    try {
      if (modalAction === 'APPROVE') {
        await approvalsApi.approve(selectedItem.taskId, comment.trim() || undefined);
      } else if (modalAction === 'REJECT') {
        await approvalsApi.reject(selectedItem.taskId, comment.trim());
      } else {
        await approvalsApi.sendBack(selectedItem.taskId, comment.trim());
      }
      setModalVisible(false);
      Alert.alert('Decision recorded', 'The workflow has been updated and the requester will be notified.');
      await load(true);
    } catch (error: any) {
      Alert.alert('Action failed', error?.response?.data?.message ?? 'Please try again.');
    } finally {
      setActionLoading(false);
    }
  };

  const availableTypes = useMemo(
    () => ['ALL', ...Object.keys(typeConfig).filter((type) => pendingByType[type] || filterType === type)] as (ApprovalItemType | 'ALL')[],
    [filterType, pendingByType, typeConfig],
  );

  return (
    <View style={[styles.root, { backgroundColor: theme.colors.canvas }]}>
      <LiquidBackdrop subtle />
      <ScrollView
        contentContainerStyle={styles.content}
        refreshControl={
          <RefreshControl
            refreshing={refreshing}
            onRefresh={() => {
              setRefreshing(true);
              void load(true);
            }}
            tintColor={theme.colors.primary}
            colors={[theme.colors.primary]}
          />
        }
        showsVerticalScrollIndicator={false}
      >
        <ScreenHero
          eyebrow="Manager workspace"
          title="Approval center"
          subtitle={tab === 'PENDING' ? `${items.length} pending decision${items.length === 1 ? '' : 's'}` : 'Completed workflow decisions'}
          actions={
            navigation.canGoBack() ? (
              <GlassIconButton icon="arrow-back" label="Go back" onPress={() => navigation.goBack()} />
            ) : undefined
          }
        />

        <View style={styles.section}>
          <GlassSurface elevated={false} radius={theme.radius.xl} contentStyle={styles.tabContainer}>
            {(['PENDING', 'HISTORY'] as const).map((nextTab) => {
              const selected = tab === nextTab;
              return (
                <MotionPressable
                  key={nextTab}
                  onPress={() => setTab(nextTab)}
                  haptic="selection"
                  style={styles.tabShell}
                  contentStyle={[
                    styles.tabButton,
                    { backgroundColor: selected ? `${theme.colors.primary}1F` : 'transparent' },
                  ]}
                  accessibilityRole="tab"
                  accessibilityState={{ selected }}
                >
                  <Ionicons
                    name={nextTab === 'PENDING' ? 'hourglass-outline' : 'time-outline'}
                    size={18}
                    color={selected ? theme.colors.primary : theme.colors.textMuted}
                  />
                  <Text
                    style={[
                      theme.typography.bodyStrong,
                      { color: selected ? theme.colors.primary : theme.colors.textSecondary },
                    ]}
                  >
                    {nextTab === 'PENDING' ? 'Pending' : 'History'}
                  </Text>
                  {nextTab === 'PENDING' && items.length > 0 ? (
                    <View style={[styles.tabCount, { backgroundColor: theme.colors.primary }]}>
                      <Text style={styles.tabCountText}>{items.length}</Text>
                    </View>
                  ) : null}
                </MotionPressable>
              );
            })}
          </GlassSurface>
        </View>

        <View style={styles.filterSection}>
          <ScrollView horizontal showsHorizontalScrollIndicator={false} contentContainerStyle={styles.filterRail}>
            {availableTypes.map((type) => {
              const selected = filterType === type;
              const meta = type === 'ALL'
                ? { label: 'All', icon: 'apps-outline' as const, color: theme.colors.primary }
                : typeConfig[type] ?? { label: type, icon: 'document-outline' as const, color: theme.colors.textMuted };
              const count = type === 'ALL' ? items.length : pendingByType[type] ?? 0;
              return (
                <MotionPressable
                  key={type}
                  onPress={() => setFilterType(type)}
                  haptic="selection"
                  contentStyle={[
                    styles.filterChip,
                    {
                      backgroundColor: selected ? `${meta.color}20` : theme.colors.surface,
                      borderColor: selected ? meta.color : theme.colors.border,
                    },
                  ]}
                  accessibilityRole="radio"
                  accessibilityState={{ selected }}
                >
                  <Ionicons name={meta.icon} size={15} color={selected ? meta.color : theme.colors.textMuted} />
                  <Text
                    style={[
                      theme.typography.caption,
                      { color: selected ? meta.color : theme.colors.textSecondary, fontWeight: selected ? '700' : '500' },
                    ]}
                  >
                    {meta.label}{count ? ` · ${count}` : ''}
                  </Text>
                </MotionPressable>
              );
            })}
          </ScrollView>
        </View>

        <View style={styles.section}>
          <SectionHeader
            title={tab === 'PENDING' ? 'Decisions waiting' : 'Decision history'}
            subtitle={`${filteredItems.length} item${filteredItems.length === 1 ? '' : 's'}`}
          />
          {loading ? (
            <GlassSurface radius={theme.radius.xl} contentStyle={styles.stateCard}>
              <ActivityIndicator color={theme.colors.primary} />
              <Text style={[theme.typography.caption, { color: theme.colors.textSecondary }]}>Loading approval workflow…</Text>
            </GlassSurface>
          ) : filteredItems.length === 0 ? (
            <GlassSurface radius={theme.radius.xl} contentStyle={styles.emptyCard}>
              <View style={[styles.emptyIcon, { backgroundColor: `${theme.colors.success}18` }]}>
                <Ionicons name="checkmark-done-circle-outline" size={32} color={theme.colors.success} />
              </View>
              <Text style={[theme.typography.h3, { color: theme.colors.text }]}>
                {tab === 'PENDING' ? 'You are all caught up' : 'No decisions yet'}
              </Text>
              <Text style={[theme.typography.caption, styles.emptyText, { color: theme.colors.textMuted }]}>
                {tab === 'PENDING'
                  ? 'New requests that require your authority will appear here.'
                  : 'Completed approvals and rejections will be recorded here.'}
              </Text>
            </GlassSurface>
          ) : (
            <View style={styles.list}>
              {filteredItems.map((item) => (
                <ApprovalCard
                  key={item.id}
                  item={item}
                  pending={tab === 'PENDING'}
                  meta={typeConfig[item.type] ?? {
                    label: item.type,
                    icon: 'document-outline',
                    color: theme.colors.textMuted,
                  }}
                  onApprove={() => openAction(item, 'APPROVE')}
                  onReject={() => openAction(item, 'REJECT')}
                  onSendBack={() => openAction(item, 'SEND_BACK')}
                />
              ))}
            </View>
          )}
        </View>
        <View style={styles.bottomSpacer} />
      </ScrollView>

      <DecisionModal
        visible={modalVisible}
        action={modalAction}
        item={selectedItem}
        comment={comment}
        loading={actionLoading}
        onCommentChange={setComment}
        onClose={() => setModalVisible(false)}
        onSubmit={() => void submitAction()}
      />
    </View>
  );
}

function ApprovalCard({
  item,
  pending,
  meta,
  onApprove,
  onReject,
  onSendBack,
}: {
  item: ApprovalItem;
  pending: boolean;
  meta: { label: string; icon: React.ComponentProps<typeof Ionicons>['name']; color: string };
  onApprove: () => void;
  onReject: () => void;
  onSendBack: () => void;
}) {
  const { theme } = useTheme();
  const urgency = getUrgency(item.urgency, theme);

  return (
    <GlassSurface radius={theme.radius.xl} contentStyle={styles.approvalCard}>
      <View style={styles.cardHeader}>
        <View style={[styles.typeIcon, { backgroundColor: `${meta.color}18` }]}>
          <Ionicons name={meta.icon} size={21} color={meta.color} />
        </View>
        <View style={styles.cardHeading}>
          <Text style={[theme.typography.micro, { color: meta.color, textTransform: 'uppercase', letterSpacing: 0.6 }]}>
            {meta.label}
          </Text>
          <Text numberOfLines={2} style={[theme.typography.h3, { color: theme.colors.text, marginTop: 3 }]}>
            {item.title}
          </Text>
        </View>
        <View style={[styles.urgencyPill, { backgroundColor: `${urgency.color}18` }]}>
          <Text style={[theme.typography.micro, { color: urgency.color }]}>{urgency.label}</Text>
        </View>
      </View>

      <Text style={[theme.typography.body, styles.summary, { color: theme.colors.textSecondary }]}>
        {item.summary}
      </Text>

      <View style={styles.requesterRow}>
        <View style={[styles.requesterAvatar, { backgroundColor: `${theme.colors.primary}18` }]}>
          <Ionicons name="person-outline" size={16} color={theme.colors.primary} />
        </View>
        <View style={styles.requesterCopy}>
          <Text style={[theme.typography.caption, { color: theme.colors.text }]}>{item.requestedBy}</Text>
          <Text style={[theme.typography.micro, { color: theme.colors.textMuted, marginTop: 2 }]}>
            Requested {formatDate(item.requestedAt, 'relative')}
          </Text>
        </View>
      </View>

      {Object.entries(item.details).length ? (
        <View style={[styles.detailsPanel, { backgroundColor: theme.colors.surfaceSoft }]}>
          {Object.entries(item.details).slice(0, 4).map(([key, value], index, rows) => (
            <View
              key={key}
              style={[
                styles.detailRow,
                index < rows.length - 1 && {
                  borderBottomColor: theme.colors.divider,
                  borderBottomWidth: StyleSheet.hairlineWidth,
                },
              ]}
            >
              <Text style={[theme.typography.micro, styles.detailLabel, { color: theme.colors.textMuted }]}>
                {key.replace(/([A-Z])/g, ' $1').trim()}
              </Text>
              <Text style={[theme.typography.caption, { color: theme.colors.text, textAlign: 'right', flex: 1 }]}>
                {String(value)}
              </Text>
            </View>
          ))}
        </View>
      ) : null}

      {pending ? (
        <View style={[styles.actions, { borderTopColor: theme.colors.divider }]}>
          {item.canSendBack ? (
            <MotionPressable onPress={onSendBack} haptic="selection" contentStyle={styles.returnButton}>
              <Ionicons name="return-up-back-outline" size={16} color={theme.colors.textSecondary} />
              <Text style={[theme.typography.caption, { color: theme.colors.textSecondary, fontWeight: '700' }]}>Return</Text>
            </MotionPressable>
          ) : <View />}
          <View style={styles.actionRight}>
            {item.canReject ? (
              <MotionPressable
                onPress={onReject}
                haptic="medium"
                contentStyle={[styles.rejectButton, { borderColor: theme.colors.danger }]}
              >
                <Text style={[theme.typography.caption, { color: theme.colors.danger, fontWeight: '700' }]}>Reject</Text>
              </MotionPressable>
            ) : null}
            {item.canApprove ? (
              <MotionPressable
                onPress={onApprove}
                haptic="medium"
                contentStyle={[styles.approveButton, { backgroundColor: theme.colors.success }]}
              >
                <Ionicons name="checkmark" size={16} color="#FFFFFF" />
                <Text style={[theme.typography.caption, { color: '#FFFFFF', fontWeight: '700' }]}>Approve</Text>
              </MotionPressable>
            ) : null}
          </View>
        </View>
      ) : null}
    </GlassSurface>
  );
}

function DecisionModal({
  visible,
  action,
  item,
  comment,
  loading,
  onCommentChange,
  onClose,
  onSubmit,
}: {
  visible: boolean;
  action: ApprovalAction;
  item: ApprovalItem | null;
  comment: string;
  loading: boolean;
  onCommentChange: (value: string) => void;
  onClose: () => void;
  onSubmit: () => void;
}) {
  const { theme } = useTheme();
  const config = action === 'APPROVE'
    ? { title: 'Confirm approval', label: 'Approve request', icon: 'checkmark-circle-outline' as const, color: theme.colors.success }
    : action === 'REJECT'
      ? { title: 'Reject request', label: 'Reject request', icon: 'close-circle-outline' as const, color: theme.colors.danger }
      : { title: 'Return request', label: 'Return for changes', icon: 'return-up-back-outline' as const, color: theme.colors.warning };

  return (
    <Modal visible={visible} transparent animationType="slide" onRequestClose={onClose}>
      <View style={[styles.modalBackdrop, { backgroundColor: theme.colors.overlay }]}>
        <GlassSurface
          radius={theme.radius.xxl}
          style={styles.modalSheet}
          contentStyle={styles.modalContent}
          tintColor={theme.isDark ? 'rgba(10,26,52,0.94)' : 'rgba(255,255,255,0.94)'}
        >
          <View style={[styles.modalHandle, { backgroundColor: theme.colors.border }]} />
          <View style={[styles.modalIcon, { backgroundColor: `${config.color}18` }]}>
            <Ionicons name={config.icon} size={26} color={config.color} />
          </View>
          <Text style={[theme.typography.h2, { color: theme.colors.text, marginTop: 13 }]}>{config.title}</Text>
          <Text style={[theme.typography.caption, { color: theme.colors.textMuted, marginTop: 5 }]}>
            {item?.title ?? 'Selected workflow item'}
          </Text>

          <Text style={[theme.typography.caption, styles.commentLabel, { color: theme.colors.textSecondary }]}>
            {action === 'APPROVE' ? 'Comment (optional)' : 'Reason (required)'}
          </Text>
          <TextInput
            value={comment}
            onChangeText={onCommentChange}
            multiline
            numberOfLines={4}
            textAlignVertical="top"
            placeholder={action === 'APPROVE' ? 'Add context for the audit trail' : 'Explain the decision clearly'}
            placeholderTextColor={theme.colors.textMuted}
            selectionColor={theme.colors.primary}
            style={[
              theme.typography.body,
              styles.commentInput,
              { color: theme.colors.text, backgroundColor: theme.colors.surfaceSoft, borderColor: theme.colors.border },
            ]}
          />

          <View style={styles.modalActions}>
            <MotionPressable
              onPress={onClose}
              haptic="selection"
              style={styles.modalSecondaryShell}
              contentStyle={[styles.modalSecondary, { borderColor: theme.colors.border }]}
            >
              <Text style={[theme.typography.bodyStrong, { color: theme.colors.textSecondary }]}>Cancel</Text>
            </MotionPressable>
            <LiquidButton
              label={config.label}
              icon={config.icon}
              onPress={onSubmit}
              loading={loading}
              disabled={loading}
              variant={action === 'REJECT' ? 'danger' : action === 'APPROVE' ? 'success' : 'primary'}
              style={styles.modalPrimary}
            />
          </View>
        </GlassSurface>
      </View>
    </Modal>
  );
}

function getUrgency(urgency: ApprovalItem['urgency'], theme: ReturnType<typeof useTheme>['theme']) {
  if (urgency === 'HIGH') return { label: 'High', color: theme.colors.danger };
  if (urgency === 'MEDIUM') return { label: 'Medium', color: theme.colors.warning };
  return { label: 'Low', color: theme.colors.success };
}

const styles = StyleSheet.create({
  root: { flex: 1 },
  content: { paddingBottom: 36 },
  section: { paddingHorizontal: 16, marginTop: 16 },
  filterSection: { marginTop: 11 },
  tabContainer: { flexDirection: 'row', padding: 5 },
  tabShell: { flex: 1 },
  tabButton: { minHeight: 48, borderRadius: 16, flexDirection: 'row', alignItems: 'center', justifyContent: 'center', gap: 7 },
  tabCount: { minWidth: 22, height: 22, borderRadius: 11, alignItems: 'center', justifyContent: 'center', paddingHorizontal: 6 },
  tabCountText: { color: '#FFFFFF', fontSize: 10, fontWeight: '800' },
  filterRail: { paddingHorizontal: 16, gap: 8 },
  filterChip: { minHeight: 40, borderRadius: 999, borderWidth: StyleSheet.hairlineWidth, flexDirection: 'row', alignItems: 'center', gap: 6, paddingHorizontal: 13 },
  stateCard: { minHeight: 180, alignItems: 'center', justifyContent: 'center', gap: 12, padding: 24 },
  emptyCard: { minHeight: 230, alignItems: 'center', justifyContent: 'center', gap: 9, padding: 24 },
  emptyIcon: { width: 64, height: 64, borderRadius: 22, alignItems: 'center', justifyContent: 'center', marginBottom: 3 },
  emptyText: { textAlign: 'center', maxWidth: 290 },
  list: { gap: 11 },
  approvalCard: { padding: 16 },
  cardHeader: { flexDirection: 'row', alignItems: 'flex-start', gap: 11 },
  typeIcon: { width: 44, height: 44, borderRadius: 15, alignItems: 'center', justifyContent: 'center' },
  cardHeading: { flex: 1, minWidth: 0 },
  urgencyPill: { paddingHorizontal: 8, paddingVertical: 5, borderRadius: 999 },
  summary: { marginTop: 13 },
  requesterRow: { flexDirection: 'row', alignItems: 'center', gap: 9, marginTop: 13 },
  requesterAvatar: { width: 34, height: 34, borderRadius: 12, alignItems: 'center', justifyContent: 'center' },
  requesterCopy: { flex: 1 },
  detailsPanel: { marginTop: 13, borderRadius: 16, paddingHorizontal: 12 },
  detailRow: { minHeight: 48, flexDirection: 'row', alignItems: 'center', gap: 12, paddingVertical: 8 },
  detailLabel: { textTransform: 'uppercase', letterSpacing: 0.45, maxWidth: '43%' },
  actions: { borderTopWidth: StyleSheet.hairlineWidth, flexDirection: 'row', alignItems: 'center', justifyContent: 'space-between', marginTop: 15, paddingTop: 13 },
  returnButton: { minHeight: 43, flexDirection: 'row', alignItems: 'center', gap: 5, paddingHorizontal: 9, borderRadius: 13 },
  actionRight: { flexDirection: 'row', alignItems: 'center', gap: 8 },
  rejectButton: { minHeight: 43, minWidth: 76, borderRadius: 14, borderWidth: 1.2, alignItems: 'center', justifyContent: 'center', paddingHorizontal: 12 },
  approveButton: { minHeight: 43, minWidth: 94, borderRadius: 14, flexDirection: 'row', alignItems: 'center', justifyContent: 'center', gap: 5, paddingHorizontal: 13 },
  modalBackdrop: { flex: 1, justifyContent: 'flex-end' },
  modalSheet: { borderBottomLeftRadius: 0, borderBottomRightRadius: 0 },
  modalContent: { paddingHorizontal: 20, paddingTop: 12, paddingBottom: 30 },
  modalHandle: { width: 44, height: 5, borderRadius: 999, alignSelf: 'center' },
  modalIcon: { width: 56, height: 56, borderRadius: 20, alignItems: 'center', justifyContent: 'center', marginTop: 17 },
  commentLabel: { marginTop: 21, marginBottom: 7, fontWeight: '700' },
  commentInput: { minHeight: 112, borderRadius: 17, borderWidth: StyleSheet.hairlineWidth, paddingHorizontal: 14, paddingVertical: 12 },
  modalActions: { flexDirection: 'row', gap: 10, marginTop: 17 },
  modalSecondaryShell: { flex: 1 },
  modalSecondary: { minHeight: 56, borderRadius: 18, borderWidth: StyleSheet.hairlineWidth, alignItems: 'center', justifyContent: 'center' },
  modalPrimary: { flex: 1 },
  bottomSpacer: { height: 12 },
});
