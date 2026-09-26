import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { OrganizationBrandingForm } from './OrganizationBrandingForm'
import type { OrganizationSummary } from '@/api/types'

const acme: OrganizationSummary = {
  id: '11111111-1111-1111-1111-111111111111',
  name: 'Acme Co',
  createdAt: '2024-01-01T00:00:00Z',
}

/**
 * T5b: minimal organization branding form — logoUrl + primaryColor only, no
 * upload, no date format/geolocation/plan (see the task file: user decision
 * "lo mas simple posible, a futuro ampliamos"). Same mocking style as
 * `OrganizationsScreen.test.tsx`.
 */
describe('OrganizationBrandingForm', () => {
  const fetchMock = vi.fn()
  const onSaved = vi.fn()
  const onCancel = vi.fn()

  beforeEach(() => vi.stubGlobal('fetch', fetchMock))
  afterEach(() => {
    vi.unstubAllGlobals()
    fetchMock.mockReset()
    onSaved.mockReset()
    onCancel.mockReset()
  })

  const getOnce = (body: unknown) => fetchMock.mockResolvedValueOnce(new Response(JSON.stringify(body), { status: 200 }))

  it('loads the organization\'s current branding and prefills the fields', async () => {
    getOnce({ logoUrl: 'https://cdn.example.com/logo.png', primaryColor: '#336699' })

    render(<OrganizationBrandingForm organization={acme} onSaved={onSaved} onCancel={onCancel} />)

    expect(await screen.findByLabelText('Logo URL')).toHaveValue('https://cdn.example.com/logo.png')
    expect(screen.getByLabelText('Primary color')).toHaveValue('#336699')
    expect(fetchMock.mock.calls[0][0]).toBe(`/account/organizations/${acme.id}/branding`)
  })

  it('renders empty fields when the organization has no branding yet', async () => {
    getOnce({ logoUrl: null, primaryColor: null })

    render(<OrganizationBrandingForm organization={acme} onSaved={onSaved} onCancel={onCancel} />)

    await screen.findByLabelText('Logo URL')
    expect(screen.getByLabelText('Logo URL')).toHaveValue('')
    expect(screen.getByLabelText('Primary color')).toHaveValue('')
  })

  it('shows a live logo preview once a URL is typed, and a color swatch once a color is picked', async () => {
    getOnce({ logoUrl: null, primaryColor: null })

    const user = userEvent.setup()
    render(<OrganizationBrandingForm organization={acme} onSaved={onSaved} onCancel={onCancel} />)

    await screen.findByLabelText('Logo URL')
    expect(screen.queryByAltText(/logo preview/i)).not.toBeInTheDocument()

    await user.type(screen.getByLabelText('Logo URL'), 'https://cdn.example.com/logo.png')
    expect(screen.getByAltText(/logo preview/i)).toHaveAttribute('src', 'https://cdn.example.com/logo.png')

    await user.clear(screen.getByLabelText('Primary color'))
    await user.type(screen.getByLabelText('Primary color'), '#ff0000')
    expect(screen.getByTestId('primary-color-swatch')).toHaveStyle({ backgroundColor: '#ff0000' })
  })

  it('saves the typed logo URL and color', async () => {
    getOnce({ logoUrl: null, primaryColor: null })
    fetchMock.mockResolvedValueOnce(new Response(null, { status: 204 }))

    const user = userEvent.setup()
    render(<OrganizationBrandingForm organization={acme} onSaved={onSaved} onCancel={onCancel} />)

    await screen.findByLabelText('Logo URL')
    await user.type(screen.getByLabelText('Logo URL'), 'https://cdn.example.com/logo.png')
    await user.clear(screen.getByLabelText('Primary color'))
    await user.type(screen.getByLabelText('Primary color'), '#336699')
    await user.click(screen.getByRole('button', { name: 'Save' }))

    expect(fetchMock.mock.calls[1][0]).toBe(`/account/organizations/${acme.id}/branding`)
    expect(fetchMock.mock.calls[1][1].method).toBe('PUT')
    expect(JSON.parse(fetchMock.mock.calls[1][1].body as string)).toEqual({
      logoUrl: 'https://cdn.example.com/logo.png',
      primaryColor: '#336699',
    })
    expect(onSaved).toHaveBeenCalled()
  })

  it('sends null for a field left blank, clearing it', async () => {
    getOnce({ logoUrl: 'https://cdn.example.com/logo.png', primaryColor: '#336699' })
    fetchMock.mockResolvedValueOnce(new Response(null, { status: 204 }))

    const user = userEvent.setup()
    render(<OrganizationBrandingForm organization={acme} onSaved={onSaved} onCancel={onCancel} />)

    await screen.findByLabelText('Logo URL')
    await user.clear(screen.getByLabelText('Logo URL'))
    await user.clear(screen.getByLabelText('Primary color'))
    await user.click(screen.getByRole('button', { name: 'Save' }))

    expect(JSON.parse(fetchMock.mock.calls[1][1].body as string)).toEqual({
      logoUrl: null,
      primaryColor: null,
    })
    expect(onSaved).toHaveBeenCalled()
  })

  it('shows the API error and does not call onSaved when the update is rejected', async () => {
    getOnce({ logoUrl: null, primaryColor: null })
    fetchMock.mockResolvedValueOnce(
      new Response(JSON.stringify({ title: 'logoUrl must be an absolute http or https URL.' }), { status: 400 }),
    )

    const user = userEvent.setup()
    render(<OrganizationBrandingForm organization={acme} onSaved={onSaved} onCancel={onCancel} />)

    await screen.findByLabelText('Logo URL')
    await user.click(screen.getByRole('button', { name: 'Save' }))

    expect(await screen.findByRole('alert')).toHaveTextContent(/logoUrl must be an absolute http or https URL/i)
    expect(onSaved).not.toHaveBeenCalled()
  })

  it('disables Save and shows a Retry action when the branding load fails, without clearing the stored branding', async () => {
    fetchMock.mockRejectedValueOnce(new Error('Commerce.Cloud.Api is unreachable.'))

    render(<OrganizationBrandingForm organization={acme} onSaved={onSaved} onCancel={onCancel} />)

    expect(await screen.findByRole('alert')).toHaveTextContent(/no está disponible/i)
    expect(screen.getByRole('button', { name: 'Save' })).toBeDisabled()
    expect(screen.getByRole('button', { name: 'Retry' })).toBeInTheDocument()

    const user = userEvent.setup()
    // R3-branding-load-failure-save-clears: Save must never fire while the
    // load is known to have failed — clicking a disabled button is a no-op,
    // but assert the update call never happens either.
    await user.click(screen.getByRole('button', { name: 'Save' }))
    expect(fetchMock).toHaveBeenCalledTimes(1)
  })

  it('re-enables Save after a successful Retry', async () => {
    fetchMock.mockRejectedValueOnce(new Error('network down'))
    getOnce({ logoUrl: 'https://cdn.example.com/logo.png', primaryColor: '#336699' })

    const user = userEvent.setup()
    render(<OrganizationBrandingForm organization={acme} onSaved={onSaved} onCancel={onCancel} />)

    await screen.findByRole('button', { name: 'Retry' })
    await user.click(screen.getByRole('button', { name: 'Retry' }))

    await waitFor(() => expect(screen.getByLabelText('Logo URL')).toHaveValue('https://cdn.example.com/logo.png'))
    expect(screen.queryByRole('button', { name: 'Retry' })).not.toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Save' })).not.toBeDisabled()
  })

  it('cancels without saving', async () => {
    getOnce({ logoUrl: null, primaryColor: null })

    const user = userEvent.setup()
    render(<OrganizationBrandingForm organization={acme} onSaved={onSaved} onCancel={onCancel} />)

    await screen.findByLabelText('Logo URL')
    await user.click(screen.getByRole('button', { name: 'Cancel' }))

    expect(onCancel).toHaveBeenCalled()
    expect(fetchMock).toHaveBeenCalledTimes(1)
  })
})
