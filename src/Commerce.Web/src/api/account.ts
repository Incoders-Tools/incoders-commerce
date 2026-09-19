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
