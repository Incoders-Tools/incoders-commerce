import { fireEvent, render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { useState } from 'react'
import { describe, expect, it, vi } from 'vitest'
import { Input } from './input'

/**
 * owner feedback P3: every date is typed and read dd/mm/aaaa, whatever the browser's language. The screens keep getting
 * and giving ISO dates (`2026-10-13`), as with the browser's own date field.
 */
describe('DateInput (Input type="date")', () => {
  function Harness({ initial = '', onValue }: { initial?: string; onValue?: (value: string) => void }) {
    const [value, setValue] = useState(initial)
    return (
      <>
        <label htmlFor="when">Fecha</label>
        <Input
          id="when"
          type="date"
          value={value}
          onChange={(e) => {
            setValue(e.target.value)
            onValue?.(e.target.value)
          }}
        />
        <button type="button" onClick={() => setValue('2026-01-05')}>
          Poner 5 de enero
        </button>
        <output data-testid="iso">{value}</output>
      </>
    )
  }

  it('shows an ISO date as dd/mm/aaaa, says how to type one, and offers the calendar', () => {
    render(<Harness initial="2026-10-13" />)
    const field = screen.getByLabelText('Fecha')
    expect(field).toHaveValue('13/10/2026')
    expect(field).toHaveAttribute('placeholder', 'dd/mm/aaaa')
    expect(field).toHaveAttribute('type', 'text')
    expect(screen.getByRole('button', { name: 'Elegir fecha' })).toBeInTheDocument()
  })

  it('puts the slashes while typing and gives the screen the ISO date once complete', async () => {
    const onValue = vi.fn()
    const user = userEvent.setup()
    render(<Harness onValue={onValue} />)

    await user.type(screen.getByLabelText('Fecha'), '1310')
    expect(screen.getByLabelText('Fecha')).toHaveValue('13/10/')
    expect(screen.getByTestId('iso')).toHaveTextContent(/^$/)
    await user.type(screen.getByLabelText('Fecha'), '2026')
    expect(screen.getByLabelText('Fecha')).toHaveValue('13/10/2026')
    expect(screen.getByTestId('iso')).toHaveTextContent('2026-10-13')
    expect(onValue).toHaveBeenLastCalledWith('2026-10-13')
  })

  it('gives nothing for a day that does not exist, and lets the slashes be erased', async () => {
    const user = userEvent.setup()
    render(<Harness />)

    await user.type(screen.getByLabelText('Fecha'), '31022026')
    expect(screen.getByLabelText('Fecha')).toHaveValue('31/02/2026')
    expect(screen.getByTestId('iso')).toHaveTextContent(/^$/)
    await user.type(screen.getByLabelText('Fecha'), '{backspace}{backspace}{backspace}{backspace}{backspace}')
    expect(screen.getByLabelText('Fecha')).toHaveValue('31/02')
  })

  it('follows a date the screen sets, and takes a pasted ISO date', () => {
    render(<Harness initial="2026-10-13" />)
    fireEvent.click(screen.getByRole('button', { name: 'Poner 5 de enero' }))
    expect(screen.getByLabelText('Fecha')).toHaveValue('05/01/2026')

    fireEvent.change(screen.getByLabelText('Fecha'), { target: { value: '2026-10-11' } })
    expect(screen.getByLabelText('Fecha')).toHaveValue('11/10/2026')
    expect(screen.getByTestId('iso')).toHaveTextContent('2026-10-11')
  })
})
