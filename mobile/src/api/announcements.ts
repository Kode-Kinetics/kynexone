// Company announcements for the signed-in employee. Owned by lane A1.
//
// GET /api/ess/announcements → active, unexpired EmployeeAnnouncement rows, newest first:
// [{ id, title, body, audience, publishedAtUtc }]. The ESS dashboard carries the latest five in
// `announcements`; this is the full list behind "See all".

import { apiGet } from './client';
import { mapAnnouncements, type Announcement } from './homeMappers';

export type { Announcement } from './homeMappers';

export const announcementsApi = {
  async getAll(): Promise<Announcement[]> {
    return mapAnnouncements(await apiGet<unknown>('/ess/announcements'));
  },
};
