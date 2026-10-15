import { business } from './content'
import { About } from './sections/About'
import { Access } from './sections/Access'
import { Cuts } from './sections/Cuts'
import { Hero } from './sections/Hero'
import { SiteFooter } from './sections/SiteFooter'
import { SiteHeader } from './sections/SiteHeader'
import { Visit } from './sections/Visit'

/** schema.org data so search engines can show the shop as a local business. */
const structuredData = {
  '@context': 'https://schema.org',
  // A distributor, with its butcher shop on Ruta 51 as a part of the business.
  '@type': ['WholesaleStore', 'ButcherShop'],
  name: business.name,
  foundingDate: String(business.foundedYear),
  telephone: business.phone,
  address: {
    '@type': 'PostalAddress',
    streetAddress: business.address,
    addressCountry: 'AR',
  },
  sameAs: [business.instagramHref],
}

export function App() {
  return (
    <>
      <a
        href="#contenido"
        className="sr-only focus:not-sr-only focus:fixed focus:top-3 focus:left-3 focus:z-30 focus:rounded-full focus:bg-field focus:px-5 focus:py-3 focus:text-paper"
      >
        Ir al contenido
      </a>
      <SiteHeader />
      <main id="contenido">
        <Hero />
        <About />
        <Cuts />
        <Access />
        <Visit />
      </main>
      <SiteFooter />
      <script
        type="application/ld+json"
        dangerouslySetInnerHTML={{ __html: JSON.stringify(structuredData).replace(/</g, '\\u003c') }}
      />
    </>
  )
}
