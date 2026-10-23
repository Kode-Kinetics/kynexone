import client from './client';

export interface NotificationItem {
  id: string;
  title: string;
  message: string;
  entityName: string;
  entityId: string | null;
  /** Null for a workspace-wide notice. Only a notice addressed to you can be marked read or dismissed. */
  userId?: string | null;
  status: string;
  channel: string;
  createdAtUtc: string;
  readAtUtc: string | null;
}

export const notificationsApi = {
  list: () => client.get<NotificationItem[]>('/api/notifications').then((r) => r.data),
  markRead: (id: string) => client.post(`/api/notifications/${id}/read`),
  markAllRead: () => client.post('/api/notifications/read-all'),
  dismiss: (id: string) => client.delete(`/api/notifications/${id}`),
};
