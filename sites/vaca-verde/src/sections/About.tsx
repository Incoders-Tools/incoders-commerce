import cow from '../assets/cow-engraving.webp'
import { LogoMark } from '../components/LogoMark'
import { business } from '../content'

export function About() {
  return (
    <section id="nosotros" aria-labelledby="about-title" className="bg-paper-deep">
      <div className="mx-auto grid max-w-7xl items-center gap-14 px-5 py-24 sm:px-8 md:grid-cols-2 lg:py-32">
        <div className="relative mx-auto w-full max-w-md">
          <img
            src={cow}
            alt="Grabado de una cabeza de vaca"
            width={512}
            height={366}
            loading="lazy"
            className="w-full mix-blend-multiply"
          />
          {/* The round shop sign of the flyer, used as the founding seal. */}
          <div className="absolute -right-2 -bottom-6 flex size-28 flex-col items-center justify-center rounded-full bg-gold text-paper shadow-[0_0_0_6px_var(--color-paper-deep)] sm:size-32">
            <LogoMark className="h-9 w-auto" />
            <span className="mt-1.5 font-display text-sm leading-none">desde {business.foundedYear}</span>
          </div>
        </div>
        <div className="max-w-xl">
          <h2
            id="about-title"
            className="font-display text-[clamp(2.5rem,4.6vw,3.9rem)] leading-none font-light tracking-[-0.02em] text-field"
          >
            por qué verde
          </h2>
          <p className="mt-8 text-lg leading-relaxed">
            El verde representa el campo, la pastura libre y el respeto por el ciclo natural de la tierra.
          </p>
          <p className="mt-5 leading-relaxed text-ink-soft">
            Por eso cuidamos de dónde viene cada corte: la calidad de la carne se define en la alimentación y el
            bienestar del animal en el pastizal, mucho antes de llegar al mostrador.
          </p>
        </div>
      </div>
    </section>
  )
}
