import { LogoMark } from '../components/LogoMark'
import { buttonStyles } from '../components/buttonStyles'
import { access } from '../content'

/**
 * The page's job: send each visitor to the right door of the Commerce web app.
 * Clients are the main audience, so their path leads; staff get a quieter one.
 */
export function Access() {
  return (
    <section id="ingresar" aria-labelledby="access-title" className="bg-pasture text-meadow">
      <div className="mx-auto max-w-7xl px-5 py-24 sm:px-8 lg:py-28">
        <div className="flex items-center gap-4">
          <LogoMark className="h-10 w-auto text-logo" />
          <h2
            id="access-title"
            className="font-display text-[clamp(2.3rem,4.2vw,3.5rem)] leading-none font-light tracking-[-0.02em] text-paper"
          >
            ingresar a vaca verde
          </h2>
        </div>

        <div className="mt-14 grid gap-5 lg:grid-cols-[1.45fr_1fr]">
          <article aria-labelledby="access-clients" className="flex flex-col bg-pasture-soft p-8 sm:p-12">
            <h3 id="access-clients" className="font-display text-3xl text-paper">
              Clientes
            </h3>
            <p className="mt-4 max-w-md text-lg leading-relaxed">
              Pedidos online con su cuenta de cliente. Si todavía no tiene una, también puede pedir como invitado.
            </p>
            <div className="mt-auto pt-10">
              <a href={access.order} className={buttonStyles.logo}>
                Hacer un pedido
              </a>
            </div>
          </article>

          <article aria-labelledby="access-staff" className="flex flex-col border border-meadow/20 p-8 sm:p-12">
            <h3 id="access-staff" className="font-display text-3xl text-paper">
              Equipo y administración
            </h3>
            <p className="mt-4 max-w-sm leading-relaxed text-meadow/80">
              Acceso al sistema de gestión del local: ventas, pedidos, stock y administración.
            </p>
            <div className="mt-auto pt-10">
              <a href={access.login} className={buttonStyles.outlineOnDark}>
                Ingresar al sistema
              </a>
            </div>
          </article>
        </div>
      </div>
    </section>
  )
}
