/** What a screen keeps about a chosen city: enough to render it without refetching. */
export interface CityOption {
  id: string
  name: string
  provinceName: string
}

/** "Name — Province" (just the name when the province is unknown). */
export const cityLabel = (city: Pick<CityOption, 'name' | 'provinceName'>) =>
  city.provinceName ? `${city.name} — ${city.provinceName}` : city.name
