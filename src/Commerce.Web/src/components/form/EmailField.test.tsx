import { useState } from 'react'
import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { describe, expect, it } from 'vitest'
import { EmailField } from './EmailField'

function Harness({ initial = '', error }: { initial?: string; error?: string | null }) {
  const [value, setValue] = useState(initial)
  return (
    <>
      <EmailField id="email" label="Correo electrónico" value={value} onChange={setValue} error={error} />
      <button type="button">Otro</button>
    </>
  )
}

const INVALID = 'Ingresá un correo electrónico válido, por ejemplo nombre@dominio.com.'
const VALID = 'Correo electrónico válido'

describe('EmailField', () => {
  it('is neutral while empty', () => {
    render(<Harness />)

    const input = screen.getByLabelText('Correo electrónico')
    expect(input).toHaveAttribute('type', 'email')
    expect(input).not.toHaveAttribute('aria-invalid')
    expect(input).not.toHaveAccessibleDescription(VALID)
    expect(screen.queryByText(INVALID)).not.toBeInTheDocument()
  })

  it('shows the valid state live, as soon as the address is valid', async () => {
    const user = userEvent.setup()
    render(<Harness />)

    const input = screen.getByLabelText('Correo electrónico')
    await user.type(input, 'ana@mail.c')
    expect(input).not.toHaveAccessibleDescription(VALID)
    expect(input).toHaveAccessibleDescription(INVALID)
    await user.type(input, 'om')

    expect(input).toHaveAccessibleDescription(VALID)
    expect(screen.getByTestId('email-valid-icon')).toBeInTheDocument()
    expect(input).not.toHaveAttribute('aria-invalid')
  })

  // Owner decision 2026-10-03: flagged on every keystroke until the address is valid; only then the check.
  it('flags every keystroke while the address is not valid yet, then shows the check', async () => {
    const user = userEvent.setup()
    render(<Harness />)

    const input = screen.getByLabelText('Correo electrónico')
    await user.type(input, 'a')
    expect(screen.getByText(INVALID)).toBeInTheDocument()
    expect(input).toHaveAttribute('aria-invalid', 'true')

    await user.type(input, 'na@')
    expect(input).toHaveAccessibleDescription(INVALID)
    expect(screen.queryByTestId('email-valid-icon')).not.toBeInTheDocument()

    await user.type(input, 'mail.com')
    expect(screen.queryByText(INVALID)).not.toBeInTheDocument()
    expect(input).not.toHaveAttribute('aria-invalid')
    expect(screen.getByTestId('email-valid-icon')).toBeInTheDocument()
  })

  it('flags at once a value that can never become a valid address', async () => {
    const user = userEvent.setup()
    render(<Harness />)

    await user.type(screen.getByLabelText('Correo electrónico'), 'ana@@')

    expect(screen.getByText(INVALID)).toBeInTheDocument()
  })

  it('shows an error handed in by the form (a refused submit or the server) over its own state', () => {
    render(<Harness initial="ana@mail.com" error="El servidor rechazó el correo." />)

    const input = screen.getByLabelText('Correo electrónico')
    expect(screen.getByText('El servidor rechazó el correo.')).toBeInTheDocument()
    expect(input).toHaveAttribute('aria-invalid', 'true')
    expect(screen.queryByTestId('email-valid-icon')).not.toBeInTheDocument()
  })
})
