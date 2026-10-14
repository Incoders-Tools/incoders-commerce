import { render, screen, within } from '@testing-library/react'
import { describe, expect, it } from 'vitest'
import { App } from './App'

// vitest.config.ts injects __APP_URL__ = https://app.example.test
describe('Vaca Verde site', () => {
  it('sends every "Hacer un pedido" button to the client ordering screen of the web app', () => {
    render(<App />)

    const orderLinks = screen.getAllByRole('link', { name: 'Hacer un pedido' })
    expect(orderLinks.length).toBeGreaterThan(0)
    for (const link of orderLinks) {
      expect(link).toHaveAttribute('href', 'https://app.example.test/order')
    }
  })

  it('offers staff and administrators the sign-in of the web app inside the access section', () => {
    render(<App />)

    const access = screen.getByRole('region', { name: /ingresar a vaca verde/i })
    expect(within(access).getByRole('link', { name: 'Hacer un pedido' })).toHaveAttribute(
      'href',
      'https://app.example.test/order',
    )
    expect(within(access).getByRole('link', { name: 'Ingresar al sistema' })).toHaveAttribute(
      'href',
      'https://app.example.test/login',
    )
  })

  it('points the header "Ingresar" link at the access section', () => {
    render(<App />)

    const header = screen.getByRole('banner')
    expect(within(header).getByRole('link', { name: 'Ingresar' })).toHaveAttribute('href', '#ingresar')
  })

  it('shows the contact details of the flyer as working links', () => {
    render(<App />)

    expect(screen.getByRole('heading', { level: 1 })).toBeInTheDocument()
    expect(screen.getByRole('link', { name: /2161-1045/ })).toHaveAttribute('href', 'tel:+541121611045')
    expect(screen.getByRole('link', { name: /@vacaverde\.carne/i })).toHaveAttribute(
      'href',
      'https://www.instagram.com/vacaverde.carne/',
    )
  })

  it('publishes the business as a ButcherShop for search engines', () => {
    const { container } = render(<App />)

    const script = container.querySelector('script[type="application/ld+json"]')
    const data = JSON.parse(script?.textContent ?? '{}')
    expect(data['@type']).toBe('ButcherShop')
    expect(data.telephone).toBe('+541121611045')
    expect(data.address.streetAddress).toBe('Colectora Vitelmo Carbajal 318, Ruta 51')
  })
})
