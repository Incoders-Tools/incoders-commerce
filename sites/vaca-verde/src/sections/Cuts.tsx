import cutEngraving from '../assets/cut-engraving.webp'
import cutsDisplay from '../assets/cuts-display.webp'
import freshCut from '../assets/fresh-cut.webp'
import ribRoast from '../assets/rib-roast.webp'
import rolledCut from '../assets/rolled-cut.webp'
import rolledRoast from '../assets/rolled-roast.webp'

const smallPhotos = [
  { src: freshCut, width: 576, height: 768, alt: 'Corte vacuno fresco sostenido con guantes en el mostrador' },
  { src: ribRoast, width: 576, height: 768, alt: 'Tira de asado con hueso recién cortada' },
  { src: rolledCut, width: 480, height: 640, alt: 'Corte enrollado listo para la parrilla' },
  { src: rolledRoast, width: 480, height: 640, alt: 'Pieza de carne enrollada y atada' },
]

export function Cuts() {
  return (
    <section id="cortes" aria-labelledby="cuts-title">
      <div className="mx-auto max-w-7xl px-5 py-24 sm:px-8 lg:py-32">
        <div className="grid items-end gap-10 md:grid-cols-[1fr_auto]">
          <div className="max-w-2xl">
            <h2
              id="cuts-title"
              className="font-display text-[clamp(2.5rem,4.6vw,3.9rem)] leading-none font-light tracking-[-0.02em] text-field"
            >
              para juntarse y compartir
            </h2>
            <blockquote className="mt-8 text-xl leading-relaxed text-ink">
              <p>
                El asado no es solo comida: es el momento de pausar, juntarse y compartir. Por eso buscamos que cada
                corte sea honesto, natural y fresco.
              </p>
            </blockquote>
          </div>
          <img
            src={cutEngraving}
            alt=""
            width={484}
            height={346}
            loading="lazy"
            className="hidden w-56 mix-blend-multiply md:block lg:w-72"
          />
        </div>

        <div className="mt-14 grid grid-cols-2 gap-3 md:grid-cols-4 md:grid-rows-2">
          <img
            src={cutsDisplay}
            alt="Cortes vacunos frescos exhibidos en la carnicería"
            width={936}
            height={1248}
            loading="lazy"
            // From md up the square photos set the row heights; h-0 + min-h-full keeps this one from stretching them.
            className="col-span-2 aspect-[4/3] w-full object-cover md:row-span-2 md:aspect-auto md:h-0 md:min-h-full"
          />
          {smallPhotos.map((photo) => (
            <img
              key={photo.src}
              src={photo.src}
              alt={photo.alt}
              width={photo.width}
              height={photo.height}
              loading="lazy"
              className="aspect-square h-full w-full object-cover"
            />
          ))}
        </div>
      </div>
    </section>
  )
}
