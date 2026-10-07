import React, { useCallback, useEffect, useMemo, useState } from 'react';
import {
  ActivityIndicator,
  Alert,
  RefreshControl,
  ScrollView,
  StyleSheet,
  Text,
  View,
} from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import { notificationsApi } from '@/api/adapters';
import { formatDate } from '@/utils/date';
import { useTheme } from '@/theme/ThemeProvider';
import {
  GlassSurface,
  LiquidBackdrop,
  MotionPressable,
  ScreenHero,
  SectionHeader,
} from '@/components/ui';
import type { AppNotification } from '@/types';

const notificationMeta: Record<
  string,
  { icon: React.ComponentProps<typeof Ionicons>['name']; tone: 'success' | 'danger' | 'warning' | 'primary' | 'violet' }
> = {
  LeaveApproved: { icon: 'checkmark-circle-outline', tone: 'success' },
  LeaveRejected: { icon: 'close-circle-outline', tone: 'danger' },
  OvertimeApproved: { icon: 'checkmark-circle-outline', tone: 'success' },
  OvertimeRejected: { icon: 'close-circle-outline', tone: 'danger' },
  PayslipPublished: { icon: 'wallet-outline', tone: 'success' },
  DocumentExpiry: { icon: 'warning-outline', tone: 'warning' },
  MissingPunch: { icon: 'time-outline', tone: 'danger' },
  HRRequestUpdate: { icon: 'chatbubble-ellipses-outline', tone: 'primary' },
  PolicyAck: { icon: 'document-text-outline', tone: 'violet' },
  ApprovalPending: { icon: 'checkmark-done-outline', tone: 'warning' },
  AttendanceAlert: { icon: 'location-outline', tone: 'warning' },
  General: { icon: 'notifications-outline', tone: 'primary' },
};

export default function NotificationsScreen() {
  const { theme } = useTheme();
  const [notifications, setNotifications] = useState<AppNotification[]>([]);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [refreshing, setRefreshing] = useState(false);
  const [markingAll, setMarkingAll] = useState(false);

  const fetchNotifications = useCallback(async () => {
    setLoadError(null);
    try {
      const data = await notificationsApi.getAll({ page: 1, limit: 50 });
      setNotifications(data.items || []);
    } catch (error: any) {
      setLoadError(error.message || 'Failed to load notifications.');
    } finally {
      setLoading(false);
      setRefreshing(false);
    }
  }, []);

  useEffect(() => {
    void fetchNotifications();
  }, [fetchNotifications]);

  const unread = useMemo(
    () => notifications.filter((notification) => !notification.isRead),
    [notifications],
  );
  const earlier = useMemo(
    () => notifications.filter((notification) => notification.isRead),
    [notifications],
  );

  const markRead = async (id: string) => {
    const current = notifications;
    setNotifications((items) => items.map((item) => (item.id === id ? { ...item, isRead: true } : item)));
    try {
      await notificationsApi.markRead(id);
    } catch {
      setNotifications(current);
    }
  };

  const markAllRead = async () => {
    setMarkingAll(true);
    try {
      await Promise.all(unread.map((notification) => notificationsApi.markRead(notification.id)));
      setNotifications((items) => items.map((item) => ({ ...item, isRead: true })));
    } catch (error: any) {
      Alert.alert('Could not update notifications', error.message || 'Please try again.');
    } finally {
      setMarkingAll(false);
    }
  };

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
              void fetchNotifications();
            }}
            tintColor={theme.colors.primary}
            colors={[theme.colors.primary]}
          />
        }
        showsVerticalScrollIndicator={false}
      >
        <ScreenHero
          eyebrow="Inbox"
          title="Notifications"
          subtitle={unread.length ? `${unread.length} update${unread.length === 1 ? '' : 's'} need your attention` : 'You are fully caught up'}
          actions={
            unread.length ? (
              <MotionPressable
                onPress={() => void markAllRead()}
                disabled={markingAll}
                haptic="selection"
                contentStyle={styles.markAllButton}
              >
                {markingAll ? (
                  <ActivityIndicator size="small" color={theme.colors.primary} />
                ) : (
                  <>
                    <Ionicons name="checkmark-done" size={18} color={theme.colors.primary} />
                    <Text style={[theme.typography.caption, { color: theme.colors.primary, fontWeight: '700' }]}>Read all</Text>
                  </>
                )}
              </MotionPressable>
            ) : undefined
          }
        />

        {loading ? (
          <View style={styles.section}>
            <GlassSurface radius={theme.radius.xl} contentStyle={styles.stateCard}>
              <ActivityIndicator color={theme.colors.primary} />
              <Text style={[theme.typography.caption, { color: theme.colors.textSecondary }]}>Loading updates…</Text>
            </GlassSurface>
          </View>
        ) : loadError ? (
          <View style={styles.section}>
            <GlassSurface radius={theme.radius.xl} contentStyle={styles.stateCard}>
              <Ionicons name="cloud-offline-outline" size={28} color={theme.colors.danger} />
              <Text style={[theme.typography.h3, { color: theme.colors.text }]}>Notifications unavailable</Text>
              <Text style={[theme.typography.caption, { color: theme.colors.textSecondary, textAlign: 'center' }]}>{loadError}</Text>
              <MotionPressable onPress={() => void fetchNotifications()} contentStyle={[styles.retryButton, { backgroundColor: theme.colors.primary }]}>
                <Text style={[theme.typography.bodyStrong, { color: '#FFFFFF' }]}>Try again</Text>
              </MotionPressable>
            </GlassSurface>
          </View>
        ) : notifications.length === 0 ? (
          <View style={styles.section}>
            <EmptyNotifications />
          </View>
        ) : (
          <>
            {unread.length ? (
              <NotificationGroup title="Needs attention" subtitle={`${unread.length} unread`}>
                {unread.map((notification) => (
                  <NotificationCard key={notification.id} notification={notification} onPress={() => void markRead(notification.id)} />
                ))}
              </NotificationGroup>
            ) : null}
            {earlier.length ? (
              <NotificationGroup title="Earlier" subtitle="Previously viewed">
                {earlier.map((notification) => (
                  <NotificationCard key={notification.id} notification={notification} onPress={() => undefined} />
                ))}
              </NotificationGroup>
            ) : null}
          </>
        )}
        <View style={styles.bottomSpacer} />
      </ScrollView>
    </View>
  );
}

function NotificationGroup({
  title,
  subtitle,
  children,
}: {
  title: string;
  subtitle: string;
  children: React.ReactNode;
}) {
  return (
    <View style={styles.section}>
      <SectionHeader title={title} subtitle={subtitle} />
      <View style={styles.list}>{children}</View>
    </View>
  );
}

function NotificationCard({
  notification,
  onPress,
}: {
  notification: AppNotification;
  onPress: () => void;
}) {
  const { theme } = useTheme();
  const meta = notificationMeta[notification.type] ?? notificationMeta.General;
  const tone = theme.colors[meta.tone];

  return (
    <MotionPressable
      onPress={onPress}
      haptic="selection"
      contentStyle={styles.rounded}
      accessibilityRole="button"
      accessibilityLabel={`${notification.title}. ${notification.body ?? ''}`}
    >
      <GlassSurface
        elevated={!notification.isRead}
        radius={theme.radius.xl}
        tintColor={!notification.isRead ? `${tone}16` : undefined}
        contentStyle={styles.notificationCard}
      >
        <View style={[styles.notificationIcon, { backgroundColor: `${tone}18` }]}>
          <Ionicons name={meta.icon} size={22} color={tone} />
        </View>
        <View style={styles.notificationCopy}>
          <View style={styles.notificationTitleRow}>
            <Text
              numberOfLines={2}
              style={[
                theme.typography.bodyStrong,
                { color: theme.colors.text, flex: 1, fontWeight: notification.isRead ? '600' : '700' },
              ]}
            >
              {notification.title}
            </Text>
            {!notification.isRead ? <View style={[styles.unreadDot, { backgroundColor: tone }]} /> : null}
          </View>
          {notification.body ? (
            <Text numberOfLines={3} style={[theme.typography.caption, { color: theme.colors.textSecondary, marginTop: 4 }]}>
              {notification.body}
            </Text>
          ) : null}
          <View style={styles.notificationMeta}>
            <Text style={[theme.typography.micro, { color: theme.colors.textMuted }]}>
              {formatDate(notification.createdAt, 'relative')}
            </Text>
            {!notification.isRead ? (
              <Text style={[theme.typography.micro, { color: theme.colors.primary, fontWeight: '700' }]}>Tap to mark read</Text>
            ) : null}
          </View>
        </View>
      </GlassSurface>
    </MotionPressable>
  );
}

function EmptyNotifications() {
  const { theme } = useTheme();
  return (
    <GlassSurface radius={theme.radius.xl} contentStyle={styles.emptyCard}>
      <View style={[styles.emptyIcon, { backgroundColor: `${theme.colors.success}18` }]}>
        <Ionicons name="checkmark-done-circle-outline" size={32} color={theme.colors.success} />
      </View>
      <Text style={[theme.typography.h3, { color: theme.colors.text }]}>All caught up</Text>
      <Text style={[theme.typography.caption, styles.emptyText, { color: theme.colors.textMuted }]}>
        Workflow alerts, payslip notices and HR updates will appear here.
      </Text>
    </GlassSurface>
  );
}

const styles = StyleSheet.create({
  root: { flex: 1 },
  content: { paddingBottom: 34 },
  markAllButton: { minHeight: 44, flexDirection: 'row', alignItems: 'center', gap: 6, paddingHorizontal: 10, borderRadius: 14 },
  section: { paddingHorizontal: 16, marginTop: 16 },
  list: { gap: 9 },
  rounded: { borderRadius: 24 },
  stateCard: { minHeight: 170, alignItems: 'center', justifyContent: 'center', gap: 12, padding: 22 },
  retryButton: { minHeight: 44, paddingHorizontal: 20, borderRadius: 16, alignItems: 'center', justifyContent: 'center' },
  notificationCard: { minHeight: 94, flexDirection: 'row', alignItems: 'flex-start', gap: 13, padding: 14 },
  notificationIcon: { width: 46, height: 46, borderRadius: 16, alignItems: 'center', justifyContent: 'center' },
  notificationCopy: { flex: 1, minWidth: 0 },
  notificationTitleRow: { flexDirection: 'row', alignItems: 'flex-start', gap: 8 },
  unreadDot: { width: 8, height: 8, borderRadius: 4, marginTop: 6 },
  notificationMeta: { flexDirection: 'row', justifyContent: 'space-between', gap: 10, marginTop: 9 },
  emptyCard: { minHeight: 220, alignItems: 'center', justifyContent: 'center', gap: 9, padding: 24 },
  emptyIcon: { width: 64, height: 64, borderRadius: 22, alignItems: 'center', justifyContent: 'center', marginBottom: 4 },
  emptyText: { textAlign: 'center', maxWidth: 290 },
  bottomSpacer: { height: 12 },
});
