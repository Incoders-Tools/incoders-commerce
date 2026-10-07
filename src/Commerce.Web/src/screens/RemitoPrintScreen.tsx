import { useEffect, useRef, useState } from 'react'
import { useNavigate, useSearchParams } from 'react-router'
import { useTranslation } from 'react-i18next'
import { Button } from '@/components/ui/button'
import { fulfillmentErrorMessage } from '@/components/fulfillment/fulfillmentErrors'
import { getRemitos, type RemitoDocument } from '@/api/fulfillment'
import { formatIsoDate, formatMoney } from '@/dashboard/format'
import { useNumberFormat } from '@/organization/NumberFormatContext'

const FALLBACK_COLOR = '#1f2937'

/**
 * Printable remitos (delivery notes) of `?orders=a,b,c`, one A4 page per copy and two copies per order: ORIGINAL (for
 * the customer) and DUPLICADO (stays with the organization, signed on receipt; it is what is settled when the truck
 * returns). Styled with the organization's logo and brand color; the issuer data comes from the organization and the
 * branch document data (Settings → Documents, System → Branches). Opens the print dialog once the remitos are ready.
 * Rendered outside the app shell so only the remitos are printed.
 */
export function RemitoPrintScreen() {
  const { t } = useTranslation('fulfillment')
  const [searchParams] = useSearchParams()
  const navigate = useNavigate()
  const [remitos, setRemitos] = useState<RemitoDocument[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const printed = useRef(false)

  useEffect(() => {
    const ids = (searchParams.get('orders') ?? '').split(',').filter(Boolean)
    if (ids.length === 0) {
      setRemitos([])
      return
    }
    let cancelled = false
    getRemitos(ids)
      .then((result) => {
        if (!cancelled) setRemitos(result)
      })
      .catch((err) => {
        if (!cancelled) setError(fulfillmentErrorMessage(err, t, 'remito.loadError'))
      })
    return () => {
      cancelled = true
    }
  }, [searchParams, t])

  useEffect(() => {
    if (remitos && remitos.length > 0 && !printed.current) {
      printed.current = true
      // Let the logo and fonts lay out before the dialog snapshots the page.
      const timer = window.setTimeout(() => window.print(), 400)
      return () => window.clearTimeout(timer)
    }
  }, [remitos])

  return (
    <div className="min-h-screen bg-muted/40 print:bg-white">
      <style>{`
        @page { size: A4; margin: 10mm; }
        @media print {
          .remito-page { break-after: page; box-shadow: none !important; margin: 0 !important; border: none !important; }
          .remito-page:last-child { break-after: auto; }
        }
      `}</style>
      <div className="sticky top-0 z-10 flex items-center gap-2 border-b border-border bg-card px-4 py-2 print:hidden">
        <Button type="button" variant="outline" size="sm" onClick={() => navigate(-1)}>
          {t('remito.back')}
        </Button>
        <Button type="button" size="sm" disabled={!remitos || remitos.length === 0} onClick={() => window.print()}>
          {t('remito.print')}
        </Button>
        <span className="text-sm text-muted-foreground">
          {remitos === null && !error ? t('remito.printing') : remitos ? `${remitos.length} × 2` : ''}
        </span>
      </div>

      {error && (
        <p role="alert" className="m-6 text-sm text-destructive">
          {error}
        </p>
      )}
      {remitos?.length === 0 && (
        <p role="status" className="m-6 text-sm text-muted-foreground">
          {t('remito.none')}
        </p>
      )}

      <div className="flex flex-col items-center gap-6 py-6 print:block print:py-0">
        {remitos?.flatMap((remito) => [
          <RemitoPage key={`${remito.orderId}-o`} remito={remito} copy="original" />,
          <RemitoPage key={`${remito.orderId}-d`} remito={remito} copy="duplicate" />,
        ])}
      </div>
    </div>
  )
}

function RemitoPage({ remito, copy }: { remito: RemitoDocument; copy: 'original' | 'duplicate' }) {
  const { t } = useTranslation('fulfillment')
  const numberFormat = useNumberFormat()
  const { organization, branch, customer } = remito
  const color = organization.primaryColor ?? FALLBACK_COLOR
  const branchAddress = [branch.address, branch.locality].filter(Boolean).join(' - ')
  const customerAddress = [customer.address, customer.locality, customer.province, customer.postalCode].filter(Boolean).join(', ')

  return (
    <article
      className="remito-page flex w-[210mm] min-h-[277mm] flex-col gap-4 border border-border bg-white p-[10mm] text-[11px] leading-snug text-neutral-900 shadow-sm"
      aria-label={`${t('remito.title')} ${remito.remitoNumber} ${copy === 'original' ? t('remito.original') : t('remito.duplicate')}`}
    >
      {/* Issuer | document letter | document data */}
      <header className="grid grid-cols-[1fr_auto_1fr] border-2" style={{ borderColor: color }}>
        <div className="flex gap-3 p-3">
          {organization.logoUrl && (
            <img src={organization.logoUrl} alt={organization.name} className="h-16 w-16 shrink-0 object-contain" />
          )}
          <div className="min-w-0">
            <p className="text-base font-bold uppercase" style={{ color }}>
              {organization.name}
            </p>
            {organization.legalName && organization.legalName !== organization.name && <p className="font-semibold">{organization.legalName}</p>}
            {branchAddress && <p>{branchAddress}</p>}
            {branch.warehouseAddress && (
              <p>
                {t('remito.issuer.warehouse')}: {branch.warehouseAddress}
              </p>
            )}
            {(branch.phone || branch.email) && (
              <p>{[branch.phone && `${t('remito.issuer.phone')} ${branch.phone}`, branch.email].filter(Boolean).join(' · ')}</p>
            )}
            {organization.fiscalAddress && organization.fiscalAddress !== branchAddress && <p>{organization.fiscalAddress}</p>}
          </div>
        </div>
        <div className="flex flex-col items-center border-x-2 px-4 py-2" style={{ borderColor: color }}>
          <span className="flex h-12 w-12 items-center justify-center border-2 text-3xl font-bold" style={{ borderColor: color, color }}>
            X
          </span>
          <span className="mt-1 max-w-[30mm] text-center text-[9px]">{t('remito.noLegalValue')}</span>
        </div>
        <div className="flex flex-col gap-0.5 p-3 text-right">
          <p className="text-lg font-bold tracking-wide" style={{ color }}>
            {t('remito.title').toUpperCase()}
          </p>
          <p className="text-sm font-semibold">
            {t('remito.number')} {remito.remitoNumber}
          </p>
          <p>
            {t('remito.date')}: {formatIsoDate(remito.issuedOn)}
          </p>
          {organization.taxId && (
            <p>
              {t('remito.issuer.taxId')}: {formatTaxId(organization.taxId)}
            </p>
          )}
          {organization.taxCondition && (
            <p>
              {t('remito.issuer.taxCondition')}: {t(`taxCondition.${organization.taxCondition}`)}
            </p>
          )}
          {organization.grossIncomeNumber && (
            <p>
              {t('remito.issuer.grossIncome')}: {organization.grossIncomeNumber}
            </p>
          )}
          {organization.activityStartDate && (
            <p>
              {t('remito.issuer.activityStart')}: {formatIsoDate(organization.activityStartDate)}
            </p>
          )}
        </div>
      </header>

      <div className="flex items-center justify-between px-1 text-[10px] font-semibold uppercase tracking-wider" style={{ color }}>
        <span>{copy === 'original' ? t('remito.original') : t('remito.duplicate')}</span>
        {copy === 'duplicate' && <span>{t('remito.duplicateNote', { name: organization.name })}</span>}
      </div>

      {/* Customer and delivery */}
      <section className="grid grid-cols-2 gap-x-6 gap-y-1 border p-3" style={{ borderColor: color }}>
        <Row label={t('remito.customer')} value={customer.displayName} />
        <Row label={t('remito.order')} value={remito.orderNumber} />
        <Row label={t('remito.legalName')} value={customer.legalName} />
        <Row label={t('remito.taxId')} value={customer.taxId ? formatTaxId(customer.taxId) : null} />
        <Row label={t('remito.taxCondition')} value={customer.taxCondition ? t(`taxCondition.${customer.taxCondition}`) : null} />
        <Row label={t('remito.phone')} value={customer.phone} />
        <div className="col-span-2">
          <Row label={t('remito.address')} value={customerAddress || null} />
        </div>
        {customer.deliveryNotes && (
          <div className="col-span-2">
            <Row label={t('remito.deliveryNotes')} value={customer.deliveryNotes} />
          </div>
        )}
        {remito.runNumber !== null && (
          <div className="col-span-2 flex flex-wrap gap-x-6">
            <span className="font-semibold">{t('remito.run', { number: remito.runNumber })}</span>
            {remito.driverName && <Row label={t('remito.driver')} value={remito.driverName} />}
            {remito.vehicle && <Row label={t('remito.vehicle')} value={remito.vehicle} />}
          </div>
        )}
      </section>

      {/* Lines */}
      <table className="w-full border-collapse">
        <thead>
          <tr className="text-left text-white" style={{ backgroundColor: color }}>
            <th className="w-[18mm] px-2 py-1 text-right font-semibold">{t('remito.columns.quantity')}</th>
            <th className="w-[20mm] px-2 py-1 text-right font-semibold">{t('remito.columns.kilos')}</th>
            <th className="px-2 py-1 font-semibold">{t('remito.columns.description')}</th>
            <th className="w-[28mm] px-2 py-1 text-right font-semibold">{t('remito.columns.unitPrice')}</th>
            <th className="w-[30mm] px-2 py-1 text-right font-semibold">{t('remito.columns.subtotal')}</th>
          </tr>
        </thead>
        <tbody>
          {remito.lines.map((line) => {
            const quantity = line.deliveredQuantity ?? line.quantity
            const weighted = line.quantityBehavior !== 'FixedQuantity'
            return (
              <tr key={line.lineNo} className="border-b border-neutral-300 align-top">
                <td className="px-2 py-1 text-right tabular-nums">{weighted ? '' : numberFormat.formatNumber(quantity)}</td>
                <td className="px-2 py-1 text-right tabular-nums">{weighted ? numberFormat.formatNumber(quantity) : ''}</td>
                <td className="px-2 py-1">
                  {line.productName} <span className="text-neutral-500">{line.presentationName}</span>
                </td>
                <td className="px-2 py-1 text-right tabular-nums">{formatMoney(line.unitNetPrice)}</td>
                <td className="px-2 py-1 text-right tabular-nums">
                  {formatMoney(Math.round(line.unitNetPrice * quantity * 100) / 100)}
                </td>
              </tr>
            )
          })}
        </tbody>
      </table>

      <div className="flex justify-end">
        <div className="flex min-w-[70mm] items-center justify-between gap-6 border-2 px-3 py-2 text-sm font-bold" style={{ borderColor: color }}>
          <span>{t('remito.total')}</span>
          <span className="tabular-nums">{formatMoney(remito.total)}</span>
        </div>
      </div>

      {remito.note && <p className="border-l-4 pl-2 italic" style={{ borderColor: color }}>{remito.note}</p>}

      {/* Receipt: signed on the duplicate, kept by the organization */}
      <footer className="mt-auto flex flex-col gap-6 pt-10">
        <div className="grid grid-cols-3 gap-8 text-center">
          {[t('remito.signature'), t('remito.clarification'), t('remito.receivedOn')].map((label) => (
            <div key={label} className="border-t border-neutral-500 pt-1">
              {label}
            </div>
          ))}
        </div>
        <p className="text-center text-[9px] text-neutral-500">{organization.documentFooter ?? t('remito.noLegalValue')}</p>
      </footer>
    </article>
  )
}

function Row({ label, value }: { label: string; value: string | null }) {
  return (
    <p>
      <span className="font-semibold">{label}:</span> {value ?? '—'}
    </p>
  )
}

/** `30712345671` -> `30-71234567-1`; anything that is not 11 digits is shown as is. */
function formatTaxId(taxId: string): string {
  return /^\d{11}$/.test(taxId) ? `${taxId.slice(0, 2)}-${taxId.slice(2, 10)}-${taxId.slice(10)}` : taxId
}
