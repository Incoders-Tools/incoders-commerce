import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { apiFetch, apiFetchForm, apiFetchOutcome } from './client'

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
