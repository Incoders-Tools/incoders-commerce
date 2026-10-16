import { useEffect, useState, type FormEvent } from 'react'
import { useTranslation } from 'react-i18next'
import { Button } from '@/components/ui/button'
import { Label } from '@/components/ui/label'
import { Select } from '@/components/ui/select'
import { FormPage } from '@/components/layout/FormPage'
import { copyCatalog, listProducts } from '@/api/catalog'
import type { CopyCatalogResponse, ProductRecord, SelectableBranch } from '@/api/types'

type CopyMode = 'whole' | 'selected'

/**
 * B7 U5b (catalog-item-identification spec, "Copying Catalog Between
 * Branches"): copy the whole catalog, or chosen products, from the branch
 * currently selected (the SOURCE — the server takes it from `X-Branch-Id`)
 * into another branch of the same organization. Which branches may be
 * targeted, and who may copy at all, is decided by the server; this form
 * only offers the caller's own selectable branches.
 */
export function CatalogCopyForm({
  sourceBranch,
  targetBranches,
  onBack,
}: {
  sourceBranch: SelectableBranch
  targetBranches: SelectableBranch[]
  onBack: () => void
}) {
  const { t } = useTranslation('catalog')
  const [targetBranchId, setTargetBranchId] = useState('')
  const [mode, setMode] = useState<CopyMode>('whole')
  const [products, setProducts] = useState<ProductRecord[] | null>(null)
  const [productsError, setProductsError] = useState(false)
  const [selectedProductIds, setSelectedProductIds] = useState<string[]>([])
  const [submitting, setSubmitting] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [result, setResult] = useState<CopyCatalogResponse | null>(null)

  useEffect(() => {
    if (mode !== 'selected' || products !== null) return
    let cancelled = false
    setProductsError(false)
    listProducts()
      .then((items) => {
        if (!cancelled) setProducts(items)
      })
      .catch(() => {
        if (!cancelled) setProductsError(true)
      })
    return () => {
      cancelled = true
    }
  }, [mode, products])

  const toggleProduct = (productId: string) => {
    setSelectedProductIds((current) =>
      current.includes(productId) ? current.filter((id) => id !== productId) : [...current, productId],
    )
  }

  const canSubmit =
    targetBranchId !== '' && (mode === 'whole' || selectedProductIds.length > 0) && !submitting

  const handleSubmit = async (event: FormEvent) => {
    event.preventDefault()
    if (!canSubmit) return
    setError(null)
    setResult(null)
    setSubmitting(true)
    try {
      setResult(
        await copyCatalog({
          sourceBranchId: sourceBranch.id,
          targetBranchId,
          ...(mode === 'selected' ? { productIds: selectedProductIds } : {}),
        }),
      )
    } catch {
      setError(t('copy.errors.failed'))
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <FormPage
      title={t('copy.title')}
      description={t('copy.description', { source: sourceBranch.name })}
      onBack={onBack}
      backLabel={t('form.backLabel')}
    >
      <form className="flex max-w-md flex-col gap-5" onSubmit={handleSubmit}>
        <div className="flex flex-col gap-1.5">
          <Label htmlFor="copyTargetBranch">{t('copy.target')}</Label>
          <Select
            id="copyTargetBranch"
            value={targetBranchId}
            onChange={(e) => setTargetBranchId(e.target.value)}
          >
            <option value="">{t('copy.targetPlaceholder')}</option>
            {targetBranches.map((branch) => (
              <option key={branch.id} value={branch.id}>
                {branch.name}
              </option>
            ))}
          </Select>
        </div>

        <fieldset className="flex flex-col gap-2">
          <legend className="mb-1 text-sm font-medium text-foreground">{t('copy.scope')}</legend>
          <label className="flex items-center gap-2 text-sm">
            <input type="radio" name="copyMode" checked={mode === 'whole'} onChange={() => setMode('whole')} />
            {t('copy.whole')}
          </label>
          <label className="flex items-center gap-2 text-sm">
            <input type="radio" name="copyMode" checked={mode === 'selected'} onChange={() => setMode('selected')} />
            {t('copy.selected')}
          </label>
        </fieldset>

        {mode === 'selected' && (
          <div className="flex max-h-64 flex-col gap-1.5 overflow-y-auto rounded-md border border-input p-3">
            {productsError ? (
              <p role="alert" className="text-sm text-destructive">
                {t('copy.errors.products')}
              </p>
            ) : products === null ? (
              <p className="text-sm text-muted-foreground">{t('copy.loadingProducts')}</p>
            ) : products.length === 0 ? (
              <p className="text-sm text-muted-foreground">{t('copy.noProducts')}</p>
            ) : (
              products.map((product) => (
                <label key={product.id} className="flex items-center gap-2 text-sm">
                  <input
                    type="checkbox"
                    checked={selectedProductIds.includes(product.id)}
                    onChange={() => toggleProduct(product.id)}
                  />
                  {product.name}
                </label>
              ))
            )}
          </div>
        )}

        <p className="text-sm text-muted-foreground">{t('copy.pricesNote')}</p>

        {error && (
          <p role="alert" className="text-sm text-destructive">
            {error}
          </p>
        )}

        <div className="flex gap-2">
          <Button type="submit" disabled={!canSubmit}>
            {submitting ? t('copy.copying') : t('copy.submit')}
          </Button>
          <Button type="button" variant="outline" onClick={onBack} disabled={submitting}>
            {t('form.cancel')}
          </Button>
        </div>
      </form>

      {result && <CopyResult result={result} />}
    </FormPage>
  )
}

function CopyResult({ result }: { result: CopyCatalogResponse }) {
  const { t } = useTranslation('catalog')
  return (
    <div role="status" className="mt-6 flex max-w-md flex-col gap-2 rounded-md border border-input p-4 text-sm">
      <h2 className="font-semibold text-foreground">{t('copy.result.title')}</h2>
      <p>{t('copy.result.products', { count: result.productsCopied })}</p>
      <p>{t('copy.result.presentations', { count: result.presentationsCopied })}</p>
      <p>
        {result.priceListId === null
          ? t('copy.result.noPriceList')
          : t('copy.result.prices', { count: result.priceEntriesCopied })}
      </p>
      {result.skipped.length > 0 && (
        <div>
          <p>{t('copy.result.skipped', { count: result.skipped.length })}</p>
          <p className="text-muted-foreground">{t('copy.result.skippedNote')}</p>
          <ul className="mt-1 list-disc pl-5">
            {result.skipped.map((item) => (
              <li key={item.presentationId}>{item.identificationCode ?? t('columns.noCode')}</li>
            ))}
          </ul>
        </div>
      )}
    </div>
  )
}
