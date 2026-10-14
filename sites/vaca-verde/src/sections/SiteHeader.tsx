import { Logo } from '../components/LogoMark'
import { buttonStyles } from '../components/buttonStyles'
import { access } from '../content'

const navLinks = [
  { href: '#nosotros', label: 'Nosotros' },
  { href: '#cortes', label: 'Cortes' },
  { href: '#contacto', label: 'Contacto' },
]

export function SiteHeader() {
  return (
    <header className="sticky top-0 z-20 border-b border-gold/20 bg-paper/90 backdrop-blur-md">
      <div className="mx-auto flex h-18 max-w-7xl items-center justify-between gap-6 px-5 sm:px-8">
        <a href="#inicio" className="text-field" aria-label="Vaca Verde, inicio">
          <Logo />
        </a>
        <nav aria-label="Secciones" className="hidden md:block">
          <ul className="flex items-center gap-8 text-[0.95rem] text-ink-soft">
            {navLinks.map((link) => (
              <li key={link.href}>
                <a href={link.href} className="transition-colors hover:text-field">
                  {link.label}
                </a>
              </li>
            ))}
          </ul>
        </nav>
        <div className="flex items-center gap-3">
          <a href="#ingresar" className={`${buttonStyles.outline} px-5 py-2.5`}>
            Ingresar
          </a>
          <a href={access.order} className={`${buttonStyles.primary} px-5 py-2.5 max-sm:hidden`}>
            Hacer un pedido
          </a>
        </div>
      </div>
    </header>
  )
}
