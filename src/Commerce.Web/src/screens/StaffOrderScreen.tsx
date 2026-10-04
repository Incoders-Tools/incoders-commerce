import { useEffect, useId, useRef, useState } from 'react'
import { Trans, useTranslation } from 'react-i18next'
import { CircleCheck } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Label } from '@/components/ui/label'
import { Textarea } from '@/components/ui/textarea'
import { PageHeader } from '@/components/data/PageHeader'
import { CustomerPicker } from '@/components/staffOrder/CustomerPicker'
import { ProductSearch } from '@/components/staffOrder/ProductSearch'
import { StaffOrderLines, type StaffOrderLineView } from '@/components/staffOrder/StaffOrderLines'
import { quoteStaffOrder, submitStaffOrder } from '@/api/staffOrders'
import { ApiError } from '@/api/client'
import type { StaffCustomerOption, StaffOrderQuote, StaffPresentationOption, StaffQuoteRequest } from '@/api/types'
import { useMissingBranch } from '@/branch/useMissingBranch'
import { formatMoney } from '@/dashboard/format'
import { parseOrderNumber } from '@/lib/orderNumber'
import { useDebouncedValue } from '@/lib/useDebouncedValue'
import { useNumberFormat } from '@/organization/NumberFormatContext'

const QUOTE_DEBOUNCE_MS = 300
const NOTE_MAX_LENGTH = 500

interface DraftLine {
  option: StaffPresentationOption
  quantityText: string
}

/** The answer to one exact draft (`key` is the serialized quote request). */
interface QuoteResult {
  key: string
  quote: StaffOrderQuote | null
  error: string | null
}

interface Confirmation {
  orderNumber: string | null
  replay: boolean
  customerName: string
  total: number | null
}

/**
 * staff-order-taking T3: "Take order". A seller on the road (phone first) or an administrator taking a phone
 * order picks a customer, adds products and sees the customer's prices before confirming. Every change of the
 * draft asks the server for a fresh quote (debounced; a stale answer never replaces the latest one), and submit
 * stays blocked until the current draft is fully priced. The order id is generated once per draft and kept
 * across retries, so a double tap or a retry after a lost answer never creates a second order. The draft lives
 * in memory only, and no id is ever shown or typed.
 */
export function StaffOrderScreen() {
  const { t } = useTranslation('orders')
  const numberFormat = useNumberFormat()
  const missingBranch = useMissingBranch()
  const noteId = useId()
  const [draftNumber, setDraftNumber] = useState(0)
  const [customer, setCustomer] = useState<StaffCustomerOption | null>(null)
  const [lines, setLines] = useState<DraftLine[]>([])
  const [note, setNote] = useState('')
  const [quoteResult, setQuoteResult] = useState<QuoteResult | null>(null)
  const [submitting, setSubmitting] = useState(false)
  const [submitError, setSubmitError] = useState<string | null>(null)
  const [confirmation, setConfirmation] = useState<Confirmation | null>(null)
  const orderIdRef = useRef<string | null>(null)
  const submittingRef = useRef(false)
  const latestQuote = useRef(0)

  const readLines = lines.map((line) => ({ line, ...readQuantity(line) }))
  const quantitiesValid = readLines.every((read) => read.quantity !== null)
  const quoteRequest: StaffQuoteRequest | null =
    customer && lines.length > 0 && quantitiesValid
      ? {
          customerId: customer.id,
          lines: readLines.map(({ line, quantity }) => ({
            productId: line.option.productId,
            presentationId: line.option.presentationId,
            quantity: quantity!,
          })),
        }
      : null
  const quoteKey = quoteRequest ? JSON.stringify(quoteRequest) : null
  const debouncedQuoteKey = useDebouncedValue(quoteKey, QUOTE_DEBOUNCE_MS)

  useEffect(() => {
    const request = ++latestQuote.current
    if (debouncedQuoteKey === null || missingBranch) return
    quoteStaffOrder(JSON.parse(debouncedQuoteKey) as StaffQuoteRequest)
      .then((quote) => {
        if (request !== latestQuote.current) return
        const usable = quote.status === 'quoted' || quote.reason === 'no-effective-price'
        setQuoteResult({ key: debouncedQuoteKey, quote, error: usable ? null : denialMessage(quote.reason) })
      })
      .catch((err: unknown) => {
        if (request === latestQuote.current) {
          setQuoteResult({ key: debouncedQuoteKey, quote: null, error: errorMessage(err, t('staffOrder.errors.unexpectedQuote')) })
        }
      })
    // denialMessage/errorMessage only read `t`.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [debouncedQuoteKey, missingBranch, t])

  if (missingBranch) {
    return (
      <section className="flex w-full flex-col gap-6">
        <PageHeader title={t('staffOrder.title')} description={t('staffOrder.description')} />
        <p role="status" className="rounded-lg border border-border bg-card px-4 py-10 text-center text-sm text-muted-foreground">
          {t('staffOrder.branchRequired')}
        </p>
      </section>
    )
  }

  function readQuantity(line: DraftLine): { quantity: number | null; error: string | null } {
    const whole = line.option.quantityBehavior === 'FixedQuantity'
    const reading = numberFormat.read(line.quantityText)
    if (reading.ok && reading.value > 0 && (!whole || Number.isInteger(reading.value))) {
      return { quantity: reading.value, error: null }
    }
    // A blank box blocks the order but is not an error yet: the operator is typing.
    if (!reading.ok && reading.reason === 'blank') return { quantity: null, error: null }
    return {
      quantity: null,
      error:
        numberFormat.errorFor(line.quantityText) ??
        t(whole ? 'staffOrder.lines.invalidInteger' : 'staffOrder.lines.invalidDecimal'),
    }
  }

  function denialMessage(reason: string): string {
    if (reason === 'not-found') return t('staffOrder.errors.notFound')
    if (reason === 'customer-disabled') return t('staffOrder.errors.customerDisabled')
    if (reason === 'no-effective-price') return t('staffOrder.errors.noEffectivePrice')
    return t('staffOrder.errors.denied', { reason })
  }

  function errorMessage(err: unknown, fallback: string): string {
    if (!(err instanceof ApiError)) return fallback
    if (err.status === 400) {
      return err.code === 'branch-selection-required' ? t('staffOrder.branchRequired') : t('staffOrder.errors.validation')
    }
    if (err.status === 403) return t('staffOrder.errors.forbidden')
    return err.message
  }

  const currentQuote = quoteResult !== null && quoteResult.key === quoteKey ? quoteResult : null
  const quotedLines = currentQuote?.quote?.lines ?? []
  // A denied quote without an error message is `no-effective-price`: some line has no price.
  const hasUnpricedLine =
    quotedLines.some((line) => line.status === 'no-effective-price') || currentQuote?.quote?.status === 'denied'
  const total = currentQuote?.quote?.status === 'quoted' ? currentQuote.quote.total : null

  const blockingMessage = !customer
    ? t('staffOrder.summary.chooseCustomer')
    : lines.length === 0
      ? t('staffOrder.summary.addProducts')
      : !quantitiesValid
        ? t('staffOrder.summary.fixQuantities')
        : !currentQuote
          ? t('staffOrder.summary.pricing')
          : currentQuote.error
            ? currentQuote.error
            : hasUnpricedLine
              ? t('staffOrder.summary.unpricedBlock')
              : null
  const canSubmit = blockingMessage === null && total !== null && !submitting

  const lineViews: StaffOrderLineView[] = readLines.map(({ line, error }) => ({
    option: line.option,
    quantityText: line.quantityText,
    error,
    quote: quotedLines.find((quoted) => quoted.presentationId === line.option.presentationId),
  }))

  const changeDraft = (change: () => void) => {
    change()
    setSubmitError(null)
  }

  const addLine = (option: StaffPresentationOption) =>
    changeDraft(() =>
      setLines((previous) => {
        const index = previous.findIndex((line) => line.option.presentationId === option.presentationId)
        if (index < 0) return [...previous, { option, quantityText: '1' }]
        const current = numberFormat.parse(previous[index].quantityText)
        const merged = (current !== null && current > 0 ? current : 0) + 1
        return previous.map((line, i) => (i === index ? { ...line, quantityText: numberFormat.formatNumber(merged) } : line))
      }),
    )

  const changeQuantity = (presentationId: string, quantityText: string) =>
    changeDraft(() =>
      setLines((previous) =>
        previous.map((line) => (line.option.presentationId === presentationId ? { ...line, quantityText } : line)),
      ),
    )

  const removeLine = (presentationId: string) =>
    changeDraft(() => setLines((previous) => previous.filter((line) => line.option.presentationId !== presentationId)))

  const submit = async () => {
    if (!canSubmit || !customer || !quoteRequest || submittingRef.current) return
    submittingRef.current = true
    orderIdRef.current ??= crypto.randomUUID()
    setSubmitting(true)
    setSubmitError(null)
    try {
      const trimmedNote = note.trim()
      const result = await submitStaffOrder({ ...quoteRequest, orderId: orderIdRef.current, note: trimmedNote === '' ? null : trimmedNote })
      if (result.status === 'accepted') {
        setConfirmation({
          orderNumber: result.orderNumber,
          replay: !result.wasNewlyAccepted,
          customerName: customer.displayName,
          total,
        })
      } else {
        setSubmitError(denialMessage(result.reason))
      }
    } catch (err) {
      setSubmitError(errorMessage(err, t('staffOrder.errors.unexpectedSubmit')))
    } finally {
      submittingRef.current = false
      setSubmitting(false)
    }
  }

  const startNewOrder = () => {
    orderIdRef.current = null
    setCustomer(null)
    setLines([])
    setNote('')
    setQuoteResult(null)
    setSubmitError(null)
    setConfirmation(null)
    setDraftNumber((n) => n + 1)
  }

  if (confirmation) {
    return (
      <section className="flex w-full flex-col gap-6">
        <PageHeader title={t('staffOrder.title')} />
        <OrderConfirmation confirmation={confirmation} onNewOrder={startNewOrder} />
      </section>
    )
  }

  return (
    <section className="flex w-full flex-col gap-6 pb-44 lg:pb-0">
      <PageHeader title={t('staffOrder.title')} description={t('staffOrder.description')} />

      <div className="grid grid-cols-1 gap-6 lg:grid-cols-[minmax(0,1fr)_20rem] lg:items-start xl:grid-cols-[minmax(0,1fr)_24rem]">
        <div className="flex min-w-0 flex-col gap-8">
          <CustomerPicker key={`customer-${draftNumber}`} selected={customer} onSelect={(next) => changeDraft(() => setCustomer(next))} />

          <section aria-labelledby={`${noteId}-products`} className="flex flex-col gap-3">
            <h2 id={`${noteId}-products`} className="text-base font-semibold text-foreground">
              {t('staffOrder.products.title')}
            </h2>
            <ProductSearch key={`products-${draftNumber}`} onAdd={addLine} />
            <StaffOrderLines lines={lineViews} awaitingQuote={customer !== null} onQuantityChange={changeQuantity} onRemove={removeLine} />
          </section>

          <div className="flex flex-col gap-1.5">
            <Label htmlFor={noteId}>{t('staffOrder.note.label')}</Label>
            <Textarea
              id={noteId}
              maxLength={NOTE_MAX_LENGTH}
              className="text-base lg:text-sm"
              placeholder={t('staffOrder.note.placeholder')}
              value={note}
              onChange={(event) => changeDraft(() => setNote(event.target.value))}
            />
            <p className="self-end text-xs tabular-nums text-muted-foreground">
              {t('staffOrder.note.counter', { length: note.length, max: NOTE_MAX_LENGTH })}
            </p>
          </div>
        </div>

        {/* Phones and tablets: a bar fixed to the bottom (beside the md sidebar) so the total and the button are
            always at hand; desktop: a sticky summary column. One element, so there is one submit button. */}
        <aside
          aria-label={t('staffOrder.summary.title')}
          className="fixed inset-x-0 bottom-0 z-20 flex flex-col gap-3 border-t border-border bg-card p-4 shadow-[0_-4px_12px_rgba(0,0,0,0.08)] md:left-64 lg:sticky lg:top-6 lg:z-auto lg:rounded-lg lg:border lg:p-5 lg:shadow-none"
        >
          <div className="hidden flex-col gap-1 lg:flex">
            <h2 className="text-base font-semibold text-foreground">{t('staffOrder.summary.title')}</h2>
            {customer && <p className="truncate text-sm text-foreground">{customer.displayName}</p>}
            {lines.length > 0 && (
              <p className="text-sm text-muted-foreground">{t('staffOrder.summary.lineCount', { count: lines.length })}</p>
            )}
          </div>

          {submitError && (
            <p role="alert" className="rounded-md border border-destructive/40 bg-destructive/10 px-3 py-2 text-sm text-destructive">
              {submitError}
            </p>
          )}
          {blockingMessage && <p className="text-sm text-muted-foreground">{blockingMessage}</p>}

          <div className="flex items-center gap-4 lg:flex-col lg:items-stretch">
            <div className="min-w-0 lg:flex lg:items-baseline lg:justify-between">
              <p className="text-xs uppercase tracking-wide text-muted-foreground">{t('staffOrder.summary.total')}</p>
              <p data-testid="order-total" className="text-xl font-semibold tabular-nums text-foreground">
                {total === null ? '—' : formatMoney(total)}
              </p>
            </div>
            <Button className="h-12 flex-1 text-base" disabled={!canSubmit} onClick={() => void submit()}>
              {submitting ? t('staffOrder.summary.submitting') : t('staffOrder.summary.submit')}
            </Button>
          </div>
        </aside>
      </div>
    </section>
  )
}

function OrderConfirmation({ confirmation, onNewOrder }: { confirmation: Confirmation; onNewOrder: () => void }) {
  const { t } = useTranslation('orders')
  const parts = parseOrderNumber(confirmation.orderNumber)

  return (
    <div className="flex flex-col items-start gap-4 rounded-lg border border-emerald-500/40 bg-emerald-500/10 p-6">
      <CircleCheck aria-hidden="true" className="size-8 text-emerald-600 dark:text-emerald-400" />
      <h2 className="text-xl font-semibold text-foreground">
        {confirmation.orderNumber ? (
          <Trans
            t={t}
            i18nKey="staffOrder.confirmation.title"
            values={{ number: confirmation.orderNumber }}
            components={{
              number: <span className="font-mono" title={parts ? t('placeOrder.outcome.numberHint', { ...parts }) : undefined} />,
            }}
          />
        ) : (
          t('staffOrder.confirmation.titleWithoutNumber')
        )}
      </h2>
      {confirmation.replay && <p className="text-sm text-muted-foreground">{t('staffOrder.confirmation.replay')}</p>}
      <div className="flex flex-col gap-1 text-sm text-foreground">
        <p>{t('staffOrder.confirmation.customer', { name: confirmation.customerName })}</p>
        {confirmation.total !== null && <p>{t('staffOrder.confirmation.total', { total: formatMoney(confirmation.total) })}</p>}
      </div>
      <Button className="h-12 w-full text-base sm:w-auto" onClick={onNewOrder}>
        {t('staffOrder.confirmation.newOrder')}
      </Button>
    </div>
  )
}
