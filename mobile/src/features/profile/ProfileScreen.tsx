import React, { useCallback, useEffect, useState } from 'react';
import {
  ActivityIndicator,
  Alert,
  Image,
  Modal,
  RefreshControl,
  ScrollView,
  StyleSheet,
  Text,
  TextInput,
  View,
} from 'react-native';
import { Ionicons } from '@expo/vector-icons';
import * as ImagePicker from 'expo-image-picker';
import { profileApi } from '@/api/adapters';
import { normalizePickedFile } from '@/api/services';
import { formatDate } from '@/utils/date';
import { useAuthStore } from '@/auth/authStore';
import { FEATURES } from '@/config/features';
import { useTheme } from '@/theme/ThemeProvider';
import {
  GlassSurface,
  LiquidBackdrop,
  LiquidButton,
  MotionPressable,
  ScreenHero,
  SectionHeader,
} from '@/components/ui';
import type { EmployeeProfile } from '@/types';

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

export default function ProfileScreen() {
  const { user } = useAuthStore();
  const { theme } = useTheme();
  const [profile, setProfile] = useState<EmployeeProfile | null>(null);
  const [loading, setLoading] = useState(true);
  const [refreshing, setRefreshing] = useState(false);
  const [updateModal, setUpdateModal] = useState(false);
  const [updateFields, setUpdateFields] = useState<UpdateFields>(emptyUpdate);
  const [submitting, setSubmitting] = useState(false);
  const [photoSource, setPhotoSource] = useState<{ uri: string; headers: Record<string, string> } | null>(null);
  const [uploadingPhoto, setUploadingPhoto] = useState(false);
  const [expiryReferenceTime] = useState(() => Date.now());
  const fetchProfile = useCallback(async (isRefresh = false) => {
    if (isRefresh) setRefreshing(true);
    try {
      const data = await profileApi.getMyProfile();
      setProfile(data);
      setPhotoSource(await profileApi.photoSource(data.profilePhotoUrl));
    } catch (error: any) {
      Alert.alert('Profile unavailable', error.message ?? 'Failed to load your profile.');
    } finally {
      setLoading(false);
      setRefreshing(false);
    }
  }, []);

  useEffect(() => {
    void fetchProfile();
  }, [fetchProfile]);

  const openUpdateModal = () => {
    setUpdateFields({
      personalEmail: profile?.personalEmail ?? '',
      phone: profile?.mobilePhone ?? '',
      emergencyContactName: '',
      emergencyContactPhone: '',
    });
    setUpdateModal(true);
  };

  const changePhoto = async () => {
    if (!FEATURES.PROFILE_PHOTO_UPLOAD) return;
    const permission = await ImagePicker.requestMediaLibraryPermissionsAsync();
    if (!permission.granted) {
      Alert.alert('Photo access needed', 'Allow KynexOne to select a profile photo in device Settings.');
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
      const next = { ...(profile as EmployeeProfile), profilePhotoUrl: uploaded.photoUrl };
      setProfile(next);
      setPhotoSource(await profileApi.photoSource(uploaded.photoUrl));
      Alert.alert('Photo updated', 'Your new profile photo is now live.');
    } catch (error: any) {
      Alert.alert('Upload failed', error?.message ?? 'Could not update your profile photo.');
    } finally {
      setUploadingPhoto(false);
    }
  };
  const submitUpdate = async () => {
    setSubmitting(true);
    try {
      await profileApi.requestUpdate({
        personalEmail: updateFields.personalEmail || undefined,
        phone: updateFields.phone || undefined,
        emergencyContactName: updateFields.emergencyContactName || undefined,
        emergencyContactPhone: updateFields.emergencyContactPhone || undefined,
      });
      setUpdateModal(false);
      Alert.alert('Request submitted', 'HR will review these profile changes before they take effect.');
    } catch (error: any) {
      Alert.alert('Update failed', error.message ?? 'Failed to submit your update request.');
    } finally {
      setSubmitting(false);
    }
  };

  if (loading) {
    return (
      <View style={[styles.loadingRoot, { backgroundColor: theme.colors.canvas }]}>
        <LiquidBackdrop subtle />
        <GlassSurface radius={theme.radius.xl} style={styles.loadingCard} contentStyle={styles.loadingContent}>
          <ActivityIndicator color={theme.colors.primary} />
          <Text style={[theme.typography.caption, { color: theme.colors.textSecondary }]}>Loading your profile…</Text>
        </GlassSurface>
      </View>
    );
  }

  const fullName = profile?.fullName ?? user?.name ?? user?.fullName ?? 'Employee';
  const subtitle = [profile?.jobTitle ?? user?.jobTitle, profile?.department ?? user?.department]
    .filter(Boolean)
    .join(' · ');

  return (
    <View style={[styles.root, { backgroundColor: theme.colors.canvas }]}>
      <LiquidBackdrop subtle />
      <ScrollView
        contentContainerStyle={styles.content}
        refreshControl={
          <RefreshControl
            refreshing={refreshing}
            onRefresh={() => void fetchProfile(true)}
            tintColor={theme.colors.primary}
            colors={[theme.colors.primary]}
          />
        }
        showsVerticalScrollIndicator={false}
      >
        <ScreenHero eyebrow="Employee profile" title="My profile" subtitle="Personal, employment and identity information" />

        <View style={styles.section}>
          <GlassSurface
            radius={theme.radius.xxl}
            tintColor={theme.isDark ? 'rgba(30,69,150,0.28)' : 'rgba(255,255,255,0.50)'}
            contentStyle={styles.identityCard}
          >
            <MotionPressable
              onPress={() => void changePhoto()}
              disabled={!FEATURES.PROFILE_PHOTO_UPLOAD || uploadingPhoto}
              haptic="selection"
              contentStyle={styles.avatarPressable}
              accessibilityLabel="Change profile photo"
            >
              <View style={[styles.avatar, { backgroundColor: theme.colors.primary }]}>
                {photoSource ? (
                  <Image source={photoSource} style={styles.avatarImage} resizeMode="cover" />
                ) : (
                  <Text style={styles.avatarLetter}>{fullName.charAt(0).toUpperCase()}</Text>
                )}
                {uploadingPhoto ? (
                  <View style={styles.avatarLoading}>
                    <ActivityIndicator color="#FFFFFF" />
                  </View>
                ) : null}
              </View>
              {FEATURES.PROFILE_PHOTO_UPLOAD && !uploadingPhoto ? (
                <View style={[styles.cameraBadge, { backgroundColor: theme.colors.cyan, borderColor: theme.colors.surfaceStrong }]}>
                  <Ionicons name="camera" size={14} color="#05202B" />
                </View>
              ) : null}
            </MotionPressable>
            <View style={styles.identityCopy}>
              <Text style={[theme.typography.h2, { color: theme.colors.text }]}>{fullName}</Text>
              <Text style={[theme.typography.caption, { color: theme.colors.textSecondary, marginTop: 4 }]}>
                {subtitle || 'Employee'}
              </Text>
              <View style={styles.identityMeta}>
                <View style={[styles.identityPill, { backgroundColor: `${theme.colors.primary}18` }]}>
                  <Ionicons name="id-card-outline" size={13} color={theme.colors.primary} />
                  <Text style={[theme.typography.micro, { color: theme.colors.primary }]}>
                    {profile?.employeeNumber ?? `#${user?.employeeId ?? '—'}`}
                  </Text>
                </View>
                <View style={[styles.identityPill, { backgroundColor: `${theme.colors.success}18` }]}>
                  <View style={[styles.activeDot, { backgroundColor: theme.colors.success }]} />
                  <Text style={[theme.typography.micro, { color: theme.colors.success }]}>Active</Text>
                </View>
              </View>
            </View>

            <MotionPressable
              onPress={openUpdateModal}
              haptic="selection"
              contentStyle={[styles.updateButton, { backgroundColor: `${theme.colors.primary}18` }]}
            >
              <Ionicons name="create-outline" size={17} color={theme.colors.primary} />
              <Text style={[theme.typography.caption, { color: theme.colors.primary, fontWeight: '700' }]}>Request update</Text>
            </MotionPressable>
          </GlassSurface>
        </View>

        <ExpiryAlerts profile={profile} referenceTime={expiryReferenceTime} />

        <ProfileSection title="Personal information" icon="person-outline">
          <InfoRow label="Employee ID" value={profile?.employeeNumber} />
          <InfoRow label="Full name" value={profile?.fullName} />
          <InfoRow label="Date of birth" value={profile?.dateOfBirth ? formatDate(profile.dateOfBirth, 'display') : null} />
          <InfoRow label="Nationality" value={profile?.nationality} />
          <InfoRow label="Gender" value={profile?.gender} />
          <InfoRow label="Marital status" value={profile?.maritalStatus} isLast />
        </ProfileSection>

        <ProfileSection title="Contact information" icon="call-outline">
          <InfoRow label="Work email" value={profile?.workEmail} />
          <InfoRow label="Personal email" value={profile?.personalEmail} />
          <InfoRow label="Mobile" value={profile?.mobilePhone} />
          <InfoRow label="Current address" value={profile?.currentAddress} isLast />
        </ProfileSection>
        <ProfileSection title="Employment details" icon="briefcase-outline">
          <InfoRow label="Department" value={profile?.department} />
          <InfoRow label="Job title" value={profile?.jobTitle} />
          <InfoRow label="Grade" value={profile?.grade} />
          <InfoRow label="Join date" value={profile?.joinDate ? formatDate(profile.joinDate, 'display') : null} />
          <InfoRow label="Employment type" value={profile?.employmentType} />
          <InfoRow label="Work location" value={profile?.workLocation} />
          <InfoRow label="Manager" value={profile?.managerName} isLast />
        </ProfileSection>

        <ProfileSection title="Identity documents" icon="document-lock-outline">
          <InfoRow label="Passport number" value={profile?.passportNumber} />
          <InfoRow label="Passport expiry" value={profile?.passportExpiry ? formatDate(profile.passportExpiry, 'display') : null} />
          <InfoRow label="Visa number" value={profile?.visaNumber} />
          <InfoRow label="Visa type" value={profile?.visaType} />
          <InfoRow label="Visa expiry" value={profile?.visaExpiry ? formatDate(profile.visaExpiry, 'display') : null} />
          <InfoRow label="Iqama / National ID" value={profile?.iqamaNumber} />
          <InfoRow label="Iqama expiry" value={profile?.iqamaExpiry ? formatDate(profile.iqamaExpiry, 'display') : null} />
          <InfoRow label="Emirates ID" value={profile?.emiratesId} />
          <InfoRow label="Emirates ID expiry" value={profile?.emiratesIdExpiry ? formatDate(profile.emiratesIdExpiry, 'display') : null} isLast />
        </ProfileSection>

        {(profile?.emergencyContacts?.length ?? 0) > 0 ? (
          <ProfileSection title="Emergency contacts" icon="medkit-outline">
            {profile!.emergencyContacts!.map((contact, index) => (
              <View
                key={`${contact.name}-${index}`}
                style={[
                  styles.contactRow,
                  index < profile!.emergencyContacts!.length - 1 && {
                    borderBottomColor: theme.colors.divider,
                    borderBottomWidth: StyleSheet.hairlineWidth,
                  },
                ]}
              >
                <View style={[styles.contactIcon, { backgroundColor: `${theme.colors.danger}15` }]}>
                  <Ionicons name="heart-outline" size={19} color={theme.colors.danger} />
                </View>
                <View style={styles.contactCopy}>
                  <Text style={[theme.typography.bodyStrong, { color: theme.colors.text }]}>{contact.name}</Text>
                  <Text style={[theme.typography.caption, { color: theme.colors.textMuted, marginTop: 2 }]}>
                    {contact.relationship} · {contact.phone}
                  </Text>
                </View>
              </View>
            ))}
          </ProfileSection>
        ) : null}
        <View style={styles.bottomSpacer} />
      </ScrollView>

      <UpdateProfileModal
        visible={updateModal}
        values={updateFields}
        submitting={submitting}
        onChange={setUpdateFields}
        onClose={() => setUpdateModal(false)}
        onSubmit={() => void submitUpdate()}
      />
    </View>
  );
}
function ProfileSection({
  title,
  icon,
  children,
}: {
  title: string;
  icon: React.ComponentProps<typeof Ionicons>['name'];
  children: React.ReactNode;
}) {
  const { theme } = useTheme();
  return (
    <View style={styles.section}>
      <SectionHeader title={title} />
      <GlassSurface elevated={false} radius={theme.radius.xl} contentStyle={styles.profileSectionCard}>
        <View style={styles.sectionTitleRow}>
          <View style={[styles.sectionIcon, { backgroundColor: `${theme.colors.primary}16` }]}>
            <Ionicons name={icon} size={18} color={theme.colors.primary} />
          </View>
          <Text style={[theme.typography.caption, { color: theme.colors.textSecondary }]}>Verified employee record</Text>
        </View>
        {children}
      </GlassSurface>
    </View>
  );
}

function InfoRow({
  label,
  value,
  isLast,
}: {
  label: string;
  value?: string | null;
  isLast?: boolean;
}) {
  const { theme } = useTheme();
  return (
    <View
      style={[
        styles.infoRow,
        !isLast && {
          borderBottomColor: theme.colors.divider,
          borderBottomWidth: StyleSheet.hairlineWidth,
        },
      ]}
    >
      <Text style={[theme.typography.micro, styles.infoLabel, { color: theme.colors.textMuted }]}>{label}</Text>
      <Text
        selectable
        style={[
          theme.typography.bodyStrong,
          { color: value ? theme.colors.text : theme.colors.textMuted, textAlign: 'right', flex: 1 },
        ]}
      >
        {value || '—'}
      </Text>
    </View>
  );
}
function ExpiryAlerts({
  profile,
  referenceTime,
}: {
  profile: EmployeeProfile | null;
  referenceTime: number;
}) {
  const { theme } = useTheme();
  const alerts = [
    { label: 'Passport', date: profile?.passportExpiry },
    { label: 'Visa', date: profile?.visaExpiry },
    { label: 'Iqama', date: profile?.iqamaExpiry },
    { label: 'Emirates ID', date: profile?.emiratesIdExpiry },
  ]
    .filter((item): item is { label: string; date: string } => !!item.date)
    .map((item) => ({
      ...item,
      daysLeft: Math.ceil((new Date(item.date).getTime() - referenceTime) / 86_400_000),
    }))
    .filter((item) => item.daysLeft <= 60);

  if (alerts.length === 0) return null;

  return (
    <View style={styles.section}>
      <SectionHeader title="Document alerts" subtitle="Identity documents requiring attention" />
      <GlassSurface elevated={false} radius={theme.radius.xl} contentStyle={styles.alertsCard}>
        {alerts.map((alert, index) => {
          const expired = alert.daysLeft < 0;
          const accent = expired ? theme.colors.danger : theme.colors.warning;
          return (
            <View
              key={alert.label}
              style={[
                styles.alertRow,
                index < alerts.length - 1 && {
                  borderBottomColor: theme.colors.divider,
                  borderBottomWidth: StyleSheet.hairlineWidth,
                },
              ]}
            >
              <View style={[styles.alertIcon, { backgroundColor: `${accent}18` }]}>
                <Ionicons name={expired ? 'alert-circle-outline' : 'time-outline'} size={19} color={accent} />
              </View>
              <View style={styles.alertCopy}>
                <Text style={[theme.typography.bodyStrong, { color: theme.colors.text }]}>{alert.label}</Text>
                <Text style={[theme.typography.caption, { color: accent, marginTop: 2 }]}>
                  {expired ? 'Expired' : `Expires in ${alert.daysLeft} days`} · {formatDate(alert.date, 'display')}
                </Text>
              </View>
            </View>
          );
        })}
      </GlassSurface>
    </View>
  );
}
function UpdateProfileModal({
  visible,
  values,
  submitting,
  onChange,
  onClose,
  onSubmit,
}: {
  visible: boolean;
  values: UpdateFields;
  submitting: boolean;
  onChange: React.Dispatch<React.SetStateAction<UpdateFields>>;
  onClose: () => void;
  onSubmit: () => void;
}) {
  const { theme } = useTheme();
  const fields: { label: string; key: keyof UpdateFields; placeholder: string; keyboardType?: 'email-address' | 'phone-pad' }[] = [
    { label: 'Personal email', key: 'personalEmail', placeholder: 'your@email.com', keyboardType: 'email-address' },
    { label: 'Mobile phone', key: 'phone', placeholder: '+971 50 000 0000', keyboardType: 'phone-pad' },
    { label: 'Emergency contact name', key: 'emergencyContactName', placeholder: 'Full name' },
    { label: 'Emergency contact phone', key: 'emergencyContactPhone', placeholder: '+966 5X XXX XXXX', keyboardType: 'phone-pad' },
  ];

  return (
    <Modal visible={visible} animationType="slide" transparent onRequestClose={onClose}>
      <View style={[styles.modalBackdrop, { backgroundColor: theme.colors.overlay }]}>
        <GlassSurface
          radius={theme.radius.xxl}
          style={styles.modalSheet}
          contentStyle={styles.modalContent}
          tintColor={theme.isDark ? 'rgba(11,29,57,0.92)' : 'rgba(255,255,255,0.92)'}
        >
          <View style={[styles.modalHandle, { backgroundColor: theme.colors.border }]} />
          <Text style={[theme.typography.h2, { color: theme.colors.text }]}>Request profile update</Text>
          <Text style={[theme.typography.caption, { color: theme.colors.textMuted, marginTop: 5, marginBottom: 18 }]}>
            HR reviews each change before it becomes part of the employee record.
          </Text>

          <ScrollView showsVerticalScrollIndicator={false} keyboardShouldPersistTaps="handled">
            {fields.map((field) => (
              <View key={field.key} style={styles.modalField}>
                <Text style={[theme.typography.caption, styles.modalLabel, { color: theme.colors.textSecondary }]}>
                  {field.label}
                </Text>
                <TextInput
                  value={values[field.key]}
                  onChangeText={(value) => onChange((current) => ({ ...current, [field.key]: value }))}
                  placeholder={field.placeholder}
                  placeholderTextColor={theme.colors.textMuted}
                  selectionColor={theme.colors.primary}
                  keyboardType={field.keyboardType}
                  autoCapitalize={field.keyboardType === 'email-address' ? 'none' : 'sentences'}
                  style={[
                    styles.modalInput,
                    theme.typography.body,
                    { color: theme.colors.text, backgroundColor: theme.colors.surfaceSoft, borderColor: theme.colors.border },
                  ]}
                />
              </View>
            ))}
          </ScrollView>

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
              label="Submit update"
              icon="paper-plane-outline"
              onPress={onSubmit}
              loading={submitting}
              disabled={submitting}
              style={styles.modalPrimary}
            />
          </View>
        </GlassSurface>
      </View>
    </Modal>
  );
}
const styles = StyleSheet.create({
  root: { flex: 1 },
  content: { paddingBottom: 36 },
  loadingRoot: { flex: 1, alignItems: 'center', justifyContent: 'center', padding: 24 },
  loadingCard: { width: '100%', maxWidth: 320, minHeight: 170 },
  loadingContent: { alignItems: 'center', justifyContent: 'center', gap: 12, padding: 24 },
  section: { paddingHorizontal: 16, marginTop: 16 },
  identityCard: { padding: 17, alignItems: 'center' },
  avatarPressable: { position: 'relative' },
  avatar: { width: 88, height: 88, borderRadius: 31, alignItems: 'center', justifyContent: 'center', overflow: 'hidden' },
  avatarImage: { width: 88, height: 88 },
  avatarLetter: { color: '#FFFFFF', fontSize: 36, fontWeight: '800' },
  avatarLoading: { ...StyleSheet.absoluteFill, backgroundColor: 'rgba(5,10,20,0.56)', alignItems: 'center', justifyContent: 'center' },
  cameraBadge: { position: 'absolute', right: -4, bottom: -4, width: 29, height: 29, borderRadius: 15, borderWidth: 2, alignItems: 'center', justifyContent: 'center' },
  identityCopy: { alignItems: 'center', marginTop: 13 },
  identityMeta: { flexDirection: 'row', gap: 7, marginTop: 10 },
  identityPill: { flexDirection: 'row', alignItems: 'center', gap: 5, paddingHorizontal: 9, paddingVertical: 6, borderRadius: 999 },
  activeDot: { width: 6, height: 6, borderRadius: 3 },
  updateButton: { flexDirection: 'row', alignItems: 'center', gap: 7, marginTop: 15, paddingHorizontal: 13, paddingVertical: 9, borderRadius: 14 },
  profileSectionCard: { paddingHorizontal: 14, paddingBottom: 2 },
  sectionTitleRow: { flexDirection: 'row', alignItems: 'center', gap: 9, paddingVertical: 12 },
  sectionIcon: { width: 36, height: 36, borderRadius: 13, alignItems: 'center', justifyContent: 'center' },
  infoRow: { minHeight: 60, flexDirection: 'row', alignItems: 'center', justifyContent: 'space-between', gap: 16, paddingVertical: 10 },
  infoLabel: { textTransform: 'uppercase', letterSpacing: 0.55, maxWidth: '42%' },
  alertsCard: { paddingHorizontal: 14 },
  alertRow: { minHeight: 70, flexDirection: 'row', alignItems: 'center', gap: 11, paddingVertical: 10 },
  alertIcon: { width: 42, height: 42, borderRadius: 15, alignItems: 'center', justifyContent: 'center' },
  alertCopy: { flex: 1 },
  contactRow: { minHeight: 67, flexDirection: 'row', alignItems: 'center', gap: 11, paddingVertical: 10 },
  contactIcon: { width: 40, height: 40, borderRadius: 14, alignItems: 'center', justifyContent: 'center' },
  contactCopy: { flex: 1 },
  modalBackdrop: { flex: 1, justifyContent: 'flex-end' },
  modalSheet: { maxHeight: '88%', borderBottomLeftRadius: 0, borderBottomRightRadius: 0 },
  modalContent: { paddingHorizontal: 20, paddingTop: 12, paddingBottom: 30 },
  modalHandle: { width: 44, height: 5, borderRadius: 999, alignSelf: 'center', marginBottom: 17 },
  modalField: { marginBottom: 13 },
  modalLabel: { marginBottom: 6, fontWeight: '700' },
  modalInput: { minHeight: 52, borderRadius: 16, borderWidth: StyleSheet.hairlineWidth, paddingHorizontal: 13 },
  modalActions: { flexDirection: 'row', gap: 10, marginTop: 8 },
  modalSecondaryShell: { flex: 1 },
  modalSecondary: { minHeight: 56, borderRadius: 18, borderWidth: StyleSheet.hairlineWidth, alignItems: 'center', justifyContent: 'center' },
  modalPrimary: { flex: 1 },
  bottomSpacer: { height: 14 },
});
