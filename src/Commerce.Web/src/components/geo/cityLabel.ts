/** What a screen keeps about a chosen city: enough to render it without refetching. */
export interface CityOption {
  id: string
  name: string
  provinceName: string
  provinceId?: string
  /** Known postal code of the city, if any: a form may prefill its own postal code with it. */
  postalCode?: string | null
}

/** "Name — Province" (just the name when the province is unknown). */
export const cityLabel = (city: Pick<CityOption, 'name' | 'provinceName'>) =>
  city.provinceName ? `${city.name} — ${city.provinceName}` : city.name
