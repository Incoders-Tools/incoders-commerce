import { useEffect, useState, type FormEvent } from 'react'
import { Button } from '@/components/ui/button'
import { Card, CardContent, CardHeader, CardTitle } from '@/components/ui/card'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { OrderLinesEditor } from '@/components/OrderLinesEditor'
import { listPublicPresentations, requestGuestVerification, confirmGuestVerification, submitGuestOrder } from '@/api/publicOrdering'
import { customerSignIn, submitCustomerOrder } from '@/api/customerSession'
import { ApiError } from '@/api/client'
import { OrderSubmissionOutcomeStatus, type CustomerSignedInResponse, type OrderSubmissionOutcome, type PresentationRecord, type SubmitOrderLine } from '@/api/types'

type Branch = 'guest' | 'registered'

/**
 * commerce-guest-ordering design.md "One screen, guest and registered as
 * peers" (ADR-009, locked): ONE route, a two-option segmented control
 * (`Order as guest` listed FIRST / `Sign in to order`), no "recommended"
 * copy, no benefits pitch, no login-pressure interstitial, and no
 * self-registration form or link anywhere — registered logins remain
 * admin-provisioned only (public-order-surface spec.md "No Self-
 * Registration Route"). Both branches share `OrderLinesEditor`, fed by the
 * public catalog read, so there is no raw GUID input field on the guest
 * path (the old staff-operated form now lives separately as
 * `StaffOrderScreen`, tasks.md 6.8's regression guard).
 */
export function OrderScreen() {
  const [branch, setBranch] = useState<Branch>('guest')
  const [presentations, setPresentations] = useState<PresentationRecord[]>([])
  const [catalogError, setCatalogError] = useState<string | null>(null)

  useEffect(() => {
    let cancelled = false
    listPublicPresentations()
      .then((items) => {
        if (!cancelled) {
          setPresentations(items)
        }
      })
      .catch((err) => {
        if (!cancelled) {
          setCatalogError(err instanceof ApiError ? err.message : 'Unexpected error loading the catalog.')
        }
      })
    return () => {
      cancelled = true
    }
  }, [])

  return (
    <Card className="mx-auto mt-8 w-full max-w-lg">
      <CardHeader>
        <CardTitle>Place an order</CardTitle>
      </CardHeader>
      <CardContent>
        <div role="tablist" aria-label="Order as" className="mb-4 flex gap-2">
          <Button
            type="button"
            role="tab"
            aria-selected={branch === 'guest'}
            variant={branch === 'guest' ? 'default' : 'outline'}
            onClick={() => setBranch('guest')}
          >
            Order as guest
          </Button>
          <Button
            type="button"
            role="tab"
            aria-selected={branch === 'registered'}
            variant={branch === 'registered' ? 'default' : 'outline'}
            onClick={() => setBranch('registered')}
          >
            Sign in to order
          </Button>
        </div>

        {catalogError && (
          <p role="alert" className="mb-4 text-sm text-red-600">
            {catalogError}
          </p>
        )}

        <div role="tabpanel">
          {branch === 'guest' ? (
            <GuestOrderPanel presentations={presentations} />
          ) : (
            <RegisteredOrderPanel presentations={presentations} />
          )}
        </div>
      </CardContent>
    </Card>
  )
}

function GuestOrderPanel({ presentations }: { presentations: PresentationRecord[] }) {
  const [documentId, setDocumentId] = useState('')
  const [email, setEmail] = useState('')
  const [displayName, setDisplayName] = useState('')
  const [deliveryNotes, setDeliveryNotes] = useState('')
  const [verificationId, setVerificationId] = useState<string | null>(null)
  const [code, setCode] = useState('')
  const [verified, setVerified] = useState(false)
  const [lines, setLines] = useState<SubmitOrderLine[]>([])
  const [outcome, setOutcome] = useState<OrderSubmissionOutcome | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [requesting, setRequesting] = useState(false)
  const [confirming, setConfirming] = useState(false)
  const [submitting, setSubmitting] = useState(false)

  const handleRequestCode = async (event: FormEvent) => {
    event.preventDefault()
    setError(null)
    setRequesting(true)
    try {
      const result = await requestGuestVerification({ documentId, email })
      setVerificationId(result.verificationId)
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Unexpected error requesting the verification code.')
    } finally {
      setRequesting(false)
    }
  }

  const handleConfirmCode = async (event: FormEvent) => {
    event.preventDefault()
    if (!verificationId) {
      return
    }
    setError(null)
    setConfirming(true)
    try {
      await confirmGuestVerification({ verificationId, code })
      setVerified(true)
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Unexpected error confirming the verification code.')
    } finally {
      setConfirming(false)
    }
  }

  const handleSubmitOrder = async (event: FormEvent) => {
    event.preventDefault()
    // Defense in depth (public-order-surface spec.md "Guest Verification
    // Gate Before Admission") — the server is the real gate; this only
    // stops an obviously-doomed request from ever leaving the browser.
    if (!verified || !verificationId) {
      setError('Confirm your verification code before submitting the order.')
      return
    }
    setError(null)
    setOutcome(null)
    setSubmitting(true)
    try {
      const result = await submitGuestOrder({
        orderId: crypto.randomUUID(),
        verificationId,
        documentId,
        email,
        displayName: displayName || email,
        deliveryNotes: deliveryNotes || null,
        lines,
        correlationId: crypto.randomUUID(),
      })
      setOutcome(result)
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Unexpected error submitting the order.')
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <div className="flex flex-col gap-6">
      <form className="flex flex-col gap-4" onSubmit={handleRequestCode}>
        <Field id="guest-document" label="Document (DNI)" value={documentId} onChange={setDocumentId} disabled={verified} />
        <Field id="guest-email" label="Email" value={email} onChange={setEmail} type="email" disabled={verified} />
        <Field id="guest-display-name" label="Name" value={displayName} onChange={setDisplayName} disabled={verified} required={false} />
        <Field
          id="guest-delivery-notes"
          label="Delivery notes (optional)"
          value={deliveryNotes}
          onChange={setDeliveryNotes}
          disabled={verified}
          required={false}
        />
        {!verified && (
          <Button type="submit" disabled={requesting || !documentId || !email}>
            {requesting ? 'Sending…' : verificationId ? 'Resend verification code' : 'Send verification code'}
          </Button>
        )}
      </form>

      {verificationId && !verified && (
        <form className="flex flex-col gap-4" onSubmit={handleConfirmCode}>
          <Field id="guest-code" label="Verification code" value={code} onChange={setCode} />
          <Button type="submit" disabled={confirming || !code}>
            {confirming ? 'Confirming…' : 'Confirm code'}
          </Button>
        </form>
      )}

      {verified && <p className="text-sm text-green-700">Verification confirmed.</p>}

      <form className="flex flex-col gap-4" onSubmit={handleSubmitOrder}>
        <OrderLinesEditor presentations={presentations} lines={lines} onChange={setLines} />

        {!verified && (
          <p className="text-sm text-neutral-600">Confirm your verification code before submitting the order.</p>
        )}

        {error && (
          <p role="alert" className="text-sm text-red-600">
            {error}
          </p>
        )}
        {outcome && (
          <p data-testid="order-outcome" className="text-sm text-neutral-700">
            {outcome.status === OrderSubmissionOutcomeStatus.Accepted ? 'Order accepted.' : `Denied: ${outcome.reason}`}
          </p>
        )}

        <Button type="submit" disabled={submitting || !verified || lines.length === 0}>
          {submitting ? 'Submitting…' : 'Submit order'}
        </Button>
      </form>
    </div>
  )
}

function RegisteredOrderPanel({ presentations }: { presentations: PresentationRecord[] }) {
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [session, setSession] = useState<CustomerSignedInResponse | null>(null)
  const [lines, setLines] = useState<SubmitOrderLine[]>([])
  const [outcome, setOutcome] = useState<OrderSubmissionOutcome | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [signingIn, setSigningIn] = useState(false)
  const [submitting, setSubmitting] = useState(false)

  const handleSignIn = async (event: FormEvent) => {
    event.preventDefault()
    setError(null)
    setSigningIn(true)
    try {
      const signedIn = await customerSignIn({ email, password })
      setSession(signedIn)
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Unexpected error signing in.')
    } finally {
      setSigningIn(false)
    }
  }

  const handleSubmitOrder = async (event: FormEvent) => {
    event.preventDefault()
    if (!session) {
      return
    }
    setError(null)
    setOutcome(null)
    setSubmitting(true)
    try {
      const result = await submitCustomerOrder({
        orderId: crypto.randomUUID(),
        lines,
        correlationId: crypto.randomUUID(),
      })
      setOutcome(result)
    } catch (err) {
      setError(err instanceof ApiError ? err.message : 'Unexpected error submitting the order.')
    } finally {
      setSubmitting(false)
    }
  }

  if (!session) {
    return (
      <form className="flex flex-col gap-4" onSubmit={handleSignIn}>
        <Field id="customer-email" label="Email" value={email} onChange={setEmail} type="email" />
        <Field id="customer-password" label="Password" value={password} onChange={setPassword} type="password" />
        {error && (
          <p role="alert" className="text-sm text-red-600">
            {error}
          </p>
        )}
        <Button type="submit" disabled={signingIn || !email || !password}>
          {signingIn ? 'Signing in…' : 'Sign in'}
        </Button>
      </form>
    )
  }

  return (
    <form className="flex flex-col gap-4" onSubmit={handleSubmitOrder}>
      {/* Pre-filled from the signed-in session — never re-typed. */}
      <p className="text-sm text-neutral-700">Signed in as {session.email}</p>

      <OrderLinesEditor presentations={presentations} lines={lines} onChange={setLines} />

      {error && (
        <p role="alert" className="text-sm text-red-600">
          {error}
        </p>
      )}
      {outcome && (
        <p data-testid="order-outcome" className="text-sm text-neutral-700">
          {outcome.status === OrderSubmissionOutcomeStatus.Accepted ? 'Order accepted.' : `Denied: ${outcome.reason}`}
        </p>
      )}

      <Button type="submit" disabled={submitting || lines.length === 0}>
        {submitting ? 'Submitting…' : 'Submit order'}
      </Button>
    </form>
  )
}

function Field({
  id,
  label,
  value,
  onChange,
  type = 'text',
  disabled = false,
  required = true,
}: {
  id: string
  label: string
  value: string
  onChange: (value: string) => void
  type?: string
  disabled?: boolean
  required?: boolean
}) {
  return (
    <div className="flex flex-col gap-1.5">
      <Label htmlFor={id}>{label}</Label>
      <Input
        id={id}
        type={type}
        value={value}
        onChange={(e) => onChange(e.target.value)}
        disabled={disabled}
        required={required}
      />
    </div>
  )
}
