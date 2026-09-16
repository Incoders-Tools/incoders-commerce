import { apiFetch } from './client'
import type { SignedInResponse, SignInRequest } from './types'

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
