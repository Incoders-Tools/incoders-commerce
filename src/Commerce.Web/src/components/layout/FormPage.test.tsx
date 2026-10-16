import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it, vi } from 'vitest'
import { FormPage } from './FormPage'

describe('FormPage', () => {
  it('renders the title and description', () => {
    render(
      <FormPage title="New customer" description="Onboard a new account." onBack={vi.fn()}>
        <p>body</p>
      </FormPage>,
    )

    expect(screen.getByRole('heading', { name: 'New customer' })).toBeInTheDocument()
    expect(screen.getByText('Onboard a new account.')).toBeInTheDocument()
  })

  it('renders the body content full-width, not boxed inside a card', () => {
    const { container } = render(
      <FormPage title="New customer" onBack={vi.fn()}>
        <p>body content</p>
      </FormPage>,
    )

    expect(screen.getByText('body content')).toBeInTheDocument()
    expect(container.querySelector('.rounded-lg.border')).toBeNull()
  })

  it('calls onBack when the back action is activated, with an accessible name that says where it returns to', async () => {
    const onBack = vi.fn()
    const user = userEvent.setup()
    render(
      <FormPage title="New customer" onBack={onBack} backLabel="Back to customers">
        <p>body</p>
      </FormPage>,
    )

    const back = screen.getByRole('button', { name: 'Back to customers' })
    await user.click(back)

    expect(onBack).toHaveBeenCalledTimes(1)
  })

  it('marks the back icon as decorative so the accessible name is only the label text', () => {
    render(
      <FormPage title="New customer" onBack={vi.fn()} backLabel="Back to customers">
        <p>body</p>
      </FormPage>,
    )

    const back = screen.getByRole('button', { name: 'Back to customers' })
    const icon = back.querySelector('svg')
    expect(icon).not.toBeNull()
    expect(icon).toHaveAttribute('aria-hidden', 'true')
  })

  it('falls back to a generic "Volver" label when none is given', () => {
    render(
      <FormPage title="New customer" onBack={vi.fn()}>
        <p>body</p>
      </FormPage>,
    )

    expect(screen.getByRole('button', { name: 'Volver' })).toBeInTheDocument()
  })

  it('renders an optional footer/actions area under the body', () => {
    render(
      <FormPage title="New customer" onBack={vi.fn()} footer={<button type="button">Save</button>}>
        <p>body</p>
      </FormPage>,
    )

    expect(screen.getByRole('button', { name: 'Save' })).toBeInTheDocument()
  })

  it('renders no footer area when none is given', () => {
    const { container } = render(
      <FormPage title="New customer" onBack={vi.fn()}>
        <p>body</p>
      </FormPage>,
    )

    expect(container.querySelector('.border-t')).toBeNull()
  })
})
