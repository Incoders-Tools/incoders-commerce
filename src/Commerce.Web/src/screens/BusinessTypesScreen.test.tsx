import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { BusinessTypesScreen } from './BusinessTypesScreen'
import type { MasterDataEntry } from '@/api/types'

const bar: MasterDataEntry = {
  id: '33333333-3333-3333-3333-333333333333',
  organizationId: 'org-1',
  name: 'Bar',
  key: 'bar',
  sortOrder: 1,
  isActive: true,
  createdAtUtc: '2024-03-15T12:00:00Z',
  updatedAtUtc: '2024-03-15T12:00:00Z',
}

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } })

describe('BusinessTypesScreen', () => {
  const fetchMock = vi.fn()

  beforeEach(() => {
    vi.stubGlobal('fetch', fetchMock)
  })

  afterEach(() => {
    vi.unstubAllGlobals()
    fetchMock.mockReset()
    window.localStorage.clear()
  })

  it('lists business types from the business-types endpoint', async () => {
    fetchMock.mockResolvedValueOnce(json([bar]))
    render(<BusinessTypesScreen />)

    await screen.findByText('Bar')
    expect(fetchMock.mock.calls[0][0]).toBe('/customers/business-types?includeInactive=true')
    expect(screen.getByRole('heading', { name: 'Tipos de negocio' })).toBeInTheDocument()
  })

  it('maps a duplicate name to a business-type message', async () => {
    fetchMock
      .mockResolvedValueOnce(json([]))
      .mockResolvedValueOnce(json({ error: 'business-type-name-in-use' }, 409))
    const user = userEvent.setup()
    render(<BusinessTypesScreen />)
    await screen.findByText('Todavía no hay tipos de negocio.')

    await user.click(screen.getByRole('button', { name: 'Nuevo tipo de negocio' }))
    await user.type(screen.getByLabelText('Nombre'), 'Bar')
    await user.click(screen.getByRole('button', { name: /^guardar$/i }))

    expect(await screen.findByRole('alert')).toHaveTextContent('Ya existe un tipo de negocio con ese nombre.')
    expect(fetchMock.mock.calls[1][0]).toBe('/customers/business-types')
  })
})
