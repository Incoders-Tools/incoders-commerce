import { Logo } from '../components/LogoMark'
import { access, business } from '../content'

/** Read once at load: the prerendered copy carries the build year until the page hydrates. */
const currentYear = new Date().getFullYear()

export function SiteFooter() {
  return (
    <footer className="border-t border-gold/25 bg-paper-deep">
      <div className="mx-auto flex max-w-7xl flex-col gap-8 px-5 py-12 text-sm text-ink-soft sm:px-8 md:flex-row md:items-center md:justify-between">
        <div className="space-y-3">
          <span className="text-field">
            <Logo />
          </span>
          <p>Carnicería sobre la Ruta 51, desde {business.foundedYear}.</p>
        </div>
        <ul className="flex flex-wrap gap-x-7 gap-y-2">
          <li>
            <a href={access.order} className="hover:text-field">
              Pedidos online
            </a>
          </li>
          <li>
            <a href={access.login} className="hover:text-field">
              Acceso del equipo
            </a>
          </li>
          <li>
            <a href={business.instagramHref} target="_blank" rel="noopener noreferrer" className="hover:text-field">
              Instagram
            </a>
          </li>
        </ul>
        <p suppressHydrationWarning>
          © {currentYear} {business.name}
        </p>
      </div>
    </footer>
  )
}
