import { useTranslation } from 'react-i18next'
import { Plus, Trash2 } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Field } from '@/components/form/FormParts'
import { PresentationPicker } from '@/components/purchasing/PresentationPicker'
import type { PresentationOption } from '@/components/purchasing/presentationOptions'
import { formatMoney } from '@/dashboard/format'
import { parseAmount, parseQuantity, type DecimalSeparator } from '@/lib/quantity'
import { useNumberFormat } from '@/organization/NumberFormatContext'

/** One editable line. Numbers stay as the text the operator typed until the form is submitted. */
export interface LineDraft {
  key: string
  presentation: PresentationOption | null
  quantity: string
  unitCost: string
  lotCode: string
  expiresOn: string
}

export type LineErrors = Partial<Record<'presentationId' | 'quantity' | 'unitCost' | 'lotCode' | 'expiresOn', string>>

let nextKey = 0
export const newLineDraft = (): LineDraft => ({
  key: `line-${nextKey++}`,
  presentation: null,
  quantity: '',
  unitCost: '',
  lotCode: '',
  expiresOn: '',
})

const roundCents = (value: number) => Math.round(value * 100) / 100

/** Quantity (in the organization's format) times unit cost (es-AR money), to the cent; 0 while either is blank or unreadable. */
export function lineTotal(line: Pick<LineDraft, 'quantity' | 'unitCost'>, separator: DecimalSeparator): number {
  const quantity = parseQuantity(line.quantity, separator)
  const unitCost = parseAmount(line.unitCost)
  return quantity === null || unitCost === null ? 0 : roundCents(quantity * unitCost)
}

export const receptionTotal = (lines: LineDraft[], separator: DecimalSeparator) =>
  roundCents(lines.reduce((sum, line) => sum + lineTotal(line, separator), 0))

interface ReceptionLinesEditorProps {
  lines: LineDraft[]
  options: PresentationOption[]
  optionsFailed: boolean
  errors: Record<number, LineErrors>
  onChange: (lines: LineDraft[]) => void
}

/**
 * Lines of a draft reception. Each line is a bordered card on a phone and a
 * single grid row from `md:` up; every control carries its line number in its
 * accessible name so a screen reader hears which line it edits.
 */
export function ReceptionLinesEditor({ lines, options, optionsFailed, errors, onChange }: ReceptionLinesEditorProps) {
  const { t } = useTranslation('purchases')
  const numberFormat = useNumberFormat()

  const update = (key: string, patch: Partial<LineDraft>) =>
    onChange(lines.map((line) => (line.key === key ? { ...line, ...patch } : line)))

  return (
    <div className="flex flex-col gap-3">
      {lines.map((line, index) => {
        const n = index + 1
        const lineErrors = errors[index] ?? {}
        const unit = line.presentation
          ? line.presentation.behavior === 'FixedQuantity'
            ? t('lines.unit.units')
            : t('lines.unit.kg')
          : null
        return (
          <div
            key={line.key}
            className="grid grid-cols-2 items-start gap-3 rounded-lg border border-border p-3 md:grid-cols-12"
          >
            <PresentationPicker
              className="col-span-2 md:col-span-4"
              label={t('lines.presentation', { n })}
              placeholder={t('lines.presentationPlaceholder')}
              options={options}
              loadFailed={optionsFailed}
              value={line.presentation}
              error={lineErrors.presentationId ?? null}
              onChange={(presentation) => update(line.key, { presentation })}
            />
            <div className="col-span-1 md:col-span-2">
              <Field
                id={`${line.key}-quantity`}
                label={t('lines.quantity', { n })}
                value={line.quantity}
                onChange={(quantity) => update(line.key, { quantity })}
                error={lineErrors.quantity ?? null}
                hint={unit ?? undefined}
                placeholder={numberFormat.example}
                inputMode="decimal"
              />
            </div>
            <Field
              id={`${line.key}-unitCost`}
              className="col-span-1 md:col-span-2"
              label={t('lines.unitCost', { n })}
              value={line.unitCost}
              onChange={(unitCost) => update(line.key, { unitCost })}
              error={lineErrors.unitCost ?? null}
            />
            <div className="col-span-1 flex flex-col gap-1.5 md:col-span-2">
              <span className="text-sm font-medium text-foreground">{t('lines.lineTotal')}</span>
              <output aria-label={`${t('lines.lineTotal')} ${n}`} className="flex h-9 items-center text-sm tabular-nums">
                {formatMoney(lineTotal(line, numberFormat.separator))}
              </output>
            </div>
            <Field
              id={`${line.key}-lotCode`}
              className="col-span-1 md:col-span-2"
              label={t('lines.lotCode', { n })}
              value={line.lotCode}
              onChange={(lotCode) => update(line.key, { lotCode })}
              error={lineErrors.lotCode ?? null}
            />
            <Field
              id={`${line.key}-expiresOn`}
              className="col-span-1 md:col-span-2"
              type="date"
              label={t('lines.expiresOn', { n })}
              value={line.expiresOn}
              onChange={(expiresOn) => update(line.key, { expiresOn })}
              error={lineErrors.expiresOn ?? null}
            />
            <div className="col-span-2 flex justify-end md:col-span-12">
              <Button
                type="button"
                variant="outline"
                size="sm"
                aria-label={t('lines.remove', { n })}
                disabled={lines.length === 1}
                onClick={() => onChange(lines.filter((candidate) => candidate.key !== line.key))}
              >
                <Trash2 aria-hidden="true" className="size-4" />
                <span className="sr-only md:not-sr-only">{t('lines.remove', { n })}</span>
              </Button>
            </div>
          </div>
        )
      })}
      <div>
        <Button type="button" variant="outline" size="sm" onClick={() => onChange([...lines, newLineDraft()])}>
          <Plus aria-hidden="true" className="size-4" />
          {t('lines.add')}
        </Button>
      </div>
    </div>
  )
}
