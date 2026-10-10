import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { ApiError, apiFetch, apiFetchForm, apiFetchOutcome, ORGANIZATION_SUSPENDED_EVENT } from './client'

vi.mock('@/branch/BranchContext', () => ({ getSelectedBranchId: () => 'branch-1' }))
vi.mock('@/organization/OrganizationContext', () => ({ getSelectedOrganizationId: () => 'org-1' }))

// Every request helper must carry the tenant selection: the server answers
// branch-owned routes (price imports, catalog rename, orders) with
// `branch-selection-required` when `X-Branch-Id` is missing.
describe('tenant headers', () => {
  const fetchMock = vi.fn()

  beforeEach(() => vi.stubGlobal('fetch', fetchMock))
  afterEach(() => {
    vi.unstubAllGlobals()
    fetchMock.mockReset()
  })

  function sentHeaders(): Record<string, string> {
    return fetchMock.mock.calls[0][1].headers as Record<string, string>
  }

  it('apiFetch sends the selected organization and branch', async () => {
    fetchMock.mockResolvedValueOnce(new Response('{}', { status: 200 }))

    await apiFetch('/stock')

    expect(sentHeaders()).toMatchObject({ 'X-Organization-Id': 'org-1', 'X-Branch-Id': 'branch-1' })
  })

  it('apiFetchForm sends them without forcing a Content-Type', async () => {
    fetchMock.mockResolvedValueOnce(new Response('{}', { status: 200 }))

    await apiFetchForm('/pricing/imports', new FormData())

    expect(sentHeaders()).toMatchObject({ 'X-Organization-Id': 'org-1', 'X-Branch-Id': 'branch-1' })
    expect(sentHeaders()).not.toHaveProperty('Content-Type')
  })

  it('apiFetchOutcome sends them', async () => {
    fetchMock.mockResolvedValueOnce(new Response('{}', { status: 200 }))

    await apiFetchOutcome('/orders/', { method: 'POST', body: '{}' })

    expect(sentHeaders()).toMatchObject({ 'X-Organization-Id': 'org-1', 'X-Branch-Id': 'branch-1' })
  })
})

// organization-account-standing T7: a suspended organization's API answers 403 { code: "organization-suspended" };
// apiFetch announces it so the session (AuthProvider) can switch to the suspended screen mid-session.
describe('organization suspension', () => {
  const fetchMock = vi.fn()
  const listener = vi.fn()

  beforeEach(() => {
    vi.stubGlobal('fetch', fetchMock)
    window.addEventListener(ORGANIZATION_SUSPENDED_EVENT, listener)
  })
  afterEach(() => {
    vi.unstubAllGlobals()
    fetchMock.mockReset()
    listener.mockReset()
    window.removeEventListener(ORGANIZATION_SUSPENDED_EVENT, listener)
  })

  it('announces a 403 organization-suspended and throws it with its code', async () => {
    fetchMock.mockResolvedValueOnce(new Response(JSON.stringify({ code: 'organization-suspended' }), { status: 403 }))

    const error = await apiFetch('/account/branches').catch((err: unknown) => err)

    expect(listener).toHaveBeenCalledTimes(1)
    expect(error).toBeInstanceOf(ApiError)
    expect((error as ApiError).code).toBe('organization-suspended')
  })

  it('does not announce a permission 403', async () => {
    fetchMock.mockResolvedValueOnce(new Response(JSON.stringify({ title: 'Forbidden' }), { status: 403 }))

    await apiFetch('/account/users').catch(() => undefined)

    expect(listener).not.toHaveBeenCalled()
  })
})
