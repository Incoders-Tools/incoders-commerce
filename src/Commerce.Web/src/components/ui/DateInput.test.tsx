import { fireEvent, render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { useState, type FormEvent } from 'react'
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
        <button type="button" onClick={() => setValue('')}>
          Vaciar
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

  it('keeps what is typed when the screen does not hold the value, and starts over when the screen empties it', async () => {
    const user = userEvent.setup()
    render(
      <>
        <Input type="date" aria-label="Libre" />
        <Harness />
      </>,
    )

    await user.type(screen.getByLabelText('Libre'), '13102026')
    expect(screen.getByLabelText('Libre')).toHaveValue('13/10/2026')

    await user.type(screen.getByLabelText('Fecha'), '13102026')
    fireEvent.click(screen.getByRole('button', { name: 'Vaciar' }))
    expect(screen.getByLabelText('Fecha')).toHaveValue('')
    await user.type(screen.getByLabelText('Fecha'), '05')
    expect(screen.getByLabelText('Fecha')).toHaveValue('05/')
  })

  it('keeps a half-typed date while the screen holds the date it had, until the screen sets another', async () => {
    const user = userEvent.setup()
    render(<Harness initial="2026-10-13" />)

    await user.type(screen.getByLabelText('Fecha'), '{backspace}{backspace}')
    expect(screen.getByLabelText('Fecha')).toHaveValue('13/10/20')
    expect(screen.getByTestId('iso')).toHaveTextContent(/^$/)
    fireEvent.click(screen.getByRole('button', { name: 'Poner 5 de enero' }))
    expect(screen.getByLabelText('Fecha')).toHaveValue('05/01/2026')
  })

  it('shows a date the screen sets again after emptying the field, even the one typed before', async () => {
    const user = userEvent.setup()
    render(<Harness />)

    await user.type(screen.getByLabelText('Fecha'), '05012026')
    fireEvent.click(screen.getByRole('button', { name: 'Vaciar' }))
    expect(screen.getByLabelText('Fecha')).toHaveValue('')
    fireEvent.click(screen.getByRole('button', { name: 'Poner 5 de enero' }))
    expect(screen.getByLabelText('Fecha')).toHaveValue('05/01/2026')
  })
  // owner feedback follow-up: a date typed outside min/max is refused as the browser's own date field refuses it.
  it('marks a typed date outside its min or max as invalid, so the form does not send it', async () => {
    const user = userEvent.setup()
    const submitted = vi.fn((event: FormEvent) => event.preventDefault())
    render(
      <form onSubmit={submitted}>
        <label htmlFor="limited">Fecha</label>
        <Input id="limited" type="date" min="2026-10-01" max="2026-10-13" defaultValue="" onChange={() => {}} />
        <button type="submit">Enviar</button>
      </form>,
    )
    const field = screen.getByLabelText('Fecha') as HTMLInputElement

    await user.type(field, '30092026')
    expect(field).toBeInvalid()
    expect(field.validationMessage).toBe('La fecha tiene que ser desde el 01/10/2026.')
    await user.click(screen.getByRole('button', { name: 'Enviar' }))
    expect(submitted).not.toHaveBeenCalled()

    await user.clear(field)
    await user.type(field, '14102026')
    expect(field.validationMessage).toBe('La fecha tiene que ser hasta el 13/10/2026.')

    await user.clear(field)
    await user.type(field, '05102026')
    expect(field).toBeValid()
    await user.click(screen.getByRole('button', { name: 'Enviar' }))
    expect(submitted).toHaveBeenCalledTimes(1)
  })
})
