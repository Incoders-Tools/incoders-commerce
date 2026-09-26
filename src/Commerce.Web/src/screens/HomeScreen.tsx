import { Link } from 'react-router'
import { useTranslation } from 'react-i18next'
import { buttonVariants } from '@/components/ui/button'
import { useOptionalAuth } from '@/auth/AuthContext'

/**
 * Public front door (web-app-routing spec: "Public Routes Render Without
 * Auth Dependency"). Deliberately a shell — hero structure only, no
 * marketing content/copy/layout system (proposal.md "Out of Scope"). Reads
 * auth state via `useOptionalAuth()`, which never throws, so this screen
 * renders even with no `AuthProvider` mounted at all.
 */
export function HomeScreen() {
  const { t } = useTranslation('common')
  const auth = useOptionalAuth()

  return (
    <main className="mx-auto flex max-w-3xl flex-col items-center gap-4 p-10 text-center">
      <h1 className="text-2xl font-semibold">{t('app.name')}</h1>
      <p className="text-sm text-neutral-600">{t('home.tagline')}</p>
      {auth?.user ? (
        <Link to="/app" className={buttonVariants()}>
          {t('home.goToApp')}
        </Link>
      ) : (
        <Link to="/login" className={buttonVariants({ variant: 'outline' })}>
          {t('home.signIn')}
        </Link>
      )}
    </main>
  )
}
