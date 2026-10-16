import { useEffect, useState, type FormEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { Button } from '@/components/ui/button'
import { Input } from '@/components/ui/input'
import { Label } from '@/components/ui/label'
import { Select } from '@/components/ui/select'
import { FormPage } from '@/components/layout/FormPage'
import {
  changeProductCategory,
  createPresentation,
  createProduct,
  listPresentations,
  renameProduct,
  updatePresentation,
} from '@/api/catalog'
import { ApiError } from '@/api/client'
import { suggestNextIdentificationCode, type CodeSuggestion } from '@/catalog/nextIdentificationCode'
import {
  ManagementOutcomeStatus,
  QuantityBehavior,
  type CategoryRecord,
  type PresentationRecord,
  type ProductRecord,
} from '@/api/types'

/**
 * There is no units table: a presentation's `unitId` is an opaque id that only has to be stable per kind of unit. A new
 * or re-classified presentation reuses the unit of an existing presentation with the same quantity behavior (the seeded
 * "kg" of the weighted cuts), else these fixed ids, one per behavior.
 */
const FALLBACK_UNIT_IDS: Record<QuantityBehavior, string> = {
  [QuantityBehavior.FixedQuantity]: '7c1d6f0e-3b1a-4c8e-9f00-000000000001',
  [QuantityBehavior.Weighted]: '7c1d6f0e-3b1a-4c8e-9f00-000000000002',
  [QuantityBehavior.Bulk]: '7c1d6f0e-3b1a-4c8e-9f00-000000000003',
}

function unitIdFor(behavior: QuantityBehavior, presentations: readonly PresentationRecord[]): string {
  return presentations.find((presentation) => presentation.quantityBehavior === behavior)?.unitId ?? FALLBACK_UNIT_IDS[behavior]
}

const BEHAVIOR_OPTIONS: { value: QuantityBehavior; key: 'weighted' | 'fixedQuantity' | 'bulk' }[] = [
  { value: QuantityBehavior.Weighted, key: 'weighted' },
  { value: QuantityBehavior.FixedQuantity, key: 'fixedQuantity' },
  { value: QuantityBehavior.Bulk, key: 'bulk' },
]

export type CatalogFormTarget =
  | { mode: 'create' }
  | { mode: 'edit'; presentation: PresentationRecord; product: ProductRecord | null }

/**
 * Creates a product with its presentation, or edits both: product name and category, presentation name, quantity
 * behavior and identification code. A product sold by kilo is "Pesable"; by the unit, "Cantidad fija".
 *
 * Editing works with what could be loaded: without the product (its read failed) only the presentation is edited, and
 * without the categories the category select is not offered.
 */
export function CatalogProductForm({
  target,
  categories,
  presentations,
  onCancel,
  onSaved,
}: {
  target: CatalogFormTarget
  categories: readonly CategoryRecord[]
  presentations: readonly PresentationRecord[]
  onCancel: () => void
  onSaved: (result: { presentation: PresentationRecord; product: ProductRecord | null }) => void
}) {
  const { t } = useTranslation('catalog')
  const editing = target.mode === 'edit' ? target : null
  const product = editing?.product ?? null

  const [productName, setProductName] = useState(product?.name ?? '')
  const [categoryId, setCategoryId] = useState(product?.categoryId ?? categories[0]?.id ?? '')
  const [quantityBehavior, setQuantityBehavior] = useState<QuantityBehavior>(
    editing?.presentation.quantityBehavior ?? QuantityBehavior.Weighted,
  )
  const [presentationName, setPresentationName] = useState(
    editing?.presentation.name ?? t('productForm.defaultPresentation.weighted'),
  )
  // A new product starts with the next internal code proposed from what the screen already has; the complete list
  // (inactive products included, which also hold their codes) refines it once loaded. Typing a code keeps it.
  const [suggestion, setSuggestion] = useState<CodeSuggestion | null>(() =>
    suggestNextIdentificationCode(presentations.map((presentation) => presentation.identificationCode)),
  )
  const [identificationCode, setIdentificationCode] = useState(
    editing?.presentation.identificationCode ?? (target.mode === 'create' ? (suggestion?.next ?? '') : ''),
  )
  const [codeTouched, setCodeTouched] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [submitting, setSubmitting] = useState(false)

  useEffect(() => {
    let cancelled = false
    listPresentations(true).then(
      (all) => {
        if (cancelled || !Array.isArray(all)) return
        const complete = suggestNextIdentificationCode(all.map((presentation) => presentation.identificationCode))
        setSuggestion(complete)
        if (target.mode === 'create' && !codeTouched && complete) setIdentificationCode(complete.next)
      },
      () => {},
    )
    return () => {
      cancelled = true
    }
    // Loaded once per form: a later keystroke must not re-run it.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  const showProductFields = editing === null || product !== null
  const showCategory = showProductFields && categories.some((category) => category.id === categoryId)

  const changeBehavior = (value: QuantityBehavior) => {
    // A presentation name that was still the suggested one follows the behavior ("Por kg" -> "Unidad").
    const suggested = Object.values(QuantityBehavior).map((behavior) => defaultPresentationName(behavior))
    if (presentationName.trim() === '' || suggested.includes(presentationName)) {
      setPresentationName(defaultPresentationName(value))
    }
    setQuantityBehavior(value)
  }

  function defaultPresentationName(behavior: QuantityBehavior): string {
    const key = BEHAVIOR_OPTIONS.find((option) => option.value === behavior)?.key ?? 'weighted'
    return t(`productForm.defaultPresentation.${key}`)
  }

  const describeError = (err: unknown, fallbackKey: string): string => {
    if (err instanceof ApiError) {
      if (err.code === 'identification-code-in-use') return t('productForm.errors.codeInUse')
      if (err.code === 'product-inactive') return t('productForm.errors.productInactive')
      return err.message
    }
    return t(fallbackKey)
  }

  const handleSubmit = async (event: FormEvent) => {
    event.preventDefault()
    setError(null)
    const name = productName.trim()
    const code = identificationCode.trim() === '' ? null : identificationCode.trim()
    if (showProductFields && name === '') {
      setError(t('productForm.errors.nameRequired'))
      return
    }
    if (presentationName.trim() === '') {
      setError(t('productForm.errors.presentationRequired'))
      return
    }

    setSubmitting(true)
    try {
      if (editing === null) {
        const unitId = unitIdFor(quantityBehavior, presentations)
        const created = await createProduct({ name, ...(categoryId ? { categoryId } : {}), defaultUnitId: unitId })
        const presentation = await createPresentation({
          productId: created.id,
          name: presentationName.trim(),
          quantityBehavior,
          unitId,
          identificationCode: code,
        })
        onSaved({ presentation, product: created })
        return
      }

      const current = editing.presentation
      const unitId =
        quantityBehavior === current.quantityBehavior ? current.unitId : unitIdFor(quantityBehavior, presentations)
      const presentation = await updatePresentation(current.id, {
        name: presentationName.trim(),
        quantityBehavior,
        unitId,
        identificationCode: code,
      })

      let savedProduct = product
      if (product !== null && name !== product.name) {
        const outcome = await renameProduct(product.id, {
          newName: name,
          isOffline: false,
          correlationId: crypto.randomUUID(),
        })
        if (outcome.status !== ManagementOutcomeStatus.Allowed) {
          throw new ApiError(outcome.reason || t('productForm.errors.renameDenied'), 403)
        }
        savedProduct = { ...product, name }
      }
      if (savedProduct !== null && showCategory && categoryId !== savedProduct.categoryId) {
        savedProduct = await changeProductCategory(savedProduct.id, categoryId)
      }
      onSaved({ presentation, product: savedProduct })
    } catch (err) {
      setError(describeError(err, editing === null ? 'productForm.errors.unexpectedCreate' : 'errors.unexpectedUpdate'))
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <FormPage
      title={editing === null ? t('productForm.createTitle') : t('form.title')}
      description={
        editing === null
          ? t('productForm.createDescription')
          : t('form.description', { name: product?.name ?? editing.presentation.name })
      }
      onBack={onCancel}
      backLabel={t('form.backLabel')}
    >
      <form className="flex max-w-xl flex-col gap-4" onSubmit={handleSubmit} noValidate>
        {showProductFields && (
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="productName">{t('productForm.productName')}</Label>
            <Input id="productName" value={productName} onChange={(e) => setProductName(e.target.value)} autoFocus />
          </div>
        )}
        {showCategory && (
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="productCategory">{t('form.category')}</Label>
            <Select id="productCategory" value={categoryId} onChange={(e) => setCategoryId(e.target.value)}>
              {categories.map((category) => (
                <option key={category.id} value={category.id}>
                  {category.name}
                </option>
              ))}
            </Select>
          </div>
        )}
        <div className="grid gap-4 sm:grid-cols-2">
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="quantityBehavior">{t('columns.quantityBehavior')}</Label>
            <Select
              id="quantityBehavior"
              value={String(quantityBehavior)}
              onChange={(e) => changeBehavior(Number(e.target.value) as QuantityBehavior)}
            >
              {BEHAVIOR_OPTIONS.map((option) => (
                <option key={option.value} value={option.value}>
                  {t(`quantityBehaviorOptions.${option.key}`)}
                </option>
              ))}
            </Select>
            <p className="text-xs text-muted-foreground">{t('productForm.quantityBehaviorHint')}</p>
          </div>
          <div className="flex flex-col gap-1.5">
            <Label htmlFor="presentationName">{t('productForm.presentationName')}</Label>
            <Input id="presentationName" value={presentationName} onChange={(e) => setPresentationName(e.target.value)} />
          </div>
        </div>
        <div className="flex flex-col gap-1.5">
          <Label htmlFor="identificationCode">{t('form.label')}</Label>
          <div className="flex gap-2">
            <Input
              id="identificationCode"
              value={identificationCode}
              placeholder={suggestion ? t('productForm.codeSuggestion.placeholder', { next: suggestion.next }) : undefined}
              aria-describedby="identificationCode-hint identificationCode-last"
              onChange={(e) => {
                setCodeTouched(true)
                setIdentificationCode(e.target.value)
              }}
            />
            {suggestion && identificationCode.trim() !== suggestion.next && (
              <Button
                type="button"
                variant="outline"
                onClick={() => {
                  setCodeTouched(true)
                  setIdentificationCode(suggestion.next)
                }}
              >
                {t('productForm.codeSuggestion.use', { next: suggestion.next })}
              </Button>
            )}
          </div>
          {suggestion && (
            <p id="identificationCode-last" className="text-xs text-muted-foreground">
              {target.mode === 'create'
                ? t('productForm.codeSuggestion.proposed', { last: suggestion.last, next: suggestion.next })
                : t('productForm.codeSuggestion.last', { last: suggestion.last, next: suggestion.next })}
            </p>
          )}
          <p id="identificationCode-hint" className="text-xs text-muted-foreground">{t('productForm.codeHint')}</p>
        </div>
        {editing === null && <p className="text-sm text-muted-foreground">{t('productForm.priceNote')}</p>}
        {error && (
          <p role="alert" className="text-sm text-destructive">
            {error}
          </p>
        )}
        <div className="flex gap-2">
          <Button type="submit" disabled={submitting}>
            {submitting ? t('form.saving') : editing === null ? t('productForm.create') : t('form.save')}
          </Button>
          <Button type="button" variant="outline" onClick={onCancel} disabled={submitting}>
            {t('form.cancel')}
          </Button>
        </div>
      </form>
    </FormPage>
  )
}
