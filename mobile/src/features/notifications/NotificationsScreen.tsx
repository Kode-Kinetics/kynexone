import React, { useState, useEffect, useCallback } from 'react';
import {
  View, Text, ScrollView, TouchableOpacity,
  ActivityIndicator, RefreshControl, Alert,
} from 'react-native';
import { notificationsApi } from '@/api/adapters';
import { AppNotification } from '@/types';
import { formatDate } from '@/utils/date';
import { COLORS } from '@/config';

const NOTIF_ICONS: Record<string, string> = {
  LeaveApproved:    '✅',
  LeaveRejected:    '❌',
  OvertimeApproved: '✅',
  OvertimeRejected: '❌',
  PayslipPublished: '💰',
  DocumentExpiry:   '⚠️',
  MissingPunch:     '🕐',
  HRRequestUpdate:  '📋',
  PolicyAck:        '📄',
  ApprovalPending:  '🔔',
  AttendanceAlert:  '📍',
  General:          '📢',
};

function NotifCard({
  notif, onPress, onMarkRead,
}: { notif: AppNotification; onPress: () => void; onMarkRead: () => void }) {
  const icon = NOTIF_ICONS[notif.type] ?? NOTIF_ICONS['General'];
  const timeAgo = formatDate(notif.createdAt, 'relative');

  return (
    <TouchableOpacity
      onPress={onPress}
      style={{
        backgroundColor: notif.isRead ? '#fff' : '#EFF6FF',
        borderRadius: 14, padding: 14, marginBottom: 10,
        flexDirection: 'row', alignItems: 'flex-start', gap: 12,
        shadowColor: '#000', shadowOffset: { width: 0, height: 1 },
        shadowOpacity: notif.isRead ? 0.04 : 0.08, shadowRadius: 4, elevation: 2,
        borderLeftWidth: notif.isRead ? 0 : 3, borderLeftColor: COLORS.blue,
      }}
    >
      {/* Icon */}
      <View style={{
        width: 40, height: 40, borderRadius: 20,
        backgroundColor: notif.isRead ? '#F3F4F6' : '#DBEAFE',
        alignItems: 'center', justifyContent: 'center', flexShrink: 0,
      }}>
        <Text style={{ fontSize: 18 }}>{icon}</Text>
      </View>

      {/* Content */}
      <View style={{ flex: 1 }}>
        <Text style={{
          fontSize: 14, fontWeight: notif.isRead ? '500' : '700',
          color: '#111827', lineHeight: 20,
        }}>
          {notif.title}
        </Text>
        {notif.body ? (
          <Text style={{ fontSize: 13, color: '#6B7280', marginTop: 3, lineHeight: 18 }} numberOfLines={2}>
            {notif.body}
          </Text>
        ) : null}
        <View style={{ flexDirection: 'row', justifyContent: 'space-between', alignItems: 'center', marginTop: 6 }}>
          <Text style={{ fontSize: 11, color: '#9CA3AF' }}>{timeAgo}</Text>
          {!notif.isRead && (
            <TouchableOpacity onPress={onMarkRead}>
              <Text style={{ fontSize: 11, color: COLORS.blue, fontWeight: '600' }}>Mark read</Text>
            </TouchableOpacity>
          )}
        </View>
      </View>

      {/* Unread dot */}
      {!notif.isRead && (
        <View style={{ width: 8, height: 8, borderRadius: 4, backgroundColor: COLORS.blue, marginTop: 4, flexShrink: 0 }} />
      )}
    </TouchableOpacity>
  );
}

export default function NotificationsScreen() {
  const [notifications, setNotifications] = useState<AppNotification[]>([]);
  const [loading, setLoading] = useState(true);
  const [refreshing, setRefreshing] = useState(false);
  const [markingAll, setMarkingAll] = useState(false);

  const fetchNotifications = useCallback(async () => {
    try {
      const data = await notificationsApi.getAll({ page: 1, limit: 50 });
      setNotifications(data.items || []);
    } catch (e: any) {
      Alert.alert('Error', e.message || 'Failed to load notifications');
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => { fetchNotifications(); }, [fetchNotifications]);

  const onRefresh = async () => {
    setRefreshing(true);
    await fetchNotifications();
    setRefreshing(false);
  };

  const markRead = async (id: string) => {
    try {
      await notificationsApi.markRead(id);
      setNotifications((prev) =>
        prev.map((n) => n.id === id ? { ...n, isRead: true } : n)
      );
    } catch {
      // Silent fail
    }
  };

  const markAllRead = async () => {
    setMarkingAll(true);
    try {
      // Mark all unread individually
      const unread = notifications.filter((n) => !n.isRead);
      await Promise.all(unread.map((n) => notificationsApi.markRead(n.id)));
      setNotifications((prev) => prev.map((n) => ({ ...n, isRead: true })));
    } catch (e: any) {
      Alert.alert('Error', e.message || 'Failed to mark all as read');
    } finally {
      setMarkingAll(false);
    }
  };

  const unreadCount = notifications.filter((n) => !n.isRead).length;

  return (
    <View style={{ flex: 1, backgroundColor: COLORS.background }}>
      {/* Header */}
      <View style={{ backgroundColor: COLORS.navy, paddingTop: 56, paddingBottom: 16, paddingHorizontal: 20 }}>
        <View style={{ flexDirection: 'row', justifyContent: 'space-between', alignItems: 'flex-end' }}>
          <View>
            <Text style={{ color: '#fff', fontSize: 22, fontWeight: '700' }}>
              Notifications
              {unreadCount > 0 && (
                <Text style={{ color: COLORS.cyan }}> ({unreadCount})</Text>
              )}
            </Text>
            <Text style={{ color: 'rgba(255,255,255,0.6)', fontSize: 13, marginTop: 2 }}>
              Stay up to date
            </Text>
          </View>
          {unreadCount > 0 && (
            <TouchableOpacity onPress={markAllRead} disabled={markingAll}>
              {markingAll ? (
                <ActivityIndicator color="rgba(255,255,255,0.7)" size="small" />
              ) : (
                <Text style={{ color: 'rgba(255,255,255,0.7)', fontSize: 13 }}>Mark all read</Text>
              )}
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
          {notifications.length === 0 ? (
            <View style={{ alignItems: 'center', marginTop: 60 }}>
              <Text style={{ fontSize: 48 }}>🔔</Text>
              <Text style={{ color: '#374151', fontSize: 16, fontWeight: '600', marginTop: 12 }}>No notifications</Text>
              <Text style={{ color: '#9CA3AF', fontSize: 13, marginTop: 4 }}>You're all caught up!</Text>
            </View>
          ) : (
            <>
              {unreadCount > 0 && (
                <Text style={{ fontSize: 12, fontWeight: '700', color: '#374151', marginBottom: 10, textTransform: 'uppercase' }}>
                  Unread · {unreadCount}
                </Text>
              )}
              {notifications.filter((n) => !n.isRead).map((n) => (
                <NotifCard
                  key={n.id}
                  notif={n}
                  onPress={() => markRead(n.id)}
                  onMarkRead={() => markRead(n.id)}
                />
              ))}

              {notifications.filter((n) => n.isRead).length > 0 && (
                <>
                  <Text style={{ fontSize: 12, fontWeight: '700', color: '#9CA3AF', marginTop: 8, marginBottom: 10, textTransform: 'uppercase' }}>
                    Earlier
                  </Text>
                  {notifications.filter((n) => n.isRead).map((n) => (
                    <NotifCard
                      key={n.id}
                      notif={n}
                      onPress={() => {}}
                      onMarkRead={() => {}}
                    />
                  ))}
                </>
              )}
            </>
          )}
        </ScrollView>
      )}
    </View>
  );
}
