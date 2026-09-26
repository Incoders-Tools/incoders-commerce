import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { BranchesScreen } from './BranchesScreen'
import type { BranchSummary } from '@/api/types'

const central: BranchSummary = { branchId: 'branch-1', branchName: 'Central warehouse' }
const downtown: BranchSummary = { branchId: 'branch-2', branchName: 'Downtown store' }

describe('BranchesScreen', () => {
  const fetchMock = vi.fn()

  beforeEach(() => vi.stubGlobal('fetch', fetchMock))
  afterEach(() => {
    vi.unstubAllGlobals()
    fetchMock.mockReset()
    window.localStorage.clear()
  })

  const listOnce = (branches: BranchSummary[]) =>
    fetchMock.mockResolvedValueOnce(new Response(JSON.stringify(branches), { status: 200 }))

  it('lists branches from GET /account/branches', async () => {
    listOnce([central])

    render(<BranchesScreen />)

    await screen.findByText('Central warehouse')
    expect(fetchMock.mock.calls[0][0]).toBe('/account/branches')
  })

  it('creates a branch and refreshes the list', async () => {
    listOnce([]).mockResolvedValueOnce(new Response(JSON.stringify({ branchId: 'branch-1' }), { status: 201 }))
    listOnce([central])

    const user = userEvent.setup()
    render(<BranchesScreen />)

    await screen.findByText('No branches yet.')
    await user.type(screen.getByLabelText('Branch name'), 'Central warehouse')
    await user.click(screen.getByRole('button', { name: 'Create branch' }))

    await screen.findByText('Central warehouse')
    expect(fetchMock.mock.calls[1][0]).toBe('/account/branches')
    expect(fetchMock.mock.calls[1][1].method).toBe('POST')
    expect(JSON.parse(fetchMock.mock.calls[1][1].body as string)).toEqual({ branchName: 'Central warehouse' })
    // The name field is cleared once the branch exists.
    expect(screen.getByLabelText('Branch name')).toHaveValue('')
  })

  it('surfaces a create failure as an alert without clearing the typed name', async () => {
    listOnce([]).mockRejectedValueOnce(new TypeError('Failed to fetch'))

    const user = userEvent.setup()
    render(<BranchesScreen />)

    await screen.findByText('No branches yet.')
    await user.type(screen.getByLabelText('Branch name'), 'Central warehouse')
    await user.click(screen.getByRole('button', { name: 'Create branch' }))

    expect(await screen.findByRole('alert')).toHaveTextContent(/unable to create branch/i)
    expect(screen.getByLabelText('Branch name')).toHaveValue('Central warehouse')
  })

  it('surfaces a load failure as an alert', async () => {
    fetchMock.mockRejectedValueOnce(new TypeError('Failed to fetch'))

    render(<BranchesScreen />)

    expect(await screen.findByRole('alert')).toHaveTextContent(/unable to load branches/i)
  })

  it('does not claim there are no branches when the load failed', async () => {
    fetchMock.mockRejectedValueOnce(new TypeError('Failed to fetch'))

    render(<BranchesScreen />)

    await screen.findByRole('alert')
    // "No branches yet." is a real rendering of this screen (see the empty
    // state case above), so its absence here is a fact about this state.
    expect(screen.queryByText('No branches yet.')).not.toBeInTheDocument()
    expect(screen.getByTestId('data-view-load-error')).toHaveTextContent(/branches could not be loaded/i)
  })

  it('stops reporting a load failure once a later load succeeds', async () => {
    fetchMock
      .mockRejectedValueOnce(new TypeError('Failed to fetch')) // initial load
      .mockResolvedValueOnce(new Response(JSON.stringify({ branchId: 'branch-1' }), { status: 201 })) // create
    listOnce([central]) // refreshed list

    const user = userEvent.setup()
    render(<BranchesScreen />)

    await screen.findByTestId('data-view-load-error')
    await user.type(screen.getByLabelText('Branch name'), 'Central warehouse')
    await user.click(screen.getByRole('button', { name: 'Create branch' }))

    await screen.findByText('Central warehouse')
    expect(screen.queryByTestId('data-view-load-error')).not.toBeInTheDocument()
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })

  // ---- T4b: the shared data-view layer ----

  it('uses the full width the shell gives it, with no centered narrow column', async () => {
    listOnce([central])

    const { container } = render(<BranchesScreen />)

    await screen.findByText('Central warehouse')
    expect(container.querySelector('.mx-auto')).toBeNull()
    expect(container.querySelector('[class*="max-w-2xl"], [class*="max-w-3xl"]')).toBeNull()
  })

  it('renders the real branch columns for each listed record', async () => {
    listOnce([downtown])

    render(<BranchesScreen />)

    const row = within(await screen.findByRole('table')).getAllByRole('row')[1]
    expect(within(row).getByText('Downtown store')).toBeInTheDocument()
    expect(within(row).getByText('branch-2')).toBeInTheDocument()
  })

  it('shows an empty state when there are no branches', async () => {
    listOnce([])

    render(<BranchesScreen />)

    expect(await screen.findByText('No branches yet.')).toBeInTheDocument()
    expect(screen.queryByRole('table')).not.toBeInTheDocument()
  })

  it('filters the listed branches client-side by name', async () => {
    listOnce([central, downtown])

    const user = userEvent.setup()
    render(<BranchesScreen />)

    await screen.findByText('Central warehouse')
    expect(screen.getByText('Downtown store')).toBeInTheDocument()

    await user.type(screen.getByLabelText(/search branches/i), 'downtown')

    expect(screen.getByText('Downtown store')).toBeInTheDocument()
    expect(screen.queryByText('Central warehouse')).not.toBeInTheDocument()
    expect(fetchMock).toHaveBeenCalledTimes(1)
  })

  it('shows a helpful empty state when the filter matches nothing', async () => {
    listOnce([central, downtown])

    const user = userEvent.setup()
    render(<BranchesScreen />)

    await screen.findByText('Central warehouse')
    await user.type(screen.getByLabelText(/search branches/i), 'zzzz')

    expect(screen.getByText(/no branches match/i)).toBeInTheDocument()
    expect(screen.queryByRole('table')).not.toBeInTheDocument()
    expect(screen.queryAllByTestId('data-view-card')).toHaveLength(0)
  })

  it('switches to the card view and restores that preference on remount', async () => {
    listOnce([central, downtown])
    listOnce([central, downtown])

    const user = userEvent.setup()
    const first = render(<BranchesScreen />)

    await screen.findByText('Central warehouse')
    expect(screen.getByRole('table')).toBeInTheDocument()

    await user.click(screen.getByRole('radio', { name: /vista de tarjetas/i }))

    expect(screen.queryByRole('table')).not.toBeInTheDocument()
    expect(screen.getAllByTestId('data-view-card')).toHaveLength(2)
    expect(window.localStorage.getItem('view:branches')).toBe('cards')

    first.unmount()
    render(<BranchesScreen />)

    await screen.findByText('Central warehouse')
    expect(screen.queryByRole('table')).not.toBeInTheDocument()
    expect(screen.getAllByTestId('data-view-card')).toHaveLength(2)
    expect(screen.getByRole('radio', { name: /vista de tarjetas/i })).toHaveAttribute('aria-checked', 'true')
  })

  it('keeps the view preference separate from the other data screens', async () => {
    window.localStorage.setItem('view:customers', 'cards')
    listOnce([central])

    render(<BranchesScreen />)

    await screen.findByText('Central warehouse')
    // Branches reads `view:branches`, which is unset here, so it stays a table.
    expect(screen.getByRole('table')).toBeInTheDocument()
    await waitFor(() => expect(window.localStorage.getItem('view:branches')).toBeNull())
  })
})
