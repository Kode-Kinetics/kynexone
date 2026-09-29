import React, { useState, useEffect, useCallback } from 'react';
import {
  View, Text, ScrollView, TouchableOpacity,
  ActivityIndicator, Alert, RefreshControl, TextInput,
} from 'react-native';
import { teamApi } from '@/api/adapters';
import { TeamMember } from '@/types';
import { formatDate, toISODate } from '@/utils/date';
import { COLORS } from '@/config';

// Keys are the normalised AttendanceStatus values the API layer returns
// (the old PascalCase keys never matched, so every chip rendered as Unknown).
const STATUS_FILTERS: { key: string; label: string; stat?: 'present' | 'absent' | 'late' | 'onLeave' }[] = [
  { key: 'All', label: 'All' },
  { key: 'PRESENT', label: 'Present', stat: 'present' },
  { key: 'ABSENT', label: 'Absent', stat: 'absent' },
  { key: 'LATE', label: 'Late', stat: 'late' },
  { key: 'ON_LEAVE', label: 'On Leave', stat: 'onLeave' },
];
const STATUS_COLORS: Record<string, { bg: string; text: string; dot: string; label: string }> = {
  PRESENT:       { bg: '#F0FDF4', text: '#15803D', dot: '#34D399', label: 'Present' },
  ABSENT:        { bg: '#FEF2F2', text: '#DC2626', dot: '#F87171', label: 'No record' },
  LATE:          { bg: '#FFF7ED', text: '#C2410C', dot: '#FB923C', label: 'Late' },
  ON_LEAVE:      { bg: '#EFF6FF', text: '#2563EB', dot: '#60A5FA', label: 'On Leave' },
  HALF_DAY:      { bg: '#FFF7ED', text: '#C2410C', dot: '#FB923C', label: 'Half Day' },
  MISSING_PUNCH: { bg: '#FEF2F2', text: '#DC2626', dot: '#F87171', label: 'Missing Punch' },
  HOLIDAY:       { bg: '#F3F4F6', text: '#6B7280', dot: '#D1D5DB', label: 'Holiday' },
  WEEKEND:       { bg: '#F3F4F6', text: '#6B7280', dot: '#D1D5DB', label: 'Rest Day' },
  Unknown:       { bg: '#F3F4F6', text: '#6B7280', dot: '#D1D5DB', label: 'Unknown' },
};

function MemberCard({ member }: { member: TeamMember }) {
  const statusStyle = STATUS_COLORS[member.todayStatus ?? 'Unknown'] ?? STATUS_COLORS['Unknown'];
  const initials = member.fullName.split(' ').map((n: string) => n[0]).join('').slice(0, 2).toUpperCase();

  return (
    <View style={{
      backgroundColor: '#fff', borderRadius: 14, padding: 14, marginBottom: 10,
      flexDirection: 'row', alignItems: 'center', gap: 12,
      shadowColor: '#000', shadowOffset: { width: 0, height: 1 }, shadowOpacity: 0.06, shadowRadius: 4, elevation: 2,
    }}>
      {/* Avatar */}
      <View style={{
        width: 44, height: 44, borderRadius: 22,
        backgroundColor: COLORS.blue, alignItems: 'center', justifyContent: 'center',
      }}>
        <Text style={{ color: '#fff', fontWeight: '700', fontSize: 15 }}>{initials}</Text>
      </View>

      {/* Info */}
      <View style={{ flex: 1 }}>
        <Text style={{ fontSize: 15, fontWeight: '700', color: '#111827' }}>{member.fullName}</Text>
        <Text style={{ fontSize: 12, color: '#6B7280', marginTop: 1 }}>{member.jobTitle ?? member.department}</Text>
        {member.clockIn && (
          <Text style={{ fontSize: 11, color: '#9CA3AF', marginTop: 2 }}>
            In: {formatDate(member.clockIn, 'time')}{member.clockOut ? ` · Out: ${formatDate(member.clockOut, 'time')}` : ''}
          </Text>
        )}
      </View>

      {/* Status */}
      <View style={{ alignItems: 'flex-end' }}>
        <View style={{
          backgroundColor: statusStyle.bg, borderRadius: 8,
          paddingHorizontal: 8, paddingVertical: 4,
          flexDirection: 'row', alignItems: 'center', gap: 4,
        }}>
          <View style={{ width: 6, height: 6, borderRadius: 3, backgroundColor: statusStyle.dot }} />
          <Text style={{ fontSize: 11, color: statusStyle.text, fontWeight: '600' }}>
            {statusStyle.label}
          </Text>
        </View>
      </View>
    </View>
  );
}

export default function TeamScreen() {
  const [members, setMembers] = useState<TeamMember[]>([]);
  const [loading, setLoading] = useState(true);
  const [refreshing, setRefreshing] = useState(false);
  const [filter, setFilter] = useState('All');
  const [search, setSearch] = useState('');

  const fetchTeam = useCallback(async () => {
    try {
      const data = await teamApi.getTeam({ date: toISODate(new Date()) });
      setMembers(data.items || []);
    } catch (e: any) {
      Alert.alert('Error', e.message || 'Failed to load team');
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => { fetchTeam(); }, [fetchTeam]);

  const onRefresh = async () => {
    setRefreshing(true);
    await fetchTeam();
    setRefreshing(false);
  };

  const filtered = members.filter((m) => {
    const matchStatus = filter === 'All' || m.todayStatus === filter;
    const matchSearch = !search || m.fullName.toLowerCase().includes(search.toLowerCase());
    return matchStatus && matchSearch;
  });

  // Stats
  const stats = {
    total: members.length,
    present: members.filter((m) => m.todayStatus === 'PRESENT').length,
    absent: members.filter((m) => m.todayStatus === 'ABSENT').length,
    late: members.filter((m) => m.todayStatus === 'LATE').length,
    onLeave: members.filter((m) => m.todayStatus === 'ON_LEAVE').length,
  };

  return (
    <View style={{ flex: 1, backgroundColor: COLORS.background }}>
      {/* Header */}
      <View style={{ backgroundColor: COLORS.navy, paddingTop: 56, paddingBottom: 20, paddingHorizontal: 20 }}>
        <Text style={{ color: '#fff', fontSize: 22, fontWeight: '700' }}>My Team</Text>
        <Text style={{ color: 'rgba(255,255,255,0.6)', fontSize: 13, marginTop: 2 }}>
          {formatDate(new Date().toISOString(), 'display')}
        </Text>

        {/* Stats row */}
        {!loading && (
          <View style={{ flexDirection: 'row', marginTop: 14, gap: 8 }}>
            {[
              { label: 'Total', value: stats.total, color: '#fff' },
              { label: 'Present', value: stats.present, color: '#34D399' },
              { label: 'Absent', value: stats.absent, color: '#F87171' },
              { label: 'Late', value: stats.late, color: '#FB923C' },
              { label: 'Leave', value: stats.onLeave, color: '#60A5FA' },
            ].map((s) => (
              <View key={s.label} style={{
                flex: 1, backgroundColor: 'rgba(255,255,255,0.1)', borderRadius: 10,
                padding: 8, alignItems: 'center',
              }}>
                <Text style={{ color: s.color, fontSize: 18, fontWeight: '800' }}>{s.value}</Text>
                <Text style={{ color: 'rgba(255,255,255,0.6)', fontSize: 10, marginTop: 1 }}>{s.label}</Text>
              </View>
            ))}
          </View>
        )}
      </View>

      {/* Search */}
      <View style={{ paddingHorizontal: 16, paddingTop: 14, paddingBottom: 8 }}>
        <TextInput
          value={search}
          onChangeText={setSearch}
          placeholder="Search team members..."
          style={{
            backgroundColor: '#fff', borderRadius: 12, paddingHorizontal: 14, paddingVertical: 10,
            fontSize: 14, borderWidth: 1, borderColor: '#E5E7EB',
          }}
        />
      </View>

      {/* Filters */}
      <ScrollView
        horizontal
        // Without flexGrow: 0 the row stretches to fill the column and each chip
        // renders as a tall empty box.
        style={{ flexGrow: 0 }}
        showsHorizontalScrollIndicator={false}
        contentContainerStyle={{ paddingHorizontal: 16, paddingBottom: 10, gap: 8 }}
      >
        {STATUS_FILTERS.map((f) => (
          <TouchableOpacity
            key={f.key}
            onPress={() => setFilter(f.key)}
            style={{
              paddingHorizontal: 14, paddingVertical: 7, borderRadius: 20,
              backgroundColor: filter === f.key ? COLORS.blue : '#fff',
              borderWidth: 1, borderColor: filter === f.key ? COLORS.blue : '#E5E7EB',
            }}
          >
            <Text style={{
              fontSize: 13, fontWeight: filter === f.key ? '700' : '400',
              color: filter === f.key ? '#fff' : '#374151',
            }}>
              {f.label}
              {f.stat ? ` (${stats[f.stat]})` : ''}
            </Text>
          </TouchableOpacity>
        ))}
      </ScrollView>

      {/* List */}
      {loading ? (
        <ActivityIndicator color={COLORS.blue} style={{ marginTop: 40 }} />
      ) : (
        <ScrollView
          refreshControl={<RefreshControl refreshing={refreshing} onRefresh={onRefresh} />}
          contentContainerStyle={{ paddingHorizontal: 16, paddingBottom: 40 }}
        >
          {filtered.length === 0 ? (
            <View style={{ alignItems: 'center', marginTop: 40 }}>
              <Text style={{ fontSize: 32 }}>👥</Text>
              <Text style={{ color: '#374151', fontSize: 15, fontWeight: '600', marginTop: 10 }}>
                {search ? 'No members found' : filter !== 'All' ? 'No team members with this status' : 'No direct reports'}
              </Text>
            </View>
          ) : (
            filtered.map((m) => <MemberCard key={m.employeeId} member={m} />)
          )}
        </ScrollView>
      )}
    </View>
  );
}
