import { render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { CityPicker, type CityOption } from './CityPicker'

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } })

const geoCity = (id: string, name: string, provinceName: string) => ({
  id,
  indecId: null,
  name,
  provinceId: '82',
  provinceName,
  countryCode: 'AR',
  departmentName: null,
  isActive: true,
  createdAtUtc: '2024-01-01T00:00:00Z',
  updatedAtUtc: '2024-01-01T00:00:00Z',
})

const rosario: CityOption = { id: 'c-ros', name: 'Rosario', provinceName: 'Santa Fe' }

describe('CityPicker', () => {
  const fetchMock = vi.fn()

  beforeEach(() => {
    fetchMock.mockImplementation(async (url: string) => {
      const search = new URL(url, 'http://x').searchParams.get('search') ?? ''
      if (search === 'zzz') return json([])
      return json([geoCity('c-ros', 'Rosario', 'Santa Fe'), geoCity('c-roq', 'Roque Pérez', 'Buenos Aires')])
    })
    vi.stubGlobal('fetch', fetchMock)
  })

  afterEach(() => {
    vi.unstubAllGlobals()
    fetchMock.mockReset()
  })

  it('shows the current value as "Name — Province" without fetching any city', () => {
    render(<CityPicker label="Ciudad" value={rosario} onChange={vi.fn()} />)

    expect(screen.getByRole('combobox', { name: 'Ciudad' })).toHaveValue('Rosario — Santa Fe')
    expect(fetchMock).not.toHaveBeenCalled()
  })

  it('searches the server while typing and lists "Name — Province" options', async () => {
    const user = userEvent.setup()
    render(<CityPicker label="Ciudad" value={null} onChange={vi.fn()} />)

    await user.type(screen.getByRole('combobox', { name: 'Ciudad' }), 'ros')

    expect(await screen.findByRole('option', { name: 'Rosario — Santa Fe' })).toBeInTheDocument()
    expect(screen.getByRole('option', { name: 'Roque Pérez — Buenos Aires' })).toBeInTheDocument()
    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(2))
    const urls = fetchMock.mock.calls.map((call) => new URL(call[0] as string, 'http://x'))
    expect(urls.every((url) => url.pathname === '/geo/cities')).toBe(true)
    // Focus lists the first page; typing "ros" fires ONE debounced search, not one per keystroke.
    expect(urls[0].searchParams.get('search')).toBeNull()
    expect(urls[1].searchParams.get('search')).toBe('ros')
    expect(urls[1].searchParams.get('limit')).toBe('20')
  })

  it('selects an option with the mouse and reports it', async () => {
    const onChange = vi.fn()
    const user = userEvent.setup()
    render(<CityPicker label="Ciudad" value={null} onChange={onChange} />)

    await user.type(screen.getByRole('combobox', { name: 'Ciudad' }), 'ros')
    await user.click(await screen.findByRole('option', { name: 'Roque Pérez — Buenos Aires' }))

    expect(onChange).toHaveBeenCalledWith(
      expect.objectContaining({ id: 'c-roq', name: 'Roque Pérez', provinceName: 'Buenos Aires' }),
    )
    expect(screen.queryByRole('listbox')).not.toBeInTheDocument()
  })

  it('supports the keyboard: arrows move, Enter selects, Escape closes', async () => {
    const onChange = vi.fn()
    const user = userEvent.setup()
    render(<CityPicker label="Ciudad" value={null} onChange={onChange} />)
    const combobox = screen.getByRole('combobox', { name: 'Ciudad' })

    await user.type(combobox, 'ros')
    await screen.findByRole('option', { name: 'Rosario — Santa Fe' })
    expect(combobox).toHaveAttribute('aria-expanded', 'true')

    await user.keyboard('{ArrowDown}{ArrowDown}')
    expect(screen.getByRole('option', { name: 'Roque Pérez — Buenos Aires' })).toHaveAttribute('aria-selected', 'true')
    await user.keyboard('{ArrowUp}{Enter}')
    expect(onChange).toHaveBeenCalledWith(expect.objectContaining({ id: 'c-ros', name: 'Rosario', provinceName: 'Santa Fe' }))

    await user.type(combobox, 'x')
    await screen.findByRole('listbox')
    await user.keyboard('{Escape}')
    expect(screen.queryByRole('listbox')).not.toBeInTheDocument()
    expect(combobox).toHaveAttribute('aria-expanded', 'false')
  })

  it('says so when nothing matches', async () => {
    const user = userEvent.setup()
    render(<CityPicker label="Ciudad" value={null} onChange={vi.fn()} />)

    await user.type(screen.getByRole('combobox', { name: 'Ciudad' }), 'zzz')

    expect(await screen.findByText('Ninguna ciudad coincide.')).toBeInTheDocument()
  })

  it('clears the current value', async () => {
    const onChange = vi.fn()
    const user = userEvent.setup()
    render(<CityPicker label="Ciudad" value={rosario} onChange={onChange} />)

    await user.click(screen.getByRole('button', { name: 'Quitar ciudad' }))

    expect(onChange).toHaveBeenCalledWith(null)
    await waitFor(() => expect(fetchMock).not.toHaveBeenCalled())
  })

  it('shows the placeholder when empty and does not offer to clear', () => {
    render(<CityPicker label="Ciudad" value={null} placeholder="Todas las ciudades" onChange={vi.fn()} />)

    expect(screen.getByRole('combobox', { name: 'Ciudad' })).toHaveAttribute('placeholder', 'Todas las ciudades')
    expect(screen.queryByRole('button', { name: 'Quitar ciudad' })).not.toBeInTheDocument()
  })

  describe('limited to one province', () => {
    const santaFe = (id: string, name: string, departmentName: string | null, postalCode: string | null = null) => ({
      ...geoCity(id, name, 'Santa Fe'),
      departmentName,
      postalCode,
    })

    beforeEach(() => {
      fetchMock.mockImplementation(async () =>
        json([
          santaFe('c-ros', 'Rosario', 'Rosario', '2000'),
          santaFe('c-sj1', 'San José', 'Garay'),
          santaFe('c-sj2', 'San José', 'San Martín'),
        ]),
      )
    })

    it('searches only that province and shows the city name alone', async () => {
      const user = userEvent.setup()
      render(<CityPicker label="Ciudad" value={rosario} provinceId="82" showProvince={false} onChange={vi.fn()} />)

      const combobox = screen.getByRole('combobox', { name: 'Ciudad' })
      expect(combobox).toHaveValue('Rosario')
      await user.click(combobox)

      expect(await screen.findByRole('option', { name: 'Rosario' })).toBeInTheDocument()
      const url = new URL(fetchMock.mock.calls[0][0] as string, 'http://x')
      expect(url.searchParams.get('provinceId')).toBe('82')
    })

    it('tells repeated names apart by their department', async () => {
      const user = userEvent.setup()
      render(<CityPicker label="Ciudad" value={null} provinceId="82" showProvince={false} onChange={vi.fn()} />)

      await user.click(screen.getByRole('combobox', { name: 'Ciudad' }))

      expect(await screen.findByRole('option', { name: 'San José Garay' })).toBeInTheDocument()
      expect(screen.getByRole('option', { name: 'San José San Martín' })).toBeInTheDocument()
      expect(screen.getByRole('option', { name: 'Rosario' })).toBeInTheDocument()
    })

    it('reports the postal code of the chosen city', async () => {
      const onChange = vi.fn()
      const user = userEvent.setup()
      render(<CityPicker label="Ciudad" value={null} provinceId="82" showProvince={false} onChange={onChange} />)

      await user.click(screen.getByRole('combobox', { name: 'Ciudad' }))
      await user.click(await screen.findByRole('option', { name: 'Rosario' }))

      expect(onChange).toHaveBeenCalledWith(expect.objectContaining({ id: 'c-ros', provinceId: '82', postalCode: '2000' }))
    })

    it('can be disabled (no province chosen yet) and then never searches', async () => {
      const user = userEvent.setup()
      render(<CityPicker label="Ciudad" value={null} disabled onChange={vi.fn()} />)

      const combobox = screen.getByRole('combobox', { name: 'Ciudad' })
      expect(combobox).toBeDisabled()
      await user.click(combobox)
      expect(fetchMock).not.toHaveBeenCalled()
    })
  })
})
