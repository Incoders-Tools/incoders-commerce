import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { OrganizationSettingsScreen } from './OrganizationSettingsScreen'

const priceList = (id: string, name: string) => ({
  id,
  name,
  organizationId: 'org-1',
  branchId: 'branch-1',
  isDefault: false,
  createdAtUtc: '2024-01-01T00:00:00Z',
  createdByUserId: 'user-1',
})

describe('OrganizationSettingsScreen', () => {
  const fetchMock = vi.fn()
  let settings: Record<string, unknown>
  let settingsStatus: number

  beforeEach(() => {
    settings = { quantityDecimalSeparator: 'Comma', defaultCustomerPriceListId: null }
    settingsStatus = 200
    fetchMock.mockImplementation(async (url: string, init?: RequestInit) => {
      if (url === '/pricing/price-lists') return json([priceList('list-reparto', 'Reparto'), priceList('list-mostrador', 'Mostrador')])
      if (init?.method === 'PUT') return new Response(null, { status: 204 })
      return json(settings, settingsStatus)
    })
    vi.stubGlobal('fetch', fetchMock)
  })
  afterEach(() => {
    vi.unstubAllGlobals()
    fetchMock.mockReset()
  })

  const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status })
  const putBodies = () =>
    fetchMock.mock.calls.filter(([, init]) => init?.method === 'PUT').map(([, init]) => JSON.parse(init.body as string))

  it('loads the current number format and offers both choices', async () => {
    settings = { quantityDecimalSeparator: 'Dot', defaultCustomerPriceListId: null }

    render(<OrganizationSettingsScreen />)

    const select = await screen.findByLabelText('Formato de números')
    await waitFor(() => expect(select).toHaveValue('Dot'))
    expect(screen.getByRole('option', { name: 'Coma decimal (1,5)' })).toBeInTheDocument()
    expect(screen.getByRole('option', { name: 'Punto decimal (1.5)' })).toBeInTheDocument()
    expect(fetchMock.mock.calls.map(([url]) => url)).toContain('/account/organization/settings')
  })

  it('saves the chosen format', async () => {
    const user = userEvent.setup()
    render(<OrganizationSettingsScreen />)
    const select = await screen.findByLabelText('Formato de números')
    await waitFor(() => expect(select).toBeEnabled())
    await user.selectOptions(select, 'Dot')
    await user.click(screen.getByRole('button', { name: 'Guardar' }))

    expect(await screen.findByText('Configuración guardada.')).toBeInTheDocument()
    expect(putBodies()).toEqual([{ quantityDecimalSeparator: 'Dot' }])
  })

  it('does not let a failed load be saved over the real setting', async () => {
    settingsStatus = 500
    settings = { title: 'x' }

    render(<OrganizationSettingsScreen />)

    expect(await screen.findByRole('alert')).toHaveTextContent('No se pudo cargar la configuración.')
    expect(screen.getByRole('button', { name: 'Guardar' })).toBeDisabled()
  })

  it('shows the default list for new customers and saves a change without touching the separator', async () => {
    settings = { quantityDecimalSeparator: 'Dot', defaultCustomerPriceListId: 'list-reparto' }
    const user = userEvent.setup()
    render(<OrganizationSettingsScreen />)

    const select = await screen.findByLabelText('Lista por defecto para clientes nuevos')
    await waitFor(() => expect(select).toHaveValue('list-reparto'))
    await user.selectOptions(select, 'list-mostrador')
    await user.click(screen.getByRole('button', { name: 'Guardar' }))

    expect(await screen.findByText('Configuración guardada.')).toBeInTheDocument()
    expect(putBodies()).toEqual([{ defaultCustomerPriceListId: 'list-mostrador' }])
  })

  it('clears the default list when "Sin lista" is chosen', async () => {
    settings = { quantityDecimalSeparator: 'Comma', defaultCustomerPriceListId: 'list-reparto' }
    const user = userEvent.setup()
    render(<OrganizationSettingsScreen />)

    const select = await screen.findByLabelText('Lista por defecto para clientes nuevos')
    await waitFor(() => expect(select).toHaveValue('list-reparto'))
    await user.selectOptions(select, '')
    await user.click(screen.getByRole('button', { name: 'Guardar' }))

    await screen.findByText('Configuración guardada.')
    expect(putBodies()).toEqual([{ clearDefaultCustomerPriceList: true }])
  })
})
