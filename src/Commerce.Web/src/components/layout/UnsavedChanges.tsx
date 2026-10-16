import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useState,
  type MouseEvent,
  type ReactNode,
} from 'react'
import { useLocation, useNavigate } from 'react-router'
import { useTranslation } from 'react-i18next'
import { ConfirmDialog } from './ConfirmDialog'

interface UnsavedChangesContextValue {
  register: (id: symbol, message: string) => void
  unregister: (id: symbol) => void
  /** The message of the first screen with pending work, or null when nothing is pending. */
  message: string | null
  /** Runs `proceed` at once when nothing is pending; otherwise asks first and runs it on "Descartar". */
  confirmLeave: (proceed: () => void) => void
}

const UnsavedChangesContext = createContext<UnsavedChangesContextValue | null>(null)

/**
 * App-wide "unsaved changes" guard. The app uses `<BrowserRouter>`, where `useBlocker` is not
 * available, so in-app navigation asks here instead: a screen with pending work registers a message
 * (`useUnsavedChanges`), and the shell's links ask before leaving (`useGuardedLinkClick`).
 * "Descartar" drops every registration and proceeds; "Seguir editando" stays.
 */
export function UnsavedChangesProvider({ children }: { children: ReactNode }) {
  const { t } = useTranslation('common')
  const [registry, setRegistry] = useState<ReadonlyMap<symbol, string>>(() => new Map())
  // The navigation waiting for "Descartar".
  const [pending, setPending] = useState<(() => void) | null>(null)

  const register = useCallback((id: symbol, message: string) => {
    setRegistry((current) => new Map(current).set(id, message))
  }, [])

  const unregister = useCallback((id: symbol) => {
    setRegistry((current) => {
      if (!current.has(id)) return current
      const next = new Map(current)
      next.delete(id)
      return next
    })
  }, [])

  const message = registry.size === 0 ? null : registry.values().next().value!

  const confirmLeave = useCallback(
    (proceed: () => void) => {
      if (message === null) proceed()
      else setPending(() => proceed)
    },
    [message],
  )

  const value = useMemo(
    () => ({ register, unregister, message, confirmLeave }),
    [register, unregister, message, confirmLeave],
  )

  return (
    <UnsavedChangesContext.Provider value={value}>
      {children}
      {pending !== null && (
        <ConfirmDialog
          title={t('unsavedChanges.title')}
          message={message}
          confirmLabel={t('unsavedChanges.discard')}
          busyLabel={t('unsavedChanges.discard')}
          cancelLabel={t('unsavedChanges.keepEditing')}
          destructive
          onConfirm={() => {
            setRegistry(new Map())
            setPending(null)
            pending()
          }}
          onCancel={() => setPending(null)}
        />
      )}
    </UnsavedChangesContext.Provider>
  )
}

/**
 * Registers `message` as pending work while it is not null; the registration goes away when it becomes
 * null or the caller unmounts. A no-op outside `UnsavedChangesProvider`.
 */
export function useUnsavedChanges(message: string | null) {
  const context = useContext(UnsavedChangesContext)
  const register = context?.register
  const unregister = context?.unregister
  const [id] = useState(() => Symbol('unsaved-changes'))

  useEffect(() => {
    if (!register || !unregister || message === null) return
    register(id, message)
    return () => unregister(id)
  }, [register, unregister, id, message])
}

/** The pending message and the ask-before-leaving action; without a provider nothing is ever pending. */
export function useUnsavedChangesGuard(): Pick<UnsavedChangesContextValue, 'message' | 'confirmLeave'> {
  const context = useContext(UnsavedChangesContext)
  return context ?? { message: null, confirmLeave: (proceed) => proceed() }
}

/**
 * An `onClick` for an in-app link to `to`: with pending work it stops the navigation, asks, and goes
 * to `to` on "Descartar". A click that opens another browser tab, or that stays on the current page,
 * leaves the pending work alone, so it never asks.
 */
export function useGuardedLinkClick() {
  const { message, confirmLeave } = useUnsavedChangesGuard()
  const navigate = useNavigate()
  const { pathname } = useLocation()

  return (to: string) => (event: MouseEvent<HTMLAnchorElement>) => {
    if (message === null || to === pathname) return
    if (event.defaultPrevented || event.button !== 0 || event.metaKey || event.altKey || event.ctrlKey || event.shiftKey)
      return
    event.preventDefault()
    confirmLeave(() => navigate(to))
  }
}
