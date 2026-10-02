import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { SupplierCategoriesScreen } from './SupplierCategoriesScreen'
import { categoryEntry, json } from './supplierFixtures'

describe('SupplierCategoriesScreen', () => {
  const fetchMock = vi.fn()

  beforeEach(() => vi.stubGlobal('fetch', fetchMock))
  afterEach(() => {
    vi.unstubAllGlobals()
    fetchMock.mockReset()
    window.localStorage.clear()
  })

  it('lists supplier categories from the suppliers/categories endpoint', async () => {
    fetchMock.mockResolvedValueOnce(json([categoryEntry('c1', 'Carne')]))
    render(<SupplierCategoriesScreen />)

    await screen.findByText('Carne')
    expect(fetchMock.mock.calls[0][0]).toBe('/suppliers/categories?includeInactive=true')
    expect(screen.getByRole('heading', { name: 'Rubros de proveedor' })).toBeInTheDocument()
  })

  it('maps a duplicate name to a supplier-category message', async () => {
    fetchMock
      .mockResolvedValueOnce(json([]))
      .mockResolvedValueOnce(json({ error: 'supplier-category-name-in-use' }, 409))
    const user = userEvent.setup()
    render(<SupplierCategoriesScreen />)
    await screen.findByText('Todavía no hay rubros de proveedor.')

    await user.click(screen.getByRole('button', { name: 'Nuevo rubro' }))
    await user.type(screen.getByLabelText('Nombre'), 'Carne')
    await user.click(screen.getByRole('button', { name: 'Guardar' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('Ya existe un rubro con ese nombre.')
  })
})
