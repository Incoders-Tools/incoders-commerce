/**
 * Business facts shown on the site, taken from Vaca Verde's printed flyer.
 * One source for the page and the structured data search engines read.
 */
export const business = {
  name: 'Vaca Verde',
  foundedYear: 2025,
  phoneDisplay: '(+54) 11 2161-1045',
  /** E.164, for the structured data. */
  phone: '+541121611045',
  /** The phone opens a WhatsApp chat (an Argentine mobile on WhatsApp carries the 9 after the country code). */
  whatsappHref: 'https://wa.me/5491121611045',
  address: 'Colectora Vitelmo Carbajal 318, Ruta 51',
  mapsHref: `https://www.google.com/maps/search/?api=1&query=${encodeURIComponent(
    'Vaca Verde carnicería, Colectora Vitelmo Carbajal 318, Ruta 51',
  )}`,
  instagramHandle: '@vacaverde.carne',
  instagramHref: 'https://www.instagram.com/vacaverde.carne/',
} as const

/** Entry points of the Commerce web app: one for clients, one for staff and administrators. */
export const access = {
  order: `${__APP_URL__}/order`,
  login: `${__APP_URL__}/login`,
} as const
