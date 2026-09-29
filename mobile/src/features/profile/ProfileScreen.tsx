import React, { useState, useEffect, useCallback } from 'react';
import {
  View, Text, ScrollView, TouchableOpacity,
  ActivityIndicator, Alert, RefreshControl, TextInput, Modal, Image,
} from 'react-native';
import { profileApi } from '@/api/adapters';
import { EmployeeProfile } from '@/types';
import { formatDate } from '@/utils/date';
import { useAuthStore } from '@/auth/authStore';
import { COLORS } from '@/config';
import { FEATURES } from '@/config/features';
import * as ImagePicker from 'expo-image-picker';
import { normalizePickedFile } from '@/api/services';

function InfoRow({ label, value }: { label: string; value?: string | null }) {
  return (
    <View style={{ paddingVertical: 10, borderBottomWidth: 1, borderBottomColor: '#F3F4F6' }}>
      <Text style={{ fontSize: 11, color: '#9CA3AF', textTransform: 'uppercase', fontWeight: '600', marginBottom: 2 }}>
        {label}
      </Text>
      <Text style={{ fontSize: 14, color: value ? '#111827' : '#D1D5DB', fontWeight: value ? '500' : '400' }}>
        {value || '—'}
      </Text>
    </View>
  );
}

function SectionCard({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <View style={{
      backgroundColor: '#fff', borderRadius: 14, padding: 16, marginBottom: 14,
      shadowColor: '#000', shadowOffset: { width: 0, height: 1 }, shadowOpacity: 0.06, shadowRadius: 4, elevation: 2,
    }}>
      <Text style={{ fontSize: 15, fontWeight: '700', color: '#111827', marginBottom: 8 }}>{title}</Text>
      {children}
    </View>
  );
}

function ExpiryAlert({ label, date, referenceTime }: { label: string; date: string; referenceTime: number }) {
  const daysLeft = Math.ceil((new Date(date).getTime() - referenceTime) / 86400000);
  const isExpired = daysLeft < 0;
  const isWarning = daysLeft >= 0 && daysLeft <= 60;
  if (!isExpired && !isWarning) return null;
  return (
    <View style={{
      flexDirection: 'row', alignItems: 'center', gap: 8,
      backgroundColor: isExpired ? '#FEF2F2' : '#FFF7ED',
      borderRadius: 8, padding: 10, marginBottom: 6,
    }}>
      <Text style={{ fontSize: 16 }}>{isExpired ? '🔴' : '🟡'}</Text>
      <Text style={{ fontSize: 13, color: isExpired ? '#DC2626' : '#C2410C', flex: 1 }}>
        {label} {isExpired ? 'EXPIRED' : `expires in ${daysLeft} days`} ({formatDate(date, 'display')})
      </Text>
    </View>
  );
}

export default function ProfileScreen() {
  const { user } = useAuthStore();
  const [profile, setProfile] = useState<EmployeeProfile | null>(null);
  const [loading, setLoading] = useState(true);
  const [refreshing, setRefreshing] = useState(false);
  const [updateModal, setUpdateModal] = useState(false);
  const [updateFields, setUpdateFields] = useState({ personalEmail: '', phone: '', emergencyContactName: '', emergencyContactPhone: '' });
  const [submitting, setSubmitting] = useState(false);
  const [photoSource, setPhotoSource] = useState<{ uri: string; headers: Record<string, string> } | null>(null);
  const [uploadingPhoto, setUploadingPhoto] = useState(false);
  const [expiryReferenceTime] = useState(() => Date.now());

  const fetchProfile = useCallback(async () => {
    try {
      const data = await profileApi.getMyProfile();
      setProfile(data);
      setPhotoSource(await profileApi.photoSource(data.profilePhotoUrl));
    } catch (e: any) {
      Alert.alert('Error', e.message || 'Failed to load profile');
    } finally {
      setLoading(false);
    }
  }, []);

  useEffect(() => { fetchProfile(); }, [fetchProfile]);

  const onRefresh = async () => {
    setRefreshing(true);
    await fetchProfile();
    setRefreshing(false);
  };

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
        })
      );
      const next = { ...(profile as EmployeeProfile), profilePhotoUrl: uploaded.photoUrl };
      setProfile(next);
      setPhotoSource(await profileApi.photoSource(uploaded.photoUrl));
      Alert.alert('Photo updated', 'Your new profile photo is now live.');
    } catch (error: any) {
      Alert.alert('Upload failed', error?.message || 'Could not update your profile photo.');
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
      Alert.alert('Submitted', 'Profile update request sent for approval');
    } catch (e: any) {
      Alert.alert('Error', e.message || 'Failed to submit update request');
    } finally {
      setSubmitting(false);
    }
  };

  if (loading) {
    return (
      <View style={{ flex: 1, alignItems: 'center', justifyContent: 'center', backgroundColor: COLORS.background }}>
        <ActivityIndicator color={COLORS.blue} size="large" />
      </View>
    );
  }

  return (
    <View style={{ flex: 1, backgroundColor: COLORS.background }}>
      {/* Header */}
      <View style={{
        backgroundColor: COLORS.navy, paddingTop: 56, paddingBottom: 30, paddingHorizontal: 20,
        alignItems: 'center',
      }}>
        {/* Avatar */}
        <TouchableOpacity
          onPress={changePhoto}
          disabled={!FEATURES.PROFILE_PHOTO_UPLOAD || uploadingPhoto}
          style={{ position: 'relative' }}
          accessibilityLabel="Change profile photo"
        >
          <View style={{
            width: 72, height: 72, borderRadius: 36, backgroundColor: COLORS.blue,
            alignItems: 'center', justifyContent: 'center', borderWidth: 3, borderColor: 'rgba(255,255,255,0.3)',
            overflow: 'hidden',
          }}>
            {photoSource ? (
              <Image source={photoSource} style={{ width: 72, height: 72 }} resizeMode="cover" />
            ) : (
              <Text style={{ color: '#fff', fontSize: 28, fontWeight: '700' }}>
                {(profile?.fullName ?? user?.name ?? 'U').charAt(0).toUpperCase()}
              </Text>
            )}
            {uploadingPhoto && (
              <View style={{ position: 'absolute', top: 0, right: 0, bottom: 0, left: 0, backgroundColor: 'rgba(11,16,32,0.62)', alignItems: 'center', justifyContent: 'center' }}>
                <ActivityIndicator color="#fff" />
              </View>
            )}
          </View>
          {FEATURES.PROFILE_PHOTO_UPLOAD && !uploadingPhoto && (
            <View style={{ position: 'absolute', right: -2, bottom: -2, width: 24, height: 24, borderRadius: 12, backgroundColor: COLORS.cyan, alignItems: 'center', justifyContent: 'center', borderWidth: 2, borderColor: COLORS.navy }}>
              <Text style={{ fontSize: 12 }}>📷</Text>
            </View>
          )}
        </TouchableOpacity>
        <Text style={{ color: '#fff', fontSize: 20, fontWeight: '700', marginTop: 10 }}>
          {profile?.fullName ?? user?.name}
        </Text>
        <Text style={{ color: 'rgba(255,255,255,0.6)', fontSize: 13, marginTop: 2 }}>
          {profile?.jobTitle ?? user?.jobTitle} · {profile?.department ?? user?.department}
        </Text>
        <TouchableOpacity
          onPress={openUpdateModal}
          style={{
            backgroundColor: 'rgba(255,255,255,0.15)', borderRadius: 20,
            paddingHorizontal: 16, paddingVertical: 6, marginTop: 12,
          }}
        >
          <Text style={{ color: '#fff', fontSize: 13 }}>Request Update</Text>
        </TouchableOpacity>
      </View>

      <ScrollView
        refreshControl={<RefreshControl refreshing={refreshing} onRefresh={onRefresh} />}
        contentContainerStyle={{ padding: 16, paddingBottom: 40 }}
      >
        {/* Expiry alerts */}
        {profile?.passportExpiry && <ExpiryAlert label="Passport" date={profile.passportExpiry} referenceTime={expiryReferenceTime} />}
        {profile?.visaExpiry && <ExpiryAlert label="Visa" date={profile.visaExpiry} referenceTime={expiryReferenceTime} />}
        {profile?.iqamaExpiry && <ExpiryAlert label="Iqama" date={profile.iqamaExpiry} referenceTime={expiryReferenceTime} />}
        {profile?.emiratesIdExpiry && <ExpiryAlert label="Emirates ID" date={profile.emiratesIdExpiry} referenceTime={expiryReferenceTime} />}

        {/* Personal Info */}
        <SectionCard title="Personal Information">
          <InfoRow label="Employee ID" value={profile?.employeeNumber} />
          <InfoRow label="Full Name" value={profile?.fullName} />
          <InfoRow label="Date of Birth" value={profile?.dateOfBirth ? formatDate(profile.dateOfBirth, 'display') : null} />
          <InfoRow label="Nationality" value={profile?.nationality} />
          <InfoRow label="Gender" value={profile?.gender} />
          <InfoRow label="Marital Status" value={profile?.maritalStatus} />
        </SectionCard>

        {/* Contact */}
        <SectionCard title="Contact Information">
          <InfoRow label="Work Email" value={profile?.workEmail} />
          <InfoRow label="Personal Email" value={profile?.personalEmail} />
          <InfoRow label="Mobile" value={profile?.mobilePhone} />
          <InfoRow label="Current Address" value={profile?.currentAddress} />
        </SectionCard>

        {/* Employment */}
        <SectionCard title="Employment Details">
          <InfoRow label="Department" value={profile?.department} />
          <InfoRow label="Job Title" value={profile?.jobTitle} />
          <InfoRow label="Grade" value={profile?.grade} />
          <InfoRow label="Join Date" value={profile?.joinDate ? formatDate(profile.joinDate, 'display') : null} />
          <InfoRow label="Employment Type" value={profile?.employmentType} />
          <InfoRow label="Work Location" value={profile?.workLocation} />
          <InfoRow label="Manager" value={profile?.managerName} />
        </SectionCard>

        {/* Documents */}
        <SectionCard title="Identity Documents">
          <InfoRow label="Passport No." value={profile?.passportNumber} />
          <InfoRow label="Passport Expiry" value={profile?.passportExpiry ? formatDate(profile.passportExpiry, 'display') : null} />
          <InfoRow label="Visa No." value={profile?.visaNumber} />
          <InfoRow label="Visa Type" value={profile?.visaType} />
          <InfoRow label="Visa Expiry" value={profile?.visaExpiry ? formatDate(profile.visaExpiry, 'display') : null} />
          <InfoRow label="Iqama/National ID" value={profile?.iqamaNumber} />
          <InfoRow label="Iqama Expiry" value={profile?.iqamaExpiry ? formatDate(profile.iqamaExpiry, 'display') : null} />
          <InfoRow label="Emirates ID" value={profile?.emiratesId} />
          <InfoRow label="Emirates ID Expiry" value={profile?.emiratesIdExpiry ? formatDate(profile.emiratesIdExpiry, 'display') : null} />
        </SectionCard>

        {/* Emergency Contacts */}
        {(profile?.emergencyContacts?.length ?? 0) > 0 && (
          <SectionCard title="Emergency Contacts">
            {profile!.emergencyContacts!.map((ec, i) => (
              <View key={i} style={{ paddingVertical: 8, borderBottomWidth: 1, borderBottomColor: '#F3F4F6' }}>
                <Text style={{ fontSize: 14, fontWeight: '600', color: '#111827' }}>{ec.name}</Text>
                <Text style={{ fontSize: 13, color: '#6B7280' }}>{ec.relationship} · {ec.phone}</Text>
              </View>
            ))}
          </SectionCard>
        )}
      </ScrollView>

      {/* Update request modal */}
      <Modal visible={updateModal} animationType="slide" transparent>
        <View style={{ flex: 1, backgroundColor: 'rgba(0,0,0,0.5)', justifyContent: 'flex-end' }}>
          <View style={{ backgroundColor: '#fff', borderTopLeftRadius: 24, borderTopRightRadius: 24, padding: 24, paddingBottom: 40 }}>
            <Text style={{ fontSize: 18, fontWeight: '700', color: '#111827', marginBottom: 4 }}>Request Profile Update</Text>
            <Text style={{ fontSize: 13, color: '#6B7280', marginBottom: 20 }}>Changes will be reviewed by HR before taking effect.</Text>

            {[
              { label: 'Personal Email', key: 'personalEmail' as const, placeholder: 'your@email.com' },
              { label: 'Mobile Phone', key: 'phone' as const, placeholder: '+971 50 000 0000' },
              { label: 'Emergency Contact Name', key: 'emergencyContactName' as const, placeholder: 'Full name' },
              { label: 'Emergency Contact Phone', key: 'emergencyContactPhone' as const, placeholder: '+966 5X XXX XXXX' },
            ].map(({ label, key, placeholder }) => (
              <View key={key} style={{ marginBottom: 14 }}>
                <Text style={{ fontSize: 13, fontWeight: '600', color: '#374151', marginBottom: 6 }}>{label}</Text>
                <TextInput
                  value={updateFields[key]}
                  onChangeText={(v) => setUpdateFields((prev) => ({ ...prev, [key]: v }))}
                  placeholder={placeholder}
                  style={{
                    borderWidth: 1, borderColor: '#D1D5DB', borderRadius: 10,
                    paddingHorizontal: 14, paddingVertical: 12, fontSize: 15,
                  }}
                />
              </View>
            ))}

            <View style={{ flexDirection: 'row', gap: 12 }}>
              <TouchableOpacity
                onPress={() => setUpdateModal(false)}
                style={{ flex: 1, borderWidth: 1, borderColor: '#D1D5DB', borderRadius: 12, padding: 14, alignItems: 'center' }}
              >
                <Text style={{ color: '#374151', fontWeight: '600' }}>Cancel</Text>
              </TouchableOpacity>
              <TouchableOpacity
                onPress={submitUpdate}
                disabled={submitting}
                style={{ flex: 1, backgroundColor: COLORS.blue, borderRadius: 12, padding: 14, alignItems: 'center' }}
              >
                {submitting ? <ActivityIndicator color="#fff" /> : (
                  <Text style={{ color: '#fff', fontWeight: '700' }}>Submit</Text>
                )}
              </TouchableOpacity>
            </View>
          </View>
        </View>
      </Modal>
    </View>
  );
}
