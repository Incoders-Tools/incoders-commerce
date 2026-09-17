import { useState } from 'react'
import { Button } from '@/components/ui/button'
import { AuthProvider, useAuth } from '@/auth/AuthContext'
import { useResetToken } from '@/auth/useResetToken'
import { CatalogScreen } from '@/screens/CatalogScreen'
import { ForgotPasswordScreen } from '@/screens/ForgotPasswordScreen'
import { OrderScreen } from '@/screens/OrderScreen'
import { RenewPasswordScreen } from '@/screens/RenewPasswordScreen'
import { ResetPasswordScreen } from '@/screens/ResetPasswordScreen'
import { SignInScreen } from '@/screens/SignInScreen'

type Tab = 'catalog' | 'order' | 'renew'

function AuthenticatedApp() {
  const { user, signOut } = useAuth()
  const [tab, setTab] = useState<Tab>('catalog')

  return (
    <div className="mx-auto max-w-3xl p-6">
      <header className="mb-6 flex items-center justify-between">
        <h1 className="text-xl font-semibold">Commerce</h1>
        <div className="flex items-center gap-3">
          <span className="text-sm text-neutral-500">{user!.displayName}</span>
          <Button variant="outline" size="sm" onClick={() => void signOut()}>
            Sign out
          </Button>
        </div>
      </header>
      <nav className="mb-6 flex gap-2">
        <Button variant={tab === 'catalog' ? 'default' : 'outline'} size="sm" onClick={() => setTab('catalog')}>
          Catalog
        </Button>
        <Button variant={tab === 'order' ? 'default' : 'outline'} size="sm" onClick={() => setTab('order')}>
          Orders
        </Button>
        <Button variant={tab === 'renew' ? 'default' : 'outline'} size="sm" onClick={() => setTab('renew')}>
          Change password
        </Button>
      </nav>
      {tab === 'catalog' ? <CatalogScreen /> : tab === 'order' ? <OrderScreen /> : <RenewPasswordScreen />}
    </div>
  )
}

type SignedOutView = 'sign-in' | 'forgot-password'

function SignedOutApp() {
  const [view, setView] = useState<SignedOutView>('sign-in')

  return view === 'sign-in' ? (
    <div className="flex flex-col gap-3">
      <SignInScreen />
      <Button
        type="button"
        variant="outline"
        size="sm"
        className="mx-auto"
        onClick={() => setView('forgot-password')}
      >
        Forgot password?
      </Button>
    </div>
  ) : (
    <ForgotPasswordScreen onBackToSignIn={() => setView('sign-in')} />
  )
}

function Root() {
  const { user } = useAuth()
  const { token, clear } = useResetToken()

  // A reset token in the URL takes priority over sign-in state (design.md
  // "Reset link, no router") — the SPA reads it once at mount via
  // useResetToken() and MapFallbackToFile("index.html") already serves this
  // path, so no server change is needed.
  if (token) {
    return <ResetPasswordScreen token={token} onSuccess={clear} />
  }

  return user ? <AuthenticatedApp /> : <SignedOutApp />
}

function App() {
  return (
    <AuthProvider>
      <Root />
    </AuthProvider>
  )
}

export default App
