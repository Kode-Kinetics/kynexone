// ============================================================
// ZAYRA MOBILE — Global Types
// ============================================================

// ---- User & Auth ----

export type UserRole =
  | 'EMPLOYEE'
  | 'GROUND_STAFF'
  | 'SUPERVISOR'
  | 'MANAGER'
  | 'HR'
  | 'PAYROLL'
  | 'FINANCE_APPROVER'
  | 'SUPER_ADMIN';

export type AccessMode =
  | 'FULL'
  | 'ESS_ONLY'
  | 'KIOSK'
  | 'MOBILE_ONLY'
  | 'ATTENDANCE_ONLY'
  | 'READ_ONLY';

export interface Permission {
  module: string;
  actions: string[];
}

export interface AuthUser {
  id: string;
  tenantId: string;
  employeeId: string;
  username: string;
  email: string;
  fullName: string;
  name?: string; // alias for fullName
  fullNameAr?: string;
  role: UserRole;
  accessMode: AccessMode;
  permissions: Permission[];
  departmentId?: string;
  department?: string;
  jobTitle?: string;
  managerId?: string;
  profilePhotoUrl?: string;
  isFirstLogin: boolean;
  isActive: boolean;
  mustChangePassword: boolean;
}

export interface AuthTokens {
  accessToken: string;
  refreshToken: string;
  expiresAt: number; // Unix timestamp ms
}

// ---- Attendance ----

export type PunchType = 'CLOCK_IN' | 'CLOCK_OUT' | 'BREAK_IN' | 'BREAK_OUT';
export type AttendanceStatus =
  | 'PRESENT'
  | 'ABSENT'
  | 'LATE'
  | 'HALF_DAY'
  | 'HOLIDAY'
  | 'WEEKEND'
  | 'ON_LEAVE'
  | 'MISSING_PUNCH';

export interface GeoLocation {
  latitude: number;
  longitude: number;
  accuracy?: number;
  timestamp: number;
  mocked?: boolean;
}

export interface MobilePunchPayload {
  punchType: PunchType;
  timestamp: string; // ISO 8601
  location?: GeoLocation;
  workLocationId?: string;
  deviceInfo: DeviceInfo;
  selfiePhotoReference?: string; // opaque tenant-scoped storage reference returned by the API
  deviceFaceVerified?: boolean;
  faceCapabilityAvailable?: boolean;
  notes?: string;
}

export interface DeviceInfo {
  deviceId: string;
  deviceModel: string;
  platform: 'ios' | 'android';
  osVersion: string;
  appVersion: string;
  ipAddress?: string;
}

export interface AttendanceDay {
  date: string;
  status: AttendanceStatus;
  clockIn?: string;
  clockOut?: string;
  workHours?: number;
  lateMinutes?: number;
  earlyExitMinutes?: number;
  shiftName?: string;
  locationName?: string;
  hasCorrection?: boolean;
}

export interface TodayAttendance {
  status: AttendanceStatus;
  clockIn?: string;
  clockOut?: string;
  shiftStart?: string;
  shiftEnd?: string;
  workLocation?: string;
  currentlyActive: boolean;
}

export interface AttendanceRegularization {
  id: string;
  date: string;
  reason: string;
  requestedClockIn?: string;
  requestedClockOut?: string;
  attachmentUrl?: string;
  status: ApprovalStatus;
  createdAt: string;
}

// ---- Leave ----

export type LeaveStatus =
  | 'DRAFT'
  | 'PENDING'
  | 'APPROVED'
  | 'REJECTED'
  | 'CANCELLED'
  | 'EXPIRED';

export interface LeaveBalance {
  leaveTypeId: string;
  leaveTypeName: string;
  leaveTypeNameAr?: string;
  allocated: number;
  used: number;
  pending: number;
  available: number;
  unit: 'DAYS' | 'HOURS';
  expiryDate?: string;
}

export interface LeaveRequest {
  id: string;
  leaveTypeId: string;
  leaveTypeName: string;
  startDate: string;
  endDate: string;
  totalDays: number;
  isHalfDay?: boolean;
  halfDayPeriod?: 'MORNING' | 'AFTERNOON';
  reason?: string;
  attachmentUrl?: string;
  status: LeaveStatus;
  submittedAt: string;
  approvalTimeline: ApprovalTimelineStep[];
  canCancel: boolean;
  canModify: boolean;
}

export interface ApprovalTimelineStep {
  stepName: string;
  approverName: string;
  action?: 'APPROVED' | 'REJECTED' | 'SENT_BACK' | 'PENDING';
  comment?: string;
  actionAt?: string;
}

// ---- Overtime ----

export interface OvertimeRequest {
  id: string;
  date: string;
  shiftId?: string;
  startTime: string;
  endTime: string;
  totalHours: number;
  durationMinutes?: number;
  reason: string;
  projectCode?: string;
  costCenterId?: string;
  attachmentUrl?: string;
  calculationPreview?: OTCalculationPreview;
  calculatedAmount?: number;
  compOffRequested?: boolean;
  compOffEligible?: boolean;
  approvedMinutes?: number;
  status: 'Pending' | 'Approved' | 'Rejected' | 'Cancelled';
  submittedAt: string;
  approvalTimeline: ApprovalTimelineStep[];
}

export interface OTCalculationPreview {
  baseHourlyRate: number;
  multiplier: number;
  totalAmount: number;
  currency: string;
}

// ---- Payslips ----

export interface Payslip {
  id: string;
  month: number;
  year: number;
  periodLabel: string;
  periodStart?: string;
  periodEnd?: string;
  grossSalary: number;
  grossPay?: number;    // alias for grossSalary
  netSalary: number;
  netPay?: number;      // alias for netSalary
  totalDeductions: number;
  currency: string;
  paymentStatus: 'PENDING' | 'PROCESSED' | 'PAID';
  status?: 'PENDING' | 'PROCESSED' | 'PAID'; // alias for paymentStatus
  paymentDate?: string;
  wpsReference?: string;
  wpsReferenceNumber?: string; // alias for wpsReference
  workingDays?: number;
  paidDays?: number;
  publishedAt: string;
}

export interface PayslipDetail extends Payslip {
  earnings: PayslipLine[];
  deductions: PayslipLine[];
  details?: PayslipLine[];    // flat combined list (earnings + deductions)
  overtimePayout?: number;
  loanDeductions?: number;
  advanceDeductions?: number;
  ytdGross?: number;
  ytdNet?: number;
}

export interface PayslipLine {
  id?: string;
  code: string;
  description: string;
  componentName?: string; // alias for description
  descriptionAr?: string;
  amount: number;
  type: 'EARNING' | 'DEDUCTION' | 'Earning' | 'Deduction';
}

// ---- Documents ----

export type DocumentStatus = 'VALID' | 'EXPIRING_SOON' | 'EXPIRED' | 'PENDING_REVIEW';

export interface EmployeeDocument {
  id: string;
  documentType: string;
  documentTypeAr?: string;
  documentNumber?: string;
  issueDate?: string;
  expiryDate?: string;
  status: DocumentStatus;
  verificationStatus?: string;
  fileUrl?: string;
  fileName?: string;
  uploadedAt?: string;
  daysUntilExpiry?: number;
}

// ---- HR Requests ----

export type HRRequestType =
  | 'SALARY_CERTIFICATE'
  | 'EXPERIENCE_LETTER'
  | 'NOC'
  | 'COMPLAINT'
  | 'GRIEVANCE'
  | 'GENERAL';

export interface HRRequest {
  id: string;
  ticketNumber?: string;
  requestType: HRRequestType;
  subject: string;
  description: string;
  attachmentUrl?: string;
  status: 'Open' | 'InProgress' | 'Resolved' | 'Closed' | 'Cancelled';
  /** Backend-derived: "Awaiting HR response" | "Responded" | "Overdue — not responded" | "Closed" */
  responseStatus?: string;
  slaDeadline?: string;
  slaBreached?: boolean;
  slaStatus?: string;
  assignedTo?: string;
  commentsCount?: number;
  createdAt: string;
  updatedAt: string;
  comments: HRRequestComment[];
}

export interface HRRequestComment {
  id: string;
  authorId?: string;
  authorName: string;
  authorRole: string;
  message: string;
  content?: string;   // alias for message
  createdAt: string;
  isInternal?: boolean;
}

// ---- Approvals ----

export type ApprovalStatus =
  | 'PENDING'
  | 'APPROVED'
  | 'REJECTED'
  | 'SENT_BACK'
  | 'CANCELLED'
  | 'EXPIRED';

export type ApprovalItemType =
  | 'LEAVE'
  | 'OVERTIME'
  | 'ATTENDANCE_CORRECTION'
  | 'RECRUITMENT_REQUISITION'
  | 'LOAN'
  | 'ADVANCE'
  | 'BONUS'
  | 'HR_REQUEST'
  | 'APPRAISAL'
  | 'PAYROLL';

export interface ApprovalItem {
  id: string;
  taskId: string;
  type: ApprovalItemType;
  title: string;
  requestedBy: string;
  requestedByPhoto?: string;
  requestedAt: string;
  urgency: 'LOW' | 'MEDIUM' | 'HIGH';
  summary: string;
  details: Record<string, string | number>;
  timeline: ApprovalTimelineStep[];
  canApprove: boolean;
  canReject: boolean;
  canSendBack: boolean;
}

// ---- Notifications ----

export type NotificationType =
  | 'APPROVAL_REQUIRED'
  | 'LEAVE_STATUS'
  | 'PAYSLIP_PUBLISHED'
  | 'DOCUMENT_EXPIRY'
  | 'MISSING_PUNCH'
  | 'HR_REQUEST_UPDATE'
  | 'POLICY_ACKNOWLEDGMENT'
  | 'SYSTEM';

export interface AppNotification {
  id: string;
  type: NotificationType;
  title: string;
  titleAr?: string;
  body: string;
  bodyAr?: string;
  isRead: boolean;
  createdAt: string;
  actionRoute?: string;
  actionId?: string;
}

// ---- Profile ----

export interface EmployeeProfile {
  id: string;
  employeeNumber: string;
  fullName: string;
  fullNameAr?: string;
  jobTitle: string;
  jobTitleAr?: string;
  department: string;
  departmentAr?: string;
  grade?: string;
  reportingManager?: string;
  managerName?: string;       // alias for reportingManager
  email: string;
  workEmail?: string;         // alias for email
  phone?: string;
  mobile?: string;
  mobilePhone?: string;       // alias for mobile
  personalEmail?: string;
  nationality?: string;
  dateOfBirth?: string;
  gender?: string;
  maritalStatus?: string;
  dateOfJoining?: string;
  joinDate?: string;          // alias for dateOfJoining
  contractType?: string;
  employmentType?: string;    // alias for contractType
  workLocation?: string;
  currentAddress?: string;
  profilePhotoUrl?: string;
  // GCC identity docs
  passportNumber?: string;
  passportExpiry?: string;
  visaNumber?: string;
  visaType?: string;
  visaExpiry?: string;
  iqamaNumber?: string;
  iqamaExpiry?: string;
  emiratesId?: string;
  emiratesIdExpiry?: string;
  emergencyContacts: EmergencyContact[];
  identityDocuments: IdentityDocument[];
}

export interface EmergencyContact {
  id: string;
  name: string;
  relationship: string;
  phone: string;
  email?: string;
}

export interface IdentityDocument {
  type: 'PASSPORT' | 'IQAMA' | 'EMIRATES_ID' | 'NATIONAL_ID' | 'VISA';
  number?: string;
  expiryDate?: string;
  status: DocumentStatus;
}

// ---- Team (Supervisor/Manager) ----

export interface TeamMember {
  employeeId: string;
  fullName: string;
  jobTitle: string;
  department?: string;
  profilePhotoUrl?: string;
  todayStatus: AttendanceStatus;
  clockIn?: string;
  clockOut?: string;
  hasPendingRequests?: boolean;
}

// ---- Dashboard ----

export interface EmployeeDashboard {
  profile?: {
    employeeId: string;
    fullName: string;
    jobTitle?: string;
    department?: string;
    profilePhotoUrl?: string;
  };
  todayAttendance: TodayAttendance;
  leaveBalances: LeaveBalance[];
  pendingRequestsCount: number;
  latestPayslip?: Payslip;
  expiringDocuments: EmployeeDocument[];
  unreadNotifications: number;
  upcomingHolidays: Holiday[];
}

export interface ManagerDashboard {
  teamSummary: {
    total: number;
    present: number;
    absent: number;
    onLeave: number;
    lateToday: number;
  };
  pendingApprovalsCount: number;
  pendingByType: Record<ApprovalItemType, number>;
  overtimeAlert?: { thisMonth: number; lastMonth: number };
}

export interface Holiday {
  date: string;
  name: string;
  nameAr?: string;
  type: 'PUBLIC' | 'COMPANY' | 'RELIGIOUS';
}

// ---- AI Assistant ----

export interface AIMessage {
  id: string;
  role: 'user' | 'assistant';
  content: string;
  timestamp: string;
  sources?: string[];
}

/** Backend DTO: ESSAIQuestionDto(string Question). There is no conversation id or locale. */
export interface AIAskPayload {
  question: string;
}

export interface AIAskResponse {
  conversationId?: string;
  answer: string;
  sources?: string[];
  suggestedActions?: SuggestedAction[];
}

export interface SuggestedAction {
  label: string;
  labelAr?: string;
  route: string;
  params?: Record<string, string>;
}

// ---- API Responses ----

export interface ApiResponse<T> {
  data: T;
  message?: string;
  success: boolean;
}

export interface PaginatedResponse<T> {
  data: T[];
  total: number;
  page: number;
  pageSize: number;
  hasMore: boolean;
}

// ---- Language ----

export type AppLocale = 'en' | 'ar';
export type DateDisplayMode = 'gregorian' | 'hijri';
