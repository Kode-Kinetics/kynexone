import React, { useCallback, useEffect, useMemo, useState } from 'react';
import {
  ActivityIndicator,
  Image,
  RefreshControl,
  ScrollView,
  StyleSheet,
  Text,
  View,
} from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import * as ImagePicker from 'expo-image-picker';
import { useTranslation } from 'react-i18next';
import { profileApi } from '@/api/adapters';
import { normalizePickedFile } from '@/api/services';
import type { EmployeeProfileRecord } from '@/api/profileMapper';
import { useAuthStore } from '@/auth/authStore';
import { FEATURES } from '@/config/features';
import { useTheme } from '@/theme/ThemeProvider';
import { useFormat } from '@/utils/format';
import { haptics } from '@/utils/haptics';
import {
  BottomSheet,
  GlassTextField,
  LiquidBackdrop,
  LiquidButton,
  MotionPressable,
  ScreenHero,
  SectionHeader,
  SkeletonCard,
  StateCard,
  StatusPill,
  Surface,
} from '@/components/ui';
import {
  dialCode,
  employmentStatusTone,
  expiringDocuments,
  identityRows,
  normalizeCountry,
  type IdentityKey,
} from './identityFields';

interface UpdateFields {
  personalEmail: string;
  phone: string;
  emergencyContactName: string;
  emergencyContactPhone: string;
}

const emptyUpdate: UpdateFields = {
  personalEmail: '',
  phone: '',
  emergencyContactName: '',
  emergencyContactPhone: '',
};

/** Known server enum values with a translation; anything else is shown as the record holds it. */
const GENDERS = ['Male', 'Female'] as const;
const MARITAL = ['Single', 'Married', 'Divorced', 'Widowed'] as const;
const EMPLOYMENT_TYPES = ['FullTime', 'PartTime', 'Contract', 'Temporary', 'Intern'] as const;

type Notice = { tone: 'success' | 'danger'; text: string } | null;

export default function ProfileScreen() {
  const { user } = useAuthStore();
  const { t, i18n } = useTranslation();
  const { theme } = useTheme();
  const f = useFormat();
  const arabic = i18n.language?.startsWith('ar');
  const [profile, setProfile] = useState<EmployeeProfileRecord | null>(null);
  const [loadState, setLoadState] = useState<'loading' | 'ready' | 'error'>('loading');
  const [refreshing, setRefreshing] = useState(false);
  const [updateOpen, setUpdateOpen] = useState(false);
  const [updateFields, setUpdateFields] = useState<UpdateFields>(emptyUpdate);
  const [submitting, setSubmitting] = useState(false);
  const [updateError, setUpdateError] = useState(false);
  const [photoSource, setPhotoSource] = useState<{ uri: string; headers: Record<string, string> } | null>(null);
  const [uploadingPhoto, setUploadingPhoto] = useState(false);
  const [notice, setNotice] = useState<Notice>(null);

  const fetchProfile = useCallback(async (isRefresh = false) => {
    if (isRefresh) setRefreshing(true);
    try {
      const data = await profileApi.getMyProfile();
      setProfile(data);
      setLoadState('ready');
      setPhotoSource(await profileApi.photoSource(data.profilePhotoUrl));
    } catch {
      setLoadState((current) => (current === 'ready' ? current : 'error'));
    } finally {
      setRefreshing(false);
    }
  }, []);

  useEffect(() => {
    void fetchProfile();
  }, [fetchProfile]);

  const country = normalizeCountry(profile?.companyCountry);
  const identity = useMemo(() => (profile ? identityRows(country, profile) : []), [country, profile]);
  const alerts = useMemo(
    () => expiringDocuments(identity, f.businessDate() ?? new Date().toISOString()),
    [f, identity],
  );

  const openUpdate = () => {
    setUpdateFields({
      personalEmail: profile?.personalEmail ?? '',
      phone: profile?.mobilePhone ?? '',
      emergencyContactName: profile?.emergencyContacts?.[0]?.name ?? '',
      emergencyContactPhone: profile?.emergencyContacts?.[0]?.phone ?? '',
    });
    setUpdateError(false);
    setUpdateOpen(true);
  };

  const changePhoto = async () => {
    if (!FEATURES.PROFILE_PHOTO_UPLOAD) return;
    setNotice(null);
    const permission = await ImagePicker.requestMediaLibraryPermissionsAsync();
    if (!permission.granted) {
      setNotice({ tone: 'danger', text: t('account.profile.photoPermission') });
      return;
    }
    const result = await ImagePicker.launchImageLibraryAsync({
      mediaTypes: ImagePicker.MediaTypeOptions.Images,
      allowsEditing: true,
      aspect: [1, 1],
      quality: 0.85,
    });
    if (result.canceled || !result.assets?.[0]) return;

    const asset = result.assets[0];
    setUploadingPhoto(true);
    try {
      const uploaded = await profileApi.uploadPhoto(
        normalizePickedFile({
          uri: asset.uri,
          name: asset.fileName ?? 'profile-photo.jpg',
          mimeType: asset.mimeType ?? 'image/jpeg',
          size: asset.fileSize,
        }),
      );
      setProfile((current) => (current ? { ...current, profilePhotoUrl: uploaded.photoUrl } : current));
      setPhotoSource(await profileApi.photoSource(uploaded.photoUrl));
      haptics.success();
      setNotice({ tone: 'success', text: t('account.profile.photoUpdated') });
    } catch {
      haptics.error();
      setNotice({ tone: 'danger', text: t('account.profile.photoFailed') });
    } finally {
      setUploadingPhoto(false);
    }
  };

  const submitUpdate = async () => {
    setSubmitting(true);
    setUpdateError(false);
    try {
      await profileApi.requestUpdate({
        personalEmail: updateFields.personalEmail || undefined,
        phone: updateFields.phone || undefined,
        emergencyContactName: updateFields.emergencyContactName || undefined,
        emergencyContactPhone: updateFields.emergencyContactPhone || undefined,
      });
      haptics.success();
      setUpdateOpen(false);
      setNotice({ tone: 'success', text: t('account.profile.updateSent') });
    } catch {
      haptics.error();
      setUpdateError(true);
    } finally {
      setSubmitting(false);
    }
  };

  const enumLabel = (group: 'gender' | 'marital' | 'employmentType', value: string | undefined, known: readonly string[]) => {
    if (!value) return null;
    return known.includes(value) ? t(`account.profile.enums.${group}.${value}` as 'account.profile.enums.gender.Male') : value;
  };

  const date = (value?: string | null) => (value ? f.date(value, 'medium') : null);

  /** The label for a document, in the jurisdiction's own words (Saudi: "Iqama / National ID"). */
  const identityLabel = (key: IdentityKey): string =>
    key === 'civilId' && country === 'BH' ? t('account.profile.ids.cpr') : t(`account.profile.ids.${key}`);

  if (loadState === 'loading') {
    return (
      <View style={[styles.root, { backgroundColor: theme.colors.canvas }]}>
        <LiquidBackdrop subtle />
        <ScreenHero eyebrow={t('account.profile.eyebrow')} title={t('account.profile.title')} />
        <View style={styles.section}>
          <SkeletonCard lines={2} />
          <SkeletonCard lines={4} style={styles.gap} />
        </View>
      </View>
    );
  }

  if (loadState === 'error' || !profile) {
    return (
      <View style={[styles.root, { backgroundColor: theme.colors.canvas }]}>
        <LiquidBackdrop subtle />
        <ScreenHero eyebrow={t('account.profile.eyebrow')} title={t('account.profile.title')} />
        <View style={styles.section}>
          <StateCard variant="error" onRetry={() => { setLoadState('loading'); void fetchProfile(); }} />
        </View>
      </View>
    );
  }

  const fullName = (arabic ? profile.fullNameAr : undefined) || profile.fullName || user?.fullName || '';
  const subtitle = [profile.jobTitle, profile.department].filter(Boolean).join(' · ');
  const status = employmentStatusTone(profile.employmentStatus);
  const phoneHint = dialCode(country);

  return (
    <View style={[styles.root, { backgroundColor: theme.colors.canvas }]}>
      <LiquidBackdrop subtle />
      <ScrollView
        contentContainerStyle={styles.content}
        refreshControl={
          <RefreshControl
            refreshing={refreshing}
            onRefresh={() => void fetchProfile(true)}
            tintColor={theme.colors.accent}
            colors={[theme.colors.accent]}
          />
        }
        showsVerticalScrollIndicator={false}
      >
        <ScreenHero eyebrow={t('account.profile.eyebrow')} title={t('account.profile.title')} />

        <View style={styles.section}>
          <Surface level={2} contentStyle={styles.identityCard}>
            <MotionPressable
              onPress={() => void changePhoto()}
              disabled={!FEATURES.PROFILE_PHOTO_UPLOAD || uploadingPhoto}
              accessibilityRole="button"
              accessibilityLabel={t('account.profile.changePhoto')}
              contentStyle={styles.avatarPressable}
            >
              <View style={[styles.avatar, { backgroundColor: theme.colors.primaryFill }]}>
                {photoSource ? (
                  <Image accessible={false} source={photoSource} style={styles.avatarImage} resizeMode="cover" />
                ) : (
                  <Text style={[theme.typography.largeTitle, { color: theme.colors.onFill }]}>
                    {fullName.trim().charAt(0).toUpperCase()}
                  </Text>
                )}
                {uploadingPhoto ? (
                  <View style={[styles.avatarLoading, { backgroundColor: theme.colors.scrim }]}>
                    <ActivityIndicator color={theme.colors.onFill} />
                  </View>
                ) : null}
              </View>
              {FEATURES.PROFILE_PHOTO_UPLOAD && !uploadingPhoto ? (
                <View style={[styles.cameraBadge, { backgroundColor: theme.colors.primaryFill, borderColor: theme.colors.cardRaised }]}>
                  <Ionicons accessible={false} name="camera" size={14} color={theme.colors.onFill} />
                </View>
              ) : null}
            </MotionPressable>
            <Text style={[theme.typography.title2, styles.center, { color: theme.colors.text }]}>{fullName}</Text>
            {subtitle ? (
              <Text style={[theme.typography.subheadline, styles.center, { color: theme.colors.textSecondary }]}>{subtitle}</Text>
            ) : null}
            <View style={styles.pills}>
              {profile.employeeNumber ? (
                <StatusPill tone="neutral" icon="id-card-outline" label={profile.employeeNumber} />
              ) : null}
              {profile.employmentStatus ? (
                <StatusPill
                  tone={status.tone}
                  label={status.key === 'unknown' ? profile.employmentStatus : t(`account.profile.status.${status.key}`)}
                />
              ) : null}
            </View>
            <LiquidButton
              label={t('account.profile.requestUpdate')}
              icon="create-outline"
              variant="secondary"
              size="compact"
              onPress={openUpdate}
              accessibilityHint={t('account.profile.requestUpdateHint')}
              style={styles.updateButton}
            />
          </Surface>
        </View>

        {notice ? (
          <View style={styles.section}>
            <Surface tintColor={theme.colors.status[notice.tone].tint} contentStyle={styles.notice}>
              <Ionicons
                accessible={false}
                name={notice.tone === 'success' ? 'checkmark-circle' : 'alert-circle-outline'}
                size={20}
                color={theme.colors.status[notice.tone].fg}
              />
              <Text accessibilityLiveRegion="polite" style={[theme.typography.subheadline, styles.flex, { color: theme.colors.text }]}>
                {notice.text}
              </Text>
            </Surface>
          </View>
        ) : null}

        {alerts.length > 0 ? (
          <View style={styles.section}>
            <SectionHeader title={t('account.profile.alertsTitle')} />
            <Surface contentStyle={styles.card}>
              {alerts.map((alert, index) => {
                const expired = alert.daysLeft < 0;
                const tone = theme.colors.status[expired ? 'danger' : 'warning'];
                return (
                  <View
                    key={alert.key}
                    style={[styles.alertRow, index < alerts.length - 1 && { borderBottomColor: theme.colors.hairline, borderBottomWidth: StyleSheet.hairlineWidth }]}
                  >
                    <View style={[styles.alertIcon, { backgroundColor: tone.tint }]}>
                      <Ionicons accessible={false} name={expired ? 'alert-circle-outline' : 'time-outline'} size={19} color={tone.fg} />
                    </View>
                    <View style={styles.flex}>
                      <Text style={[theme.typography.callout, styles.strong, { color: theme.colors.text }]}>
                        {identityLabel(alert.key)}
                      </Text>
                      <Text style={[theme.typography.footnote, { color: tone.fg }]}>
                        {expired
                          ? t('account.profile.expiredOn', { date: f.date(alert.expiry, 'medium') })
                          : t('account.profile.expiresIn', { count: alert.daysLeft, date: f.date(alert.expiry, 'medium') })}
                      </Text>
                    </View>
                  </View>
                );
              })}
            </Surface>
          </View>
        ) : null}

        <ProfileSection title={t('account.profile.personal')}>
          <InfoRow label={t('account.profile.fields.employeeId')} value={profile.employeeNumber} />
          <InfoRow label={t('account.profile.fields.fullName')} value={profile.fullName} />
          {profile.fullNameAr ? <InfoRow label={t('account.profile.fields.arabicName')} value={profile.fullNameAr} /> : null}
          <InfoRow label={t('account.profile.fields.dateOfBirth')} value={date(profile.dateOfBirth)} />
          <InfoRow label={t('account.profile.fields.nationality')} value={profile.nationality} />
          <InfoRow label={t('account.profile.fields.gender')} value={enumLabel('gender', profile.gender, GENDERS)} />
          <InfoRow label={t('account.profile.fields.maritalStatus')} value={enumLabel('marital', profile.maritalStatus, MARITAL)} isLast />
        </ProfileSection>

        <ProfileSection title={t('account.profile.contact')}>
          <InfoRow label={t('account.profile.fields.workEmail')} value={profile.workEmail} ltr />
          <InfoRow label={t('account.profile.fields.personalEmail')} value={profile.personalEmail} ltr />
          <InfoRow label={t('account.profile.fields.mobile')} value={profile.mobilePhone} ltr isLast={!profile.emergencyContacts?.length} />
          {profile.emergencyContacts?.[0] ? (
            <InfoRow
              label={t('account.profile.fields.emergencyContact')}
              value={[profile.emergencyContacts[0].name, profile.emergencyContacts[0].phone].filter(Boolean).join(' · ')}
              isLast
            />
          ) : null}
        </ProfileSection>

        <ProfileSection title={t('account.profile.employment')}>
          <InfoRow label={t('account.profile.fields.department')} value={profile.department} />
          <InfoRow label={t('account.profile.fields.jobTitle')} value={profile.jobTitle} />
          <InfoRow label={t('account.profile.fields.joinDate')} value={date(profile.joinDate)} />
          <InfoRow label={t('account.profile.fields.employmentType')} value={enumLabel('employmentType', profile.employmentType, EMPLOYMENT_TYPES)} />
          {profile.contractType && profile.contractType !== profile.employmentType ? (
            <InfoRow label={t('account.profile.fields.contractType')} value={profile.contractType} />
          ) : null}
          {profile.contractStartDate ? <InfoRow label={t('account.profile.fields.contractStart')} value={date(profile.contractStartDate)} /> : null}
          {profile.contractEndDate ? <InfoRow label={t('account.profile.fields.contractEnd')} value={date(profile.contractEndDate)} /> : null}
          {profile.probationEndDate ? <InfoRow label={t('account.profile.fields.probationEnd')} value={date(profile.probationEndDate)} /> : null}
          <InfoRow label={t('account.profile.fields.workLocation')} value={profile.workLocation} isLast />
        </ProfileSection>

        <ProfileSection title={t('account.profile.identity')}>
          {identity.map((row, index) => (
            <InfoRow
              key={row.key}
              label={identityLabel(row.key)}
              value={row.number}
              detail={row.expiry ? t('account.profile.validUntil', { date: f.date(row.expiry, 'medium') }) : null}
              emptyLabel={t('account.profile.notOnFile')}
              ltr
              isLast={index === identity.length - 1}
            />
          ))}
        </ProfileSection>
      </ScrollView>

      <BottomSheet
        visible={updateOpen}
        onClose={() => setUpdateOpen(false)}
        title={t('account.profile.updateTitle')}
        subtitle={t('account.profile.updateSubtitle')}
        actions={
          <LiquidButton
            label={t('account.profile.updateSend')}
            icon="paper-plane-outline"
            onPress={() => void submitUpdate()}
            loading={submitting}
            disabled={submitting}
          />
        }
      >
        <GlassTextField
          label={t('account.profile.fields.personalEmail')}
          icon="mail-outline"
          value={updateFields.personalEmail}
          onChangeText={(personalEmail) => setUpdateFields((c) => ({ ...c, personalEmail }))}
          placeholder={t('signin.emailPlaceholder')}
          keyboardType="email-address"
          autoCapitalize="none"
          autoCorrect={false}
        />
        <GlassTextField
          label={t('account.profile.fields.mobile')}
          icon="call-outline"
          value={updateFields.phone}
          onChangeText={(phone) => setUpdateFields((c) => ({ ...c, phone }))}
          placeholder={phoneHint ? `${phoneHint} 5X XXX XXXX` : undefined}
          keyboardType="phone-pad"
        />
        <Text style={[theme.typography.footnote, styles.hint, { color: theme.colors.textMuted }]}>
          {phoneHint ? t('account.profile.phoneHint', { code: phoneHint }) : t('account.profile.phoneHintAny')}
        </Text>
        <GlassTextField
          label={t('account.profile.fields.emergencyName')}
          icon="person-outline"
          value={updateFields.emergencyContactName}
          onChangeText={(emergencyContactName) => setUpdateFields((c) => ({ ...c, emergencyContactName }))}
        />
        <GlassTextField
          label={t('account.profile.fields.emergencyPhone')}
          icon="call-outline"
          value={updateFields.emergencyContactPhone}
          onChangeText={(emergencyContactPhone) => setUpdateFields((c) => ({ ...c, emergencyContactPhone }))}
          placeholder={phoneHint ? `${phoneHint} 5X XXX XXXX` : undefined}
          keyboardType="phone-pad"
        />
        {updateError ? (
          <Text accessibilityRole="alert" style={[theme.typography.footnote, { color: theme.colors.status.danger.fg }]}>
            {t('account.profile.updateFailed')}
          </Text>
        ) : null}
      </BottomSheet>
    </View>
  );
}

function ProfileSection({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <View style={styles.section}>
      <SectionHeader title={title} />
      <Surface contentStyle={styles.card}>{children}</Surface>
    </View>
  );
}

function InfoRow({
  label,
  value,
  detail,
  emptyLabel,
  ltr,
  isLast,
}: {
  label: string;
  value?: string | null;
  detail?: string | null;
  emptyLabel?: string;
  /** Emails, phone numbers and document numbers read left-to-right in Arabic too. */
  ltr?: boolean;
  isLast?: boolean;
}) {
  const { theme } = useTheme();
  return (
    <View
      accessible
      accessibilityLabel={[label, value || emptyLabel || '—', detail].filter(Boolean).join(', ')}
      style={[styles.infoRow, !isLast && { borderBottomColor: theme.colors.hairline, borderBottomWidth: StyleSheet.hairlineWidth }]}
    >
      <Text style={[theme.typography.footnote, { color: theme.colors.textSecondary }]}>{label}</Text>
      <Text
        selectable
        style={[
          theme.typography.callout,
          styles.strong,
          { color: value ? theme.colors.text : theme.colors.textMuted },
        ]}
      >
        {/* A left-to-right isolate keeps "+966…" from becoming "…966+" inside Arabic text. */}
        {value ? (ltr ? `\u2066${value}\u2069` : value) : emptyLabel || '—'}
      </Text>
      {detail ? <Text style={[theme.typography.footnote, { color: theme.colors.textSecondary }]}>{detail}</Text> : null}
    </View>
  );
}

const styles = StyleSheet.create({
  root: { flex: 1 },
  content: { paddingBottom: 32 },
  section: { paddingHorizontal: 16, marginTop: 16 },
  gap: { marginTop: 12 },
  identityCard: { padding: 20, alignItems: 'center', gap: 6 },
  avatarPressable: { marginBottom: 8 },
  avatar: { width: 88, height: 88, borderRadius: 44, alignItems: 'center', justifyContent: 'center', overflow: 'hidden' },
  avatarImage: { width: 88, height: 88 },
  avatarLoading: { ...StyleSheet.absoluteFill, alignItems: 'center', justifyContent: 'center' },
  cameraBadge: {
    position: 'absolute',
    end: -2,
    bottom: -2,
    width: 30,
    height: 30,
    borderRadius: 15,
    borderWidth: 2,
    alignItems: 'center',
    justifyContent: 'center',
  },
  center: { textAlign: 'center' },
  pills: { flexDirection: 'row', flexWrap: 'wrap', justifyContent: 'center', gap: 8, marginTop: 6 },
  updateButton: { marginTop: 10, minWidth: 200 },
  notice: { flexDirection: 'row', alignItems: 'center', gap: 10, padding: 14 },
  flex: { flex: 1, minWidth: 0 },
  strong: { fontWeight: '600' },
  card: { paddingHorizontal: 16 },
  alertRow: { flexDirection: 'row', alignItems: 'center', gap: 12, paddingVertical: 12 },
  alertIcon: { width: 38, height: 38, borderRadius: 12, alignItems: 'center', justifyContent: 'center' },
  infoRow: { paddingVertical: 12, gap: 2 },
  hint: { marginTop: -10, marginBottom: 6 },
});
