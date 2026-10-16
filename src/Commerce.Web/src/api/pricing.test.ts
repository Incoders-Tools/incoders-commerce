import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { floorViolationsOf, publishEntriesBatch } from './pricing'

describe('publishEntriesBatch', () => {
  const fetchMock = vi.fn()

  beforeEach(() => vi.stubGlobal('fetch', fetchMock))
  afterEach(() => {
    vi.unstubAllGlobals()
    fetchMock.mockReset()
  })

  it('posts every entry in one batch and returns what was published', async () => {
    fetchMock.mockResolvedValueOnce(new Response(JSON.stringify({ published: 2, entries: [] }), { status: 200 }))

    const result = await publishEntriesBatch('list-1', {
      effectiveFrom: '2026-10-05',
      entries: [
        { presentationId: 'p-1', unitPrice: 110 },
        { presentationId: 'p-2', unitPrice: 2750.61 },
      ],
    })

    expect(result.published).toBe(2)
    const [url, init] = fetchMock.mock.calls[0]
    expect(url).toBe('/pricing/price-lists/list-1/entries/batch')
    expect(init.method).toBe('POST')
    expect(JSON.parse(init.body as string)).toEqual({
      effectiveFrom: '2026-10-05',
      entries: [
        { presentationId: 'p-1', unitPrice: 110 },
        { presentationId: 'p-2', unitPrice: 2750.61 },
      ],
    })
  })

  it('surfaces a floor refusal as violations', async () => {
    const violation = { presentationId: 'p-1', price: 90, floorPrice: 100 }
    fetchMock.mockResolvedValueOnce(
      new Response(JSON.stringify({ error: 'price-below-floor', violations: [violation] }), { status: 409 }),
    )

    const error = await publishEntriesBatch('list-1', { effectiveFrom: null, entries: [{ presentationId: 'p-1', unitPrice: 90 }] }).catch(
      (err: unknown) => err,
    )

    expect(floorViolationsOf(error)).toEqual([violation])
  })
})
