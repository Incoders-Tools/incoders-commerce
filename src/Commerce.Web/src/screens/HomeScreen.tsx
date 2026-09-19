import { Link } from 'react-router'
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
  const auth = useOptionalAuth()

  return (
    <main className="mx-auto flex max-w-3xl flex-col items-center gap-4 p-10 text-center">
      <h1 className="text-2xl font-semibold">Commerce</h1>
      <p className="text-sm text-neutral-600">Run your storefront from one place.</p>
      {auth?.user ? (
        <Link to="/app" className={buttonVariants()}>
          Go to app
        </Link>
      ) : (
        <Link to="/login" className={buttonVariants({ variant: 'outline' })}>
          Sign in
        </Link>
      )}
    </main>
  )
}
