import {
  Apple,
  Beef,
  Croissant,
  CupSoda,
  Drumstick,
  Fish,
  Flame,
  Milk,
  ShoppingBasket,
  SprayCan,
  Tag,
  Wine,
  type LucideIcon,
} from 'lucide-react'

/**
 * The fixed vocabulary of category icon keys (catalog-categories spec). It
 * mirrors `Commerce.Domain.Catalog.CategoryIcons` and the database CHECK on
 * `categories.icon_key`; the POS maps the same keys to its own glyphs. This is
 * the one place the web knows the set.
 */
export const CATEGORY_ICON_KEYS = [
  'meat',
  'poultry',
  'fish',
  'wine',
  'drinks',
  'charcoal',
  'grocery',
  'cleaning',
  'bakery',
  'dairy',
  'produce',
  'generic',
] as const

export type CategoryIconKey = (typeof CATEGORY_ICON_KEYS)[number]

export const DEFAULT_CATEGORY_ICON_KEY: CategoryIconKey = 'generic'

const ICONS: Record<CategoryIconKey, LucideIcon> = {
  meat: Beef,
  poultry: Drumstick,
  fish: Fish,
  wine: Wine,
  drinks: CupSoda,
  charcoal: Flame,
  grocery: ShoppingBasket,
  cleaning: SprayCan,
  bakery: Croissant,
  dairy: Milk,
  produce: Apple,
  generic: Tag,
}

/** The icon for a key, falling back to the generic one for a key this build does not know. */
export function categoryIcon(iconKey: string): LucideIcon {
  return ICONS[iconKey as CategoryIconKey] ?? ICONS[DEFAULT_CATEGORY_ICON_KEY]
}

export function isCategoryIconKey(value: string): value is CategoryIconKey {
  return (CATEGORY_ICON_KEYS as readonly string[]).includes(value)
}
