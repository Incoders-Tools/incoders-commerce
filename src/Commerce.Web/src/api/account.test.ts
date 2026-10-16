import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { listUsers } from './account'

describe('listUsers', () => {
  const fetchMock = vi.fn()

  beforeEach(() => vi.stubGlobal('fetch', fetchMock))
  afterEach(() => {
    vi.unstubAllGlobals()
    fetchMock.mockReset()
  })

  it('defaults branchIds to an empty list when the server omits it', async () => {
    const legacyUser = { userId: 'user-1', email: 'a@b.test', roles: [] }
    fetchMock.mockResolvedValueOnce(new Response(JSON.stringify([legacyUser]), { status: 200 }))

    const users = await listUsers()

    expect(users[0].branchIds).toEqual([])
  })

  it('keeps the branchIds the server returns', async () => {
    const user = { userId: 'user-1', email: 'a@b.test', roles: [], branchIds: ['branch-a'] }
    fetchMock.mockResolvedValueOnce(new Response(JSON.stringify([user]), { status: 200 }))

    const users = await listUsers()

    expect(users[0].branchIds).toEqual(['branch-a'])
  })
})
