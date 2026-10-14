const buttonBase =
  'inline-flex items-center justify-center gap-2 rounded-full px-6 py-3 text-[0.95rem] font-semibold transition-colors'

export const buttonStyles = {
  primary: `${buttonBase} bg-field text-paper hover:bg-pasture`,
  outline: `${buttonBase} border border-field/40 text-field hover:border-field hover:bg-field/5`,
  logo: `${buttonBase} bg-logo text-pasture hover:bg-meadow`,
  outlineOnDark: `${buttonBase} border border-meadow/35 text-meadow hover:border-meadow hover:bg-meadow/10`,
} as const
