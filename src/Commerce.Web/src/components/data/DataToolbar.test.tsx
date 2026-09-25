import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { DataToolbar } from './DataToolbar'
import { PageHeader } from './PageHeader'

describe('DataToolbar', () => {
  it('renders an accessible search field and the view switch together', () => {
    render(
      <DataToolbar
        searchValue=""
        onSearchChange={() => {}}
        searchLabel="Search presentations"
        view="table"
        onViewChange={() => {}}
      />,
    )

    expect(screen.getByLabelText('Search presentations')).toBeInTheDocument()
    expect(screen.getByRole('radiogroup', { name: /view/i })).toBeInTheDocument()
  })

  it('reports typed search text', async () => {
    const onSearchChange = vi.fn()
    const user = userEvent.setup()
    render(
      <DataToolbar
        searchValue=""
        onSearchChange={onSearchChange}
        searchLabel="Search presentations"
        view="table"
        onViewChange={() => {}}
      />,
    )

    await user.type(screen.getByLabelText('Search presentations'), 'a')

    expect(onSearchChange).toHaveBeenCalledWith('a')
  })
})

describe('PageHeader', () => {
  it('renders the title as a heading, the description and the action slot', () => {
    render(
      <PageHeader
        title="Catalog"
        description="Presentations available to sell."
        actions={<button type="button">New</button>}
      />,
    )

    expect(screen.getByRole('heading', { name: 'Catalog' })).toBeInTheDocument()
    expect(screen.getByText('Presentations available to sell.')).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'New' })).toBeInTheDocument()
  })

  it('renders without a description or actions', () => {
    render(<PageHeader title="Catalog" />)

    expect(screen.getByRole('heading', { name: 'Catalog' })).toBeInTheDocument()
  })
})
