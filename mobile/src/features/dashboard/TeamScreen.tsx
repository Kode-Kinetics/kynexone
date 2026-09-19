import React, { useCallback, useEffect, useMemo, useState } from 'react';
import {
  ActivityIndicator,
  Alert,
  RefreshControl,
  ScrollView,
  StyleSheet,
  Text,
  TextInput,
  View,
} from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import { teamApi } from '@/api/adapters';
import { formatDate, toISODate } from '@/utils/date';
import { useTheme } from '@/theme/ThemeProvider';
import {
  GlassSurface,
  LiquidBackdrop,
  MotionPressable,
  ScreenHero,
  SectionHeader,
} from '@/components/ui';
import type { TeamMember } from '@/types';

const statusFilters: { key: string; label: string; stat?: 'present' | 'absent' | 'late' | 'onLeave' }[] = [
  { key: 'ALL', label: 'All' },
  { key: 'PRESENT', label: 'Present', stat: 'present' },
  { key: 'ABSENT', label: 'Absent', stat: 'absent' },
  { key: 'LATE', label: 'Late', stat: 'late' },
  { key: 'ON_LEAVE', label: 'On leave', stat: 'onLeave' },
];

export default function TeamScreen() {
  const { theme } = useTheme();
  const [members, setMembers] = useState<TeamMember[]>([]);
  const [loading, setLoading] = useState(true);
  const [refreshing, setRefreshing] = useState(false);
  const [filter, setFilter] = useState('ALL');
  const [search, setSearch] = useState('');

  const fetchTeam = useCallback(async () => {
    try {
      const data = await teamApi.getTeam({ date: toISODate(new Date()) });
      setMembers(data.items || []);
    } catch (error: any) {
      Alert.alert('Team unavailable', error.message || 'Failed to load your team.');
    } finally {
      setLoading(false);
      setRefreshing(false);
    }
  }, []);

  useEffect(() => {
    void fetchTeam();
  }, [fetchTeam]);

  const stats = useMemo(() => ({
    total: members.length,
    present: members.filter((member) => member.todayStatus === 'PRESENT').length,
    absent: members.filter((member) => member.todayStatus === 'ABSENT').length,
    late: members.filter((member) => member.todayStatus === 'LATE').length,
    onLeave: members.filter((member) => member.todayStatus === 'ON_LEAVE').length,
  }), [members]);

  const filtered = useMemo(() => members.filter((member) => {
    const matchesStatus = filter === 'ALL' || member.todayStatus === filter;
    const normalized = search.trim().toLowerCase();
    const matchesSearch = !normalized
      || member.fullName.toLowerCase().includes(normalized)
      || member.department?.toLowerCase().includes(normalized)
      || member.jobTitle?.toLowerCase().includes(normalized);
    return matchesStatus && matchesSearch;
  }), [filter, members, search]);

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
              void fetchTeam();
            }}
            tintColor={theme.colors.primary}
            colors={[theme.colors.primary]}
          />
        }
        showsVerticalScrollIndicator={false}
        keyboardShouldPersistTaps="handled"
      >
        <ScreenHero
          eyebrow="Manager workspace"
          title="My team"
          subtitle={`${formatDate(new Date().toISOString(), 'display')} · ${stats.total} direct report${stats.total === 1 ? '' : 's'}`}
        />

        {!loading ? (
          <View style={styles.section}>
            <ScrollView horizontal showsHorizontalScrollIndicator={false} contentContainerStyle={styles.statsRail}>
              <TeamStat label="Total" value={stats.total} icon="people-outline" accent={theme.colors.primary} />
              <TeamStat label="Present" value={stats.present} icon="checkmark-circle-outline" accent={theme.colors.success} />
              <TeamStat label="Absent" value={stats.absent} icon="close-circle-outline" accent={theme.colors.danger} />
              <TeamStat label="Late" value={stats.late} icon="time-outline" accent={theme.colors.warning} />
              <TeamStat label="Leave" value={stats.onLeave} icon="airplane-outline" accent={theme.colors.violet} />
            </ScrollView>
          </View>
        ) : null}

        <View style={styles.section}>
          <GlassSurface elevated={false} radius={theme.radius.xl} contentStyle={styles.searchCard}>
            <Ionicons name="search-outline" size={20} color={theme.colors.textMuted} />
            <TextInput
              value={search}
              onChangeText={setSearch}
              placeholder="Search name, role or department"
              placeholderTextColor={theme.colors.textMuted}
              selectionColor={theme.colors.primary}
              autoCorrect={false}
              style={[theme.typography.body, styles.searchInput, { color: theme.colors.text }]}
            />
            {search ? (
              <MotionPressable
                onPress={() => setSearch('')}
                haptic="selection"
                contentStyle={styles.clearSearch}
                accessibilityLabel="Clear search"
              >
                <Ionicons name="close-circle" size={19} color={theme.colors.textMuted} />
              </MotionPressable>
            ) : null}
          </GlassSurface>
        </View>

        <View style={styles.sectionCompact}>
          <ScrollView horizontal showsHorizontalScrollIndicator={false} contentContainerStyle={styles.filterRail}>
            {statusFilters.map((item) => {
              const selected = filter === item.key;
              return (
                <MotionPressable
                  key={item.key}
                  onPress={() => setFilter(item.key)}
                  haptic="selection"
                  contentStyle={[
                    styles.filterChip,
                    {
                      backgroundColor: selected ? `${theme.colors.primary}22` : theme.colors.surface,
                      borderColor: selected ? theme.colors.primary : theme.colors.border,
                    },
                  ]}
                  accessibilityRole="radio"
                  accessibilityState={{ selected }}
                >
                  <Text
                    style={[
                      theme.typography.caption,
                      { color: selected ? theme.colors.primary : theme.colors.textSecondary, fontWeight: selected ? '700' : '500' },
                    ]}
                  >
                    {item.label}{item.stat ? ` · ${stats[item.stat]}` : ''}
                  </Text>
                </MotionPressable>
              );
            })}
          </ScrollView>
        </View>

        <View style={styles.section}>
          <SectionHeader title="Team status" subtitle={`${filtered.length} matching employee${filtered.length === 1 ? '' : 's'}`} />
          {loading ? (
            <GlassSurface radius={theme.radius.xl} contentStyle={styles.stateCard}>
              <ActivityIndicator color={theme.colors.primary} />
              <Text style={[theme.typography.caption, { color: theme.colors.textSecondary }]}>Loading team…</Text>
            </GlassSurface>
          ) : filtered.length === 0 ? (
            <GlassSurface radius={theme.radius.xl} contentStyle={styles.emptyCard}>
              <View style={[styles.emptyIcon, { backgroundColor: `${theme.colors.primary}18` }]}>
                <Ionicons name="people-outline" size={30} color={theme.colors.primary} />
              </View>
              <Text style={[theme.typography.h3, { color: theme.colors.text }]}>No team members found</Text>
              <Text style={[theme.typography.caption, styles.emptyText, { color: theme.colors.textMuted }]}>
                Try another status filter or clear the search.
              </Text>
            </GlassSurface>
          ) : (
            <View style={styles.list}>
              {filtered.map((member) => <MemberCard key={member.employeeId} member={member} />)}
            </View>
          )}
        </View>
        <View style={styles.bottomSpacer} />
      </ScrollView>
    </View>
  );
}

function TeamStat({
  label,
  value,
  icon,
  accent,
}: {
  label: string;
  value: number;
  icon: React.ComponentProps<typeof Ionicons>['name'];
  accent: string;
}) {
  const { theme } = useTheme();
  return (
    <GlassSurface elevated={false} radius={theme.radius.xl} style={styles.statCard} contentStyle={styles.statContent}>
      <View style={[styles.statIcon, { backgroundColor: `${accent}18` }]}>
        <Ionicons name={icon} size={18} color={accent} />
      </View>
      <Text style={[styles.statValue, { color: theme.colors.text }]}>{value}</Text>
      <Text style={[theme.typography.micro, { color: theme.colors.textMuted }]}>{label}</Text>
    </GlassSurface>
  );
}

function MemberCard({ member }: { member: TeamMember }) {
  const { theme } = useTheme();
  const meta = getStatusMeta(member.todayStatus ?? 'UNKNOWN', theme);
  const initials = member.fullName
    .split(' ')
    .map((part) => part[0])
    .join('')
    .slice(0, 2)
    .toUpperCase();

  return (
    <GlassSurface elevated={false} radius={theme.radius.xl} contentStyle={styles.memberCard}>
      <View style={[styles.avatar, { backgroundColor: `${theme.colors.primary}20` }]}>
        <Text style={[styles.initials, { color: theme.colors.primary }]}>{initials}</Text>
      </View>
      <View style={styles.memberCopy}>
        <Text style={[theme.typography.bodyStrong, { color: theme.colors.text }]}>{member.fullName}</Text>
        <Text numberOfLines={1} style={[theme.typography.caption, { color: theme.colors.textSecondary, marginTop: 2 }]}>
          {member.jobTitle ?? member.department ?? 'Employee'}
        </Text>
        {member.clockIn ? (
          <View style={styles.punchLine}>
            <Ionicons name="log-in-outline" size={13} color={theme.colors.success} />
            <Text style={[theme.typography.micro, { color: theme.colors.textMuted }]}>
              {formatDate(member.clockIn, 'time')}
              {member.clockOut ? ` – ${formatDate(member.clockOut, 'time')}` : ' · Active'}
            </Text>
          </View>
        ) : null}
      </View>
      <View style={[styles.statusPill, { backgroundColor: `${meta.color}18` }]}>
        <View style={[styles.statusDot, { backgroundColor: meta.color }]} />
        <Text style={[theme.typography.micro, { color: meta.color }]}>{meta.label}</Text>
      </View>
    </GlassSurface>
  );
}

function getStatusMeta(status: string, theme: ReturnType<typeof useTheme>['theme']) {
  const map: Record<string, { label: string; color: string }> = {
    PRESENT: { label: 'Present', color: theme.colors.success },
    ABSENT: { label: 'Absent', color: theme.colors.danger },
    LATE: { label: 'Late', color: theme.colors.warning },
    ON_LEAVE: { label: 'On leave', color: theme.colors.primary },
    HALF_DAY: { label: 'Half day', color: theme.colors.warning },
    MISSING_PUNCH: { label: 'Missing punch', color: theme.colors.danger },
    HOLIDAY: { label: 'Holiday', color: theme.colors.violet },
    WEEKEND: { label: 'Rest day', color: theme.colors.textMuted },
  };
  return map[status] ?? { label: 'No record', color: theme.colors.textMuted };
}

const styles = StyleSheet.create({
  root: { flex: 1 },
  content: { paddingBottom: 34 },
  section: { paddingHorizontal: 16, marginTop: 16 },
  sectionCompact: { marginTop: 10 },
  statsRail: { gap: 9, paddingRight: 4 },
  statCard: { width: 106, minHeight: 122 },
  statContent: { padding: 13, justifyContent: 'space-between' },
  statIcon: { width: 37, height: 37, borderRadius: 13, alignItems: 'center', justifyContent: 'center' },
  statValue: { fontSize: 25, lineHeight: 29, fontWeight: '800', marginTop: 7 },
  searchCard: { minHeight: 56, flexDirection: 'row', alignItems: 'center', gap: 10, paddingHorizontal: 14 },
  searchInput: { flex: 1, minHeight: 52 },
  clearSearch: { width: 40, height: 40, borderRadius: 14, alignItems: 'center', justifyContent: 'center' },
  filterRail: { paddingHorizontal: 16, gap: 8 },
  filterChip: { minHeight: 39, borderRadius: 999, borderWidth: StyleSheet.hairlineWidth, justifyContent: 'center', paddingHorizontal: 14 },
  list: { gap: 9 },
  memberCard: { minHeight: 86, flexDirection: 'row', alignItems: 'center', gap: 12, padding: 13 },
  avatar: { width: 48, height: 48, borderRadius: 17, alignItems: 'center', justifyContent: 'center' },
  initials: { fontSize: 15, fontWeight: '800', letterSpacing: 0.2 },
  memberCopy: { flex: 1, minWidth: 0 },
  punchLine: { flexDirection: 'row', alignItems: 'center', gap: 4, marginTop: 5 },
  statusPill: { flexDirection: 'row', alignItems: 'center', gap: 5, paddingHorizontal: 8, paddingVertical: 6, borderRadius: 999 },
  statusDot: { width: 6, height: 6, borderRadius: 3 },
  stateCard: { minHeight: 180, alignItems: 'center', justifyContent: 'center', gap: 12, padding: 22 },
  emptyCard: { minHeight: 220, alignItems: 'center', justifyContent: 'center', gap: 9, padding: 24 },
  emptyIcon: { width: 60, height: 60, borderRadius: 21, alignItems: 'center', justifyContent: 'center', marginBottom: 3 },
  emptyText: { textAlign: 'center', maxWidth: 280 },
  bottomSpacer: { height: 12 },
});
