import pasture from '../assets/pasture-engraving.webp'
import { buttonStyles } from '../components/buttonStyles'
import { InstagramIcon, PhoneIcon, PinIcon } from '../components/ui'
import { business } from '../content'

export function Visit() {
  return (
    <section id="contacto" aria-labelledby="visit-title">
      <div className="mx-auto grid max-w-7xl items-center gap-14 px-5 py-24 sm:px-8 md:grid-cols-2 lg:py-32">
        <div>
          <h2
            id="visit-title"
            className="font-display text-[clamp(2.5rem,4.6vw,3.9rem)] leading-none font-light tracking-[-0.02em] text-field"
          >
            dónde encontrarnos
          </h2>
          <ul className="mt-10 space-y-6 text-lg">
            <li className="flex items-start gap-4">
              <PinIcon className="mt-1 size-5 shrink-0 text-gold-ink" />
              <span>{business.address}</span>
            </li>
            <li className="flex items-start gap-4">
              <PhoneIcon className="mt-1 size-5 shrink-0 text-gold-ink" />
              <a
                href={business.whatsappHref}
                target="_blank"
                rel="noopener noreferrer"
                className="underline decoration-gold/50 underline-offset-4 hover:text-field"
              >
                {business.phoneDisplay}
              </a>
            </li>
            <li className="flex items-start gap-4">
              <InstagramIcon className="mt-1 size-5 shrink-0 text-gold-ink" />
              <a
                href={business.instagramHref}
                target="_blank"
                rel="noopener noreferrer"
                className="underline decoration-gold/50 underline-offset-4 hover:text-field"
              >
                {business.instagramHandle}
              </a>
            </li>
          </ul>
          <div className="mt-10 flex flex-wrap gap-3">
            <a href={business.mapsHref} target="_blank" rel="noopener noreferrer" className={buttonStyles.primary}>
              Abrir en Google Maps
            </a>
            <a href={business.whatsappHref} target="_blank" rel="noopener noreferrer" className={buttonStyles.outline}>
              Escribir por WhatsApp
            </a>
          </div>
        </div>
        <img
          src={pasture}
          alt=""
          width={604}
          height={432}
          loading="lazy"
          className="mx-auto w-full max-w-lg mix-blend-multiply"
        />
      </div>
    </section>
  )
}
