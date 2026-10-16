/**
 * Two decimals, half away from zero: the server's `Money.Round2`. The product is cleaned to 15
 * significant digits first, so binary noise (`1.005 * 100 = 100.49999…`) cannot flip a midpoint.
 */
export function round2(value: number): number {
  const cents = Number((Math.abs(value) * 100).toPrecision(15))
  return (Math.sign(value) * Math.round(cents)) / 100
}

/** `base` raised (or lowered, with a negative `percent`) by `percent` %, to the cent. */
export function applyPercent(base: number, percent: number): number {
  return round2((base * (100 + percent)) / 100)
}

/** One component of a list's composition, as the final price needs it. */
export interface ComposableComponent {
  percentage: number
  calculationBase: 'Base' | 'Subtotal'
  order: number
}

/**
 * The final price of a base under a composition: the server's `RateComponentSet.Compose` (each component in order, on
 * the base or on the running subtotal), to the cent. With no components the final is the base.
 */
export function composeFinal(base: number, components: readonly ComposableComponent[]): number {
  let subtotal = base
  for (const component of [...components].sort((a, b) => a.order - b.order)) {
    const operand = component.calculationBase === 'Subtotal' ? subtotal : base
    subtotal += (operand * component.percentage) / 100
  }
  return round2(subtotal)
}

/** How much `next` differs from `previous`, in percent of `previous`. */
export function differencePercent(previous: number, next: number): number {
  return ((next - previous) / previous) * 100
}

/** True when `value` carries no more than two decimals. */
export function hasAtMostTwoDecimals(value: number): boolean {
  const cents = value * 100
  return Math.abs(cents - Math.round(cents)) < 1e-6
}
