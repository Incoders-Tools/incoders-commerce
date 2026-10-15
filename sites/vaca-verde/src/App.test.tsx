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

  it('shows the contact details of the flyer as working links, the phone opening WhatsApp', () => {
    render(<App />)

    expect(screen.getByRole('heading', { level: 1 })).toBeInTheDocument()
    for (const link of [screen.getByRole('link', { name: /2161-1045/ }), screen.getByRole('link', { name: 'Escribir por WhatsApp' })]) {
      expect(link).toHaveAttribute('href', 'https://wa.me/5491121611045')
      expect(link).toHaveAttribute('target', '_blank')
    }
    expect(document.querySelector('a[href^="tel:"]')).toBeNull()
    expect(screen.getByRole('link', { name: /@vacaverde\.carne/i })).toHaveAttribute(
      'href',
      'https://www.instagram.com/vacaverde.carne/',
    )
  })

  // owner feedback 2026-10-15, B2: a distributor, with a butcher shop on Ruta 51 as a part of the business.
  it('presents Vaca Verde as a distributor with its butcher shop on Ruta 51', () => {
    render(<App />)

    expect(screen.getByText(/Somos una distribuidora/)).toBeInTheDocument()
    expect(screen.getByText(/Distribuidora con carnicería sobre la Ruta 51/)).toBeInTheDocument()
    expect(screen.queryByText(/Somos una carnicería/)).not.toBeInTheDocument()
  })

  it('publishes the business as a wholesale store with a butcher shop for search engines', () => {
    const { container } = render(<App />)

    const script = container.querySelector('script[type="application/ld+json"]')
    const data = JSON.parse(script?.textContent ?? '{}')
    expect(data['@type']).toEqual(['WholesaleStore', 'ButcherShop'])
    expect(data.telephone).toBe('+541121611045')
    expect(data.address.streetAddress).toBe('Colectora Vitelmo Carbajal 318, Ruta 51')
  })

  // owner feedback 2026-10-15, B4.
  it('signs the footer as engineered by Incoders, linking to its site', () => {
    render(<App />)

    const footer = screen.getByRole('contentinfo')
    expect(within(footer).getByText(/Engineered by/).textContent).toMatch(/^© \d{4} Engineered by Incoders — All rights reserved\.$/)
    expect(within(footer).getByRole('link', { name: 'Incoders' })).toHaveAttribute('href', 'https://www.incoders.com.ar')
  })
})
