// Notifications: tapping one marks it read AND opens the record it is about. Rows the app cannot
// route (no recognisable subject) are not presented as buttons; unread ones offer "Mark read".

import React, { useCallback, useEffect, useMemo, useState } from 'react';
import { Alert, RefreshControl, ScrollView, StyleSheet, Text, View } from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import { useTranslation } from 'react-i18next';
import { notificationsApi } from '@/api/adapters';
import { useAuthStore } from '@/auth/authStore';
import { navigateTo, type AppRoute } from '@/navigation/routes';
import { useTheme } from '@/theme/ThemeProvider';
import { useFormat } from '@/utils/format';
import { haptics } from '@/utils/haptics';
import {
  DirectionalIcon,
  LiquidBackdrop,
  MotionPressable,
  ScreenHero,
  SectionHeader,
  SkeletonRow,
  StateCard,
  Surface,
} from '@/components/ui';
import type { AppNotification } from '@/types';
import { notificationTarget, type NotificationTarget } from './notificationTarget';

type IconName = React.ComponentProps<typeof Ionicons>['name'];

const TARGET_ICON: Record<NotificationTarget['route'], IconName> = {
  ApplyLeave: 'calendar-outline',
  Payslips: 'wallet-outline',
  Overtime: 'time-outline',
  HRRequests: 'chatbubble-ellipses-outline',
  Letters: 'document-text-outline',
  Documents: 'folder-open-outline',
  AttendanceHistory: 'finger-print-outline',
  Profile: 'person-circle-outline',
};

export default function NotificationsScreen({ navigation }: { navigation: any }) {
  const { theme } = useTheme();
  const { t, i18n } = useTranslation();
  const f = useFormat();
  const { user } = useAuthStore();
  const [notifications, setNotifications] = useState<AppNotification[] | null>(null);
  const [failed, setFailed] = useState(false);
  const [refreshing, setRefreshing] = useState(false);
  const [markingAll, setMarkingAll] = useState(false);
  const arabic = i18n.language?.startsWith('ar');

  const fetchNotifications = useCallback(async () => {
    setFailed(false);
    try {
      const data = await notificationsApi.getAll({ page: 1, limit: 50 });
      setNotifications(data.items || []);
    } catch (error) {
      console.warn('[Notifications] Load failed:', error);
      setFailed(true);
    } finally {
      setRefreshing(false);
    }
  }, []);

  useEffect(() => {
    void fetchNotifications();
  }, [fetchNotifications]);

  const unread = useMemo(() => (notifications ?? []).filter((n) => !n.isRead), [notifications]);
  const earlier = useMemo(() => (notifications ?? []).filter((n) => n.isRead), [notifications]);

  const markRead = async (id: string) => {
    setNotifications((items) => items?.map((item) => (item.id === id ? { ...item, isRead: true } : item)) ?? items);
    try {
      await notificationsApi.markRead(id);
    } catch {
      setNotifications((items) => items?.map((item) => (item.id === id ? { ...item, isRead: false } : item)) ?? items);
    }
  };

  const open = (notification: AppNotification, target: NotificationTarget) => {
    if (!notification.isRead) void markRead(notification.id);
    navigateTo(navigation, target.route as AppRoute, user, 'params' in target ? target.params : undefined);
  };

  const markAllRead = async () => {
    setMarkingAll(true);
    try {
      await Promise.all(unread.map((notification) => notificationsApi.markRead(notification.id)));
      setNotifications((items) => items?.map((item) => ({ ...item, isRead: true })) ?? items);
      haptics.success();
    } catch {
      haptics.error();
      Alert.alert(t('home.notifications.readAllFailedTitle'), t('home.notifications.readAllFailedBody'));
    } finally {
      setMarkingAll(false);
    }
  };

  const renderCard = (n: AppNotification) => {
    const title = (arabic && n.titleAr) || n.title;
    const body = (arabic && n.bodyAr) || n.body;
    return (
      <NotificationCard
        key={n.id}
        notification={n}
        title={title}
        body={body}
        when={f.relative(n.createdAt)}
        target={notificationTarget(n.title, n.body)}
        onOpen={(target) => open(n, target)}
        onMarkRead={() => void markRead(n.id)}
      />
    );
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
            tintColor={theme.colors.accent}
            colors={[theme.colors.accent]}
          />
        }
        showsVerticalScrollIndicator={false}
      >
        <ScreenHero
          eyebrow={t('home.notifications.eyebrow')}
          title={t('home.notifications.title')}
          subtitle={notifications
            ? unread.length
              ? t('home.notifications.subtitleUnread', { value: f.number(unread.length) })
              : t('home.notifications.subtitleClear')
            : undefined}
          actions={
            unread.length ? (
              <MotionPressable
                onPress={() => void markAllRead()}
                disabled={markingAll}
                accessibilityRole="button"
                accessibilityLabel={t('home.notifications.readAll')}
                contentStyle={styles.markAllButton}
              >
                <Ionicons accessible={false} name="checkmark-done" size={18} color={theme.colors.accent} />
                <Text style={[theme.typography.subheadline, styles.strong, { color: theme.colors.accent }]}>
                  {t('home.notifications.readAll')}
                </Text>
              </MotionPressable>
            ) : undefined
          }
        />

        {failed && !notifications ? (
          <View style={styles.section}>
            <StateCard variant="error" title={t('home.notifications.loadErrorTitle')} onRetry={() => void fetchNotifications()} />
          </View>
        ) : !notifications ? (
          <View style={styles.section}>
            <Surface contentStyle={styles.listCard}>
              <SkeletonRow />
              <SkeletonRow />
              <SkeletonRow />
            </Surface>
          </View>
        ) : notifications.length === 0 ? (
          <View style={styles.section}>
            <StateCard variant="empty" title={t('home.notifications.emptyTitle')} body={t('home.notifications.emptyBody')} />
          </View>
        ) : (
          <View style={styles.stack}>
            {unread.length ? (
              <View style={styles.section}>
                <SectionHeader title={t('home.notifications.unread')} />
                <View style={styles.list}>{unread.map(renderCard)}</View>
              </View>
            ) : null}
            {earlier.length ? (
              <View style={styles.section}>
                <SectionHeader title={t('home.notifications.earlier')} />
                <View style={styles.list}>{earlier.map(renderCard)}</View>
              </View>
            ) : null}
          </View>
        )}
      </ScrollView>
    </View>
  );
}

function NotificationCard({
  notification,
  title,
  body,
  when,
  target,
  onOpen,
  onMarkRead,
}: {
  notification: AppNotification;
  title: string;
  body?: string;
  when: string;
  target: NotificationTarget | null;
  onOpen: (target: NotificationTarget) => void;
  onMarkRead: () => void;
}) {
  const { theme } = useTheme();
  const { t } = useTranslation();
  const unread = !notification.isRead;
  const icon: IconName = target ? TARGET_ICON[target.route] : 'notifications-outline';

  const content = (
    <>
      <View style={[styles.icon, { backgroundColor: unread ? theme.colors.status.info.tint : theme.colors.surfaceInset }]}>
        <Ionicons accessible={false} name={icon} size={20} color={unread ? theme.colors.accent : theme.colors.textMuted} />
      </View>
      <View style={styles.flex}>
        <View style={styles.titleRow}>
          <Text numberOfLines={2} style={[theme.typography.headline, styles.flex, { color: theme.colors.text }]}>{title}</Text>
          {unread ? <View style={[styles.unreadDot, { backgroundColor: theme.colors.accent }]} /> : null}
        </View>
        {body ? (
          <Text numberOfLines={3} style={[theme.typography.subheadline, { color: theme.colors.textSecondary }]}>{body}</Text>
        ) : null}
        <Text style={[theme.typography.footnote, styles.meta, { color: theme.colors.textMuted }]}>{when}</Text>
      </View>
    </>
  );

  const spoken = [unread ? t('home.notifications.unreadA11y') : null, title, body, when].filter(Boolean).join('. ');

  if (target) {
    return (
      <MotionPressable
        onPress={() => onOpen(target)}
        accessibilityRole="button"
        accessibilityLabel={spoken}
        accessibilityHint={t('home.notifications.openA11y')}
      >
        <Surface level={unread ? 1 : 0} contentStyle={styles.card}>
          {content}
          <DirectionalIcon name="chevron" size={16} color={theme.colors.textMuted} />
        </Surface>
      </MotionPressable>
    );
  }

  return (
    <Surface level={unread ? 1 : 0} contentStyle={styles.cardColumn}>
      <View style={styles.cardRow} accessible accessibilityLabel={spoken}>
        {content}
      </View>
      {unread ? (
        <MotionPressable
          onPress={onMarkRead}
          accessibilityRole="button"
          accessibilityLabel={t('home.notifications.markRead')}
          contentStyle={styles.markRead}
        >
          <Text style={[theme.typography.subheadline, styles.strong, { color: theme.colors.accent }]}>
            {t('home.notifications.markRead')}
          </Text>
        </MotionPressable>
      ) : null}
    </Surface>
  );
}

const styles = StyleSheet.create({
  root: { flex: 1 },
  content: { paddingBottom: 24 },
  stack: { gap: 24 },
  section: { paddingHorizontal: 16 },
  flex: { flex: 1, minWidth: 0 },
  strong: { fontWeight: '600' },
  list: { gap: 10 },
  listCard: { paddingHorizontal: 14 },
  markAllButton: { minHeight: 44, flexDirection: 'row', alignItems: 'center', gap: 6, paddingHorizontal: 8 },
  card: { flexDirection: 'row', alignItems: 'flex-start', gap: 12, padding: 14 },
  cardColumn: { padding: 14, gap: 4 },
  cardRow: { flexDirection: 'row', alignItems: 'flex-start', gap: 12 },
  icon: { width: 40, height: 40, borderRadius: 12, alignItems: 'center', justifyContent: 'center' },
  titleRow: { flexDirection: 'row', alignItems: 'flex-start', gap: 8 },
  unreadDot: { width: 8, height: 8, borderRadius: 4, marginTop: 7 },
  meta: { marginTop: 6 },
  markRead: { minHeight: 44, alignSelf: 'flex-end', justifyContent: 'center', paddingHorizontal: 8 },
});
