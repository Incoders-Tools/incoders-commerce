import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { OrganizationSettingsScreen } from './OrganizationSettingsScreen'

describe('OrganizationSettingsScreen', () => {
  const fetchMock = vi.fn()

  beforeEach(() => vi.stubGlobal('fetch', fetchMock))
  afterEach(() => {
    vi.unstubAllGlobals()
    fetchMock.mockReset()
  })

  const json = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status })

  it('loads the current number format and offers both choices', async () => {
    fetchMock.mockResolvedValueOnce(json({ quantityDecimalSeparator: 'Dot' }))

    render(<OrganizationSettingsScreen />)

    const select = await screen.findByLabelText('Formato de números')
    await waitFor(() => expect(select).toHaveValue('Dot'))
    expect(screen.getByRole('option', { name: 'Coma decimal (1,5)' })).toBeInTheDocument()
    expect(screen.getByRole('option', { name: 'Punto decimal (1.5)' })).toBeInTheDocument()
    expect(fetchMock.mock.calls[0][0]).toBe('/account/organization/settings')
  })

  it('saves the chosen format', async () => {
    fetchMock
      .mockResolvedValueOnce(json({ quantityDecimalSeparator: 'Comma' }))
      .mockResolvedValueOnce(new Response(null, { status: 204 }))

    const user = userEvent.setup()
    render(<OrganizationSettingsScreen />)
    const select = await screen.findByLabelText('Formato de números')
    await waitFor(() => expect(select).toBeEnabled())
    await user.selectOptions(select, 'Dot')
    await user.click(screen.getByRole('button', { name: 'Guardar' }))

    expect(await screen.findByText('Configuración guardada.')).toBeInTheDocument()
    const [url, init] = fetchMock.mock.calls[1]
    expect(url).toBe('/account/organization/settings')
    expect(init.method).toBe('PUT')
    expect(JSON.parse(init.body as string)).toEqual({ quantityDecimalSeparator: 'Dot' })
  })

  it('does not let a failed load be saved over the real setting', async () => {
    fetchMock.mockResolvedValueOnce(json({ title: 'x' }, 500))

    render(<OrganizationSettingsScreen />)

    expect(await screen.findByRole('alert')).toHaveTextContent('No se pudo cargar la configuración.')
    expect(screen.getByRole('button', { name: 'Guardar' })).toBeDisabled()
  })
})
