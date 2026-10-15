import { apiFetch } from './client'
import type {
  AdminResetPasswordRequest,
  ConfirmResetPasswordRequest,
  RenewPasswordRequest,
  ResetPasswordRequest,
  SignedInResponse,
  SignInRequest,
} from './types'

export function signIn(request: SignInRequest): Promise<SignedInResponse> {
  return apiFetch<SignedInResponse>('/account/sign-in', {
    method: 'POST',
    body: JSON.stringify(request),
  })
}

export function signOut(): Promise<void> {
  return apiFetch<void>('/account/sign-out', { method: 'POST' })
}

export function currentUser(): Promise<SignedInResponse> {
  return apiFetch<SignedInResponse>('/account/me')
}

export function requestPasswordReset(request: ResetPasswordRequest): Promise<void> {
  return apiFetch<void>('/account/reset-password/request', {
    method: 'POST',
    body: JSON.stringify(request),
  })
}

export function confirmPasswordReset(request: ConfirmResetPasswordRequest): Promise<void> {
  return apiFetch<void>('/account/reset-password/confirm', {
    method: 'POST',
    body: JSON.stringify(request),
  })
}

export function renewPassword(request: RenewPasswordRequest): Promise<void> {
  return apiFetch<void>('/account/renew-password', {
    method: 'POST',
    body: JSON.stringify(request),
  })
}

export function adminResetPassword(userId: string, request: AdminResetPasswordRequest): Promise<void> {
  return apiFetch<void>(`/account/users/${userId}/reset-password`, {
    method: 'POST',
    body: JSON.stringify(request),
  })
}
import type { BranchDiscountPinStatus, BranchSummary, CreateBranchRequest, CreateBranchResponse, CreateOrganizationRequest, CreateOrganizationResponse, CreateUserRequest, CreateUserResponse, OrganizationAccountStanding, OrganizationBranding, OrganizationSettings, OrganizationSummary, ReactivateOrganizationRequest, UpdateOrganizationAccountStandingRequest, UpdateOrganizationBrandingRequest, UpdateOrganizationSettingsRequest, UserSummary } from './types'
export const listUsers = async () => {
  const users = await apiFetch<UserSummary[]>('/account/users')
  return users.map((user) => ({ ...user, branchIds: user.branchIds ?? [] }))
}
export const createUser = (request: CreateUserRequest) => apiFetch<CreateUserResponse>('/account/users', { method: 'POST', body: JSON.stringify(request) })
export const updateUserRoles = (userId: string, roleNames: string[]) => apiFetch<void>(`/account/users/${userId}/roles`, { method: 'PUT', body: JSON.stringify({ roleNames }) })
export const updateUserBranches = (userId: string, branchIds: string[]) => apiFetch<void>(`/account/users/${userId}/branches`, { method: 'PUT', body: JSON.stringify({ branchIds }) })
export const listBranches = () => apiFetch<BranchSummary[]>('/account/branches')
export const createBranch = (request: CreateBranchRequest) => apiFetch<CreateBranchResponse>('/account/branches', { method: 'POST', body: JSON.stringify(request) })
export const listOrganizations = () => apiFetch<OrganizationSummary[]>('/account/organizations')
export const createOrganization = (request: CreateOrganizationRequest) => apiFetch<CreateOrganizationResponse>('/account/organizations', { method: 'POST', body: JSON.stringify(request) })
// T5b: sysadmin-only, targets an arbitrary organization by id (OrganizationsScreen's "Edit branding").
export const getOrganizationBranding = (organizationId: string) => apiFetch<OrganizationBranding>(`/account/organizations/${organizationId}/branding`)
export const updateOrganizationBranding = (organizationId: string, request: UpdateOrganizationBrandingRequest) => apiFetch<void>(`/account/organizations/${organizationId}/branding`, { method: 'PUT', body: JSON.stringify(request) })
// organization-account-standing T6: sysadmin-only, targets an organization by id (OrganizationsScreen's "Estado de cuenta").
export const getOrganizationStanding = (organizationId: string) => apiFetch<OrganizationAccountStanding>(`/account/organizations/${organizationId}/standing`)
export const updateOrganizationStanding = (organizationId: string, request: UpdateOrganizationAccountStandingRequest) => apiFetch<void>(`/account/organizations/${organizationId}/standing`, { method: 'PUT', body: JSON.stringify(request) })
export const suspendOrganization = (organizationId: string) => apiFetch<void>(`/account/organizations/${organizationId}/standing/suspend`, { method: 'POST' })
export const reactivateOrganization = (organizationId: string, request: ReactivateOrganizationRequest) => apiFetch<void>(`/account/organizations/${organizationId}/standing/reactivate`, { method: 'POST', body: JSON.stringify(request) })
// T6: any authenticated user's OWN organization's branding, for theming — never takes an id.
export const getOwnOrganizationBranding = () => apiFetch<OrganizationBranding>('/account/organization/branding')
// Number format: any signed-in user reads their own organization's settings; ManageBranchSettings writes them.
export const getOwnOrganizationSettings = () => apiFetch<OrganizationSettings>('/account/organization/settings')
export const updateOwnOrganizationSettings = (request: UpdateOrganizationSettingsRequest) => apiFetch<void>('/account/organization/settings', { method: 'PUT', body: JSON.stringify(request) })
// branch-discount-pin: status is readable, the PIN is write-only (PUT sets or rotates it).
export const getBranchDiscountPin = (branchId: string) => apiFetch<BranchDiscountPinStatus>(`/account/branches/${branchId}/discount-pin`)
export const setBranchDiscountPin = (branchId: string, pin: string) => apiFetch<BranchDiscountPinStatus>(`/account/branches/${branchId}/discount-pin`, { method: 'PUT', body: JSON.stringify({ pin }) })
export const updateUserStatus = (userId: string, revoked: boolean) => apiFetch<void>(`/account/users/${userId}/status`, { method: 'PUT', body: JSON.stringify({ revoked }) })
