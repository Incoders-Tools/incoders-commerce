import { useState } from 'react'
import { fireEvent, render, screen } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { MoneyField, MoneyInput } from './MoneyField'

function Harness({ compact = false, maxDecimals }: { compact?: boolean; maxDecimals?: number }) {
  const [value, setValue] = useState('')
  return <MoneyInput aria-label="Importe" value={value} onChange={setValue} compact={compact} maxDecimals={maxDecimals} />
}

const readBack = () => screen.queryByText(/^Se toma como/)

describe('MoneyInput', () => {
  it('offers a decimal keyboard and the shared example, and says how it read the amount', () => {
    render(<Harness />)
    const input = screen.getByLabelText('Importe')
    expect(input).toHaveAttribute('inputmode', 'decimal')
    expect(input).toHaveAttribute('placeholder', 'Ej.: 15.000 o 15.000,50')
    expect(readBack()).toBeNull()

    fireEvent.change(input, { target: { value: '15.000' } })
    expect(readBack()?.textContent?.replace(/\s/g, ' ')).toBe('Se toma como $ 15.000,00')

    fireEvent.change(input, { target: { value: '15.50' } })
    expect(readBack()?.textContent?.replace(/\s/g, ' ')).toBe('Se toma como $ 15,50')

    fireEvent.change(input, { target: { value: '1,234.56' } })
    expect(readBack()).toBeNull()
  })

  it('reads up to the decimals the field takes', () => {
    render(<Harness maxDecimals={4} />)
    fireEvent.change(screen.getByLabelText('Importe'), { target: { value: '12,3456' } })
    expect(readBack()?.textContent?.replace(/\s/g, ' ')).toBe('Se toma como $ 12,3456')
  })

  it('inside a row, shows the short example and no read-back line', () => {
    render(<Harness compact />)
    const input = screen.getByLabelText('Importe')
    expect(input).toHaveAttribute('placeholder', '15.000,50')
    fireEvent.change(input, { target: { value: '15.000' } })
    expect(readBack()).toBeNull()
  })
})

describe('MoneyField', () => {
  it('labels the money input', () => {
    render(<MoneyField id="salary" label="Sueldo" value="650.000" onChange={() => {}} />)
    expect(screen.getByLabelText('Sueldo')).toHaveAttribute('placeholder', 'Ej.: 15.000 o 15.000,50')
    expect(readBack()?.textContent?.replace(/\s/g, ' ')).toBe('Se toma como $ 650.000,00')
  })
})
