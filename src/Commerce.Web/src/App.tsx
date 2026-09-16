import { useState } from 'react'
import { Button } from '@/components/ui/button'
import { AuthProvider, useAuth } from '@/auth/AuthContext'
import { CatalogScreen } from '@/screens/CatalogScreen'
import { OrderScreen } from '@/screens/OrderScreen'
import { SignInScreen } from '@/screens/SignInScreen'

type Tab = 'catalog' | 'order'

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
      </nav>
      {tab === 'catalog' ? <CatalogScreen /> : <OrderScreen />}
    </div>
  )
}

function Root() {
  const { user } = useAuth()
  return user ? <AuthenticatedApp /> : <SignInScreen />
}

function App() {
  return (
    <AuthProvider>
      <Root />
    </AuthProvider>
  )
}

export default App
