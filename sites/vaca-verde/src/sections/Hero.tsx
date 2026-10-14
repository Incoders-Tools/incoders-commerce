import storefront from '../assets/storefront-engraving.webp'
import { buttonStyles } from '../components/buttonStyles'
import { PinIcon } from '../components/ui'
import { access, business } from '../content'

export function Hero() {
  return (
    <section id="inicio" aria-labelledby="hero-title" className="overflow-hidden">
      <div className="mx-auto grid max-w-7xl items-center gap-12 px-5 pt-14 pb-20 sm:px-8 lg:grid-cols-[1.05fr_1fr] lg:pt-20 lg:pb-28">
        <div>
          <h1
            id="hero-title"
            className="font-display text-[clamp(3.1rem,7.4vw,6.4rem)] leading-[0.95] font-light tracking-[-0.025em] text-field"
          >
            cortes honestos, naturales y frescos
          </h1>
          <p className="mt-8 max-w-[34rem] text-lg leading-relaxed text-ink-soft">
            Somos una carnicería sobre la Ruta 51. Para nosotros, la calidad de un buen corte no empieza en la
            parrilla: empieza en la pastura libre y en el bienestar del animal.
          </p>
          <div className="mt-10 flex flex-wrap gap-3">
            <a href={access.order} className={buttonStyles.primary}>
              Hacer un pedido
            </a>
            <a href="#contacto" className={buttonStyles.outline}>
              Cómo llegar
            </a>
          </div>
          <p className="mt-10 flex items-center gap-2 text-sm text-gold-ink">
            <PinIcon className="size-4 shrink-0" />
            {business.address}
          </p>
        </div>
        <img
          src={storefront}
          alt="Ilustración del local de Vaca Verde, con el cartel redondo de la marca sobre la entrada"
          width={724}
          height={513}
          fetchPriority="high"
          className="ink-in w-full max-w-[44rem] justify-self-center lg:justify-self-end"
        />
      </div>
    </section>
  )
}
