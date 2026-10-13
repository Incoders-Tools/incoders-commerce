import { useCallback, useEffect, useRef, useState } from 'react'
import { useTranslation } from 'react-i18next'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { FormPage } from '@/components/layout/FormPage'
import { ProductSearch } from '@/components/staffOrder/ProductSearch'
import { presentationName } from '@/components/staffOrder/presentationName'
import { ApiError } from '@/api/client'
import { listGoods, quoteGoods, registerGoods, type EmployeeRecord, type GoodsItem, type GoodsQuote } from '@/api/employees'
import type { StaffPresentationOption } from '@/api/types'
import { formatIsoDate, formatMoney } from '@/dashboard/format'
import { todayIso } from '@/lib/isoDate'
import { useDebouncedValue } from '@/lib/useDebouncedValue'
import { useNumberFormat } from '@/organization/NumberFormatContext'

const QUOTE_DEBOUNCE_MS = 300

const decimalText = (value: number) => String(value).replace('.', ',')

interface DraftLine {
  option: StaffPresentationOption
  quantityText: string
}

/** The answer to one exact draft (`key` is the serialized quote request): a stale answer never shows. */
interface QuoteResult {
  key: string
  quote: GoodsQuote | null
  error: string | null
}

/**
 * employee-lifecycle E4c: the goods an employee takes, registered like a sale at the counter. Products and quantities;
 * the server prices them on the default list (Mostrador) and the total shows before registering. Registering takes the
 * stock out and leaves the debt on the employee's goods account; the next payroll deducts it. The registration key is
 * generated once per registration and kept across retries and edits, so a retry after a lost answer never registers twice.
 */
export function EmployeeGoodsPage({ employee, onBack }: { employee: EmployeeRecord; onBack: () => void }) {
  const { t } = useTranslation('employees')
  const numberFormat = useNumberFormat()
  const takesGoods = employee.customerId !== null
  const [items, setItems] = useState<GoodsItem[]>([])
  const [loading, setLoading] = useState(true)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [occurredOn, setOccurredOn] = useState(todayIso())
  const [lines, setLines] = useState<DraftLine[]>([])
  const [draftNumber, setDraftNumber] = useState(0)
  const [quoteResult, setQuoteResult] = useState<QuoteResult | null>(null)
  const [saving, setSaving] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [status, setStatus] = useState<string | null>(null)
  const goodsIdRef = useRef<string | null>(null)
  const latestQuote = useRef(0)

  const codeText = useCallback(
    (err: unknown, fallback: string) =>
      err instanceof ApiError && err.code ? t(`goods.codes.${err.code}`, { defaultValue: err.message }) : fallback,
    [t],
  )

  const load = useCallback(async () => {
    setLoading(true)
    setLoadError(null)
    try {
      setItems(await listGoods(employee.id))
    } catch {
      setLoadError(t('goods.loadError'))
    } finally {
      setLoading(false)
    }
  }, [employee.id, t])

  useEffect(() => {
    void load()
  }, [load])

  const readQuantity = (line: DraftLine): { quantity: number | null; error: string | null } => {
    const whole = line.option.quantityBehavior === 'FixedQuantity'
    const reading = numberFormat.read(line.quantityText)
    if (reading.ok && reading.value > 0 && (!whole || Number.isInteger(reading.value))) return { quantity: reading.value, error: null }
    // A blank box blocks the registration but is not an error yet: the operator is typing.
    if (!reading.ok && reading.reason === 'blank') return { quantity: null, error: null }
    return {
      quantity: null,
      error: numberFormat.errorFor(line.quantityText) ?? t(whole ? 'goods.invalidInteger' : 'goods.invalidQuantity'),
    }
  }

  const readLines = lines.map((line) => ({ line, ...readQuantity(line) }))
  const quantitiesValid = readLines.every((read) => read.quantity !== null)
  const dateValid = /^\d{4}-\d{2}-\d{2}$/.test(occurredOn) && occurredOn <= todayIso()
  const quoteKey =
    takesGoods && lines.length > 0 && quantitiesValid && dateValid
      ? JSON.stringify({
          occurredOn,
          lines: readLines.map(({ line, quantity }) => ({ presentationId: line.option.presentationId, quantity: quantity! })),
        })
      : null
  const debouncedQuoteKey = useDebouncedValue(quoteKey, QUOTE_DEBOUNCE_MS)

  useEffect(() => {
    const request = ++latestQuote.current
    if (debouncedQuoteKey === null) return
    quoteGoods(employee.id, JSON.parse(debouncedQuoteKey))
      .then((quote) => {
        if (request === latestQuote.current) setQuoteResult({ key: debouncedQuoteKey, quote, error: null })
      })
      .catch((err: unknown) => {
        if (request === latestQuote.current) {
          setQuoteResult({ key: debouncedQuoteKey, quote: null, error: codeText(err, t('goods.quoteError')) })
        }
      })
  }, [debouncedQuoteKey, employee.id, codeText, t])

  const currentQuote = quoteResult !== null && quoteResult.key === quoteKey ? quoteResult : null
  const canSave = quoteKey !== null && currentQuote?.quote != null && !saving

  // The key stays until a registration succeeds, even if the draft is edited: after a lost answer the first attempt may
  // have been registered, and the server hands that one back instead of registering the goods twice (as a staff order).
  const changeDraft = (change: () => void) => {
    change()
    setStatus(null)
    setError(null)
  }

  const addLine = (option: StaffPresentationOption) =>
    changeDraft(() =>
      setLines((previous) =>
        previous.some((line) => line.option.presentationId === option.presentationId) ? previous : [...previous, { option, quantityText: '' }],
      ),
    )

  const save = async () => {
    if (!canSave || quoteKey === null) return
    goodsIdRef.current ??= crypto.randomUUID()
    setSaving(true)
    setError(null)
    try {
      const request = JSON.parse(quoteKey) as { occurredOn: string; lines: { presentationId: string; quantity: number }[] }
      const registration = await registerGoods(employee.id, { goodsId: goodsIdRef.current, ...request })
      goodsIdRef.current = null
      setLines([])
      setDraftNumber((n) => n + 1)
      setStatus(t('goods.saved', { amount: formatMoney(registration.total) }))
      await load()
    } catch (err) {
      setError(codeText(err, t('errors.save')))
    } finally {
      setSaving(false)
    }
  }

  const quotedLine = (presentationId: string) => currentQuote?.quote?.lines.find((line) => line.presentationId === presentationId)

  return (
    <FormPage title={t('goods.title', { name: employee.fullName })} description={t('goods.description')} onBack={onBack} backLabel={t('form.back')}>
      <div className="flex flex-col gap-6">
        {!takesGoods ? (
          <p role="status" className="rounded-md border border-amber-500/40 bg-amber-500/10 px-4 py-3 text-sm">
            {t('goods.noAccount')}
          </p>
        ) : (
          <div className="flex max-w-3xl flex-col gap-4">
            <div className="flex w-48 flex-col gap-1.5">
              <Label htmlFor="goodsDate">{t('goods.date')}</Label>
              <Input id="goodsDate" type="date" max={todayIso()} value={occurredOn} onChange={(e) => changeDraft(() => setOccurredOn(e.target.value))} />
              {!dateValid && <span className="text-xs text-destructive">{t('goods.errors.date')}</span>}
            </div>
            <ProductSearch key={`products-${draftNumber}`} onAdd={addLine} />
            <h2 className="text-sm font-medium">{t('goods.linesTitle')}</h2>
            {lines.length === 0 ? (
              <p className="text-sm text-muted-foreground">{t('goods.noLines')}</p>
            ) : (
              <ul className="flex flex-col gap-2">
                {readLines.map(({ line, error: quantityError }) => {
                  const name = presentationName(line.option)
                  // owner feedback P1a: the box says its unit (a weighed or bulk product goes in kilos).
                  const byUnit = line.option.quantityBehavior === 'FixedQuantity'
                  const quoted = quotedLine(line.option.presentationId)
                  return (
                    <li key={line.option.presentationId} className="flex flex-wrap items-start gap-3 rounded-lg border border-border bg-card px-4 py-3">
                      <span className="flex min-w-40 flex-1 flex-col">
                        <span className="font-medium">{line.option.productName}</span>
                        <span className="text-sm text-muted-foreground">{line.option.presentationName}</span>
                      </span>
                      <span className="flex w-44 flex-col gap-1">
                        <span className="flex items-center gap-2">
                          <Input
                            aria-label={t('goods.quantityLabel', { name })}
                            inputMode={byUnit ? 'numeric' : 'decimal'}
                            placeholder={byUnit ? t('goods.unitsPlaceholder') : t('goods.kilosPlaceholder', { example: numberFormat.example })}
                            value={line.quantityText}
                            onChange={(e) =>
                              changeDraft(() =>
                                setLines((previous) =>
                                  previous.map((other) =>
                                    other.option.presentationId === line.option.presentationId ? { ...other, quantityText: e.target.value } : other,
                                  ),
                                ),
                              )
                            }
                          />
                          <span className="text-sm text-muted-foreground">{byUnit ? t('goods.unitsSuffix') : t('goods.kilosSuffix')}</span>
                        </span>
                        {quantityError && <span className="text-xs text-destructive">{quantityError}</span>}
                      </span>
                      <span className="w-32 text-right text-sm tabular-nums">
                        {quoted && (
                          <>
                            <span className="block text-muted-foreground">{formatMoney(quoted.unitPrice)}</span>
                            <span className="block font-medium">{formatMoney(quoted.lineTotal)}</span>
                          </>
                        )}
                      </span>
                      <Button
                        type="button"
                        variant="outline"
                        size="sm"
                        aria-label={t('goods.removeLabel', { name })}
                        onClick={() =>
                          changeDraft(() => setLines((previous) => previous.filter((other) => other.option.presentationId !== line.option.presentationId)))
                        }
                      >
                        {t('goods.remove')}
                      </Button>
                    </li>
                  )
                })}
              </ul>
            )}
            {quoteKey !== null &&
              (currentQuote === null ? (
                <p className="text-sm text-muted-foreground">{t('goods.pricing')}</p>
              ) : currentQuote.error ? (
                <p role="alert" className="text-sm text-destructive">
                  {currentQuote.error}
                </p>
              ) : (
                <p className="text-sm font-medium">{t('goods.total', { amount: formatMoney(currentQuote.quote!.total) })}</p>
              ))}
            <div>
              <Button type="button" disabled={!canSave} onClick={() => void save()}>
                {saving ? t('goods.saving') : t('goods.save')}
              </Button>
            </div>
          </div>
        )}

        {error && (
          <p role="alert" className="text-sm text-destructive">
            {error}
          </p>
        )}
        {status && (
          <p role="status" className="text-sm text-muted-foreground">
            {status}
          </p>
        )}

        <h2 className="text-sm font-medium">{t('goods.history')}</h2>
        {loadError ? (
          <p role="alert" className="text-sm text-destructive">
            {loadError}
          </p>
        ) : loading ? null : items.length === 0 ? (
          <p className="rounded-lg border border-dashed border-border px-4 py-6 text-center text-sm text-muted-foreground">{t('goods.empty')}</p>
        ) : (
          <div className="w-full overflow-x-auto rounded-lg border border-border bg-card">
            <table className="w-full text-left text-sm">
              <thead className="border-b border-border text-xs text-muted-foreground uppercase">
                <tr>
                  <th className="px-4 py-2 font-medium">{t('goods.columns.date')}</th>
                  <th className="px-4 py-2 font-medium">{t('goods.columns.product')}</th>
                  <th className="px-4 py-2 text-right font-medium">{t('goods.columns.quantity')}</th>
                  <th className="hidden px-4 py-2 text-right font-medium md:table-cell">{t('goods.columns.price')}</th>
                  <th className="px-4 py-2 text-right font-medium">{t('goods.columns.total')}</th>
                  <th className="hidden px-4 py-2 font-medium md:table-cell">{t('goods.columns.source')}</th>
                  <th className="px-4 py-2 font-medium">{t('goods.columns.status')}</th>
                </tr>
              </thead>
              <tbody>
                {items.map((item) => (
                  <tr key={item.id} className="border-b border-border last:border-0">
                    <td className="px-4 py-2">{formatIsoDate(item.occurredOn)}</td>
                    <td className="px-4 py-2">
                      <span className="block">{item.productName}</span>
                      <span className="block text-xs text-muted-foreground">{item.presentationName}</span>
                    </td>
                    <td className="px-4 py-2 text-right tabular-nums">{decimalText(item.quantity)}</td>
                    <td className="hidden px-4 py-2 text-right tabular-nums md:table-cell">{formatMoney(item.unitPrice)}</td>
                    <td className="px-4 py-2 text-right tabular-nums">{formatMoney(item.lineTotal)}</td>
                    <td className="hidden px-4 py-2 md:table-cell">{t(`goods.source.${item.sourceType}`)}</td>
                    <td className="px-4 py-2">{t(`goods.status.${item.status}`, { run: item.runNumber })}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>
    </FormPage>
  )
}
