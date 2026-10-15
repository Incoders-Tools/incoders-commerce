import { describe, expect, it } from 'vitest'
import { goodsDiscount, itemDiscount } from './goodsDiscount'

/** employee-lifecycle E4: the server's `PayslipPurchases`, mirrored for the payslip editor's preview. */
describe('goodsDiscount', () => {
  it('takes the item discounts first, then the percent on the rest, and never forgives more than is owed', () => {
    expect(itemDiscount(15000, 12.5)).toBe(1875)
    expect(itemDiscount(15000, 0)).toBe(0)
    expect(goodsDiscount(30000, 10, 5000)).toEqual({ itemsDiscount: 5000, discount: 7500, deducted: 22500 })
    expect(goodsDiscount(50000, 10, 0)).toEqual({ itemsDiscount: 0, discount: 5000, deducted: 45000 })
    expect(goodsDiscount(1000, 50, 3000)).toEqual({ itemsDiscount: 1000, discount: 1000, deducted: 0 })
    expect(goodsDiscount(100, 33.33, 0).discount).toBe(33.33)
    expect(itemDiscount(1.01, 50)).toBe(0.51) // to the cent, half away from zero
  })
})
