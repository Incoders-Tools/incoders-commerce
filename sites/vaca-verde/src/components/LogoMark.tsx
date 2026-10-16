/** Vaca Verde's line-drawn cow head, redrawn as a vector from the brand logo. */
export function LogoMark({ className }: { className?: string }) {
  return (
    <svg
      viewBox="0 0 380 320"
      className={className}
      fill="none"
      stroke="currentColor"
      strokeWidth={18}
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
      focusable="false"
    >
      <path d="M88 15C88 60 110 90 135 98Q190 60 245 98C270 90 292 60 290 15" />
      <path d="M108 98L20 94C24 128 62 158 112 158" />
      <path d="M272 98L360 94C356 128 318 158 268 158" />
      <path d="M108 98L160 242M272 98L220 242" />
      <path d="M160 242H220C245 242 252 270 245 285C238 302 220 308 190 308C160 308 142 302 135 285C128 270 135 242 160 242Z" />
      <path d="M160 242L175 296M220 242L205 296" />
    </svg>
  )
}

/** Mark plus the lowercase "vaca verde" wordmark, as on the shop sign. */
export function Logo({ className }: { className?: string }) {
  return (
    <span className={`inline-flex items-center gap-2.5 ${className ?? ''}`}>
      <LogoMark className="h-8 w-auto" />
      <span className="font-display text-2xl leading-none font-normal tracking-tight whitespace-nowrap">vaca verde</span>
    </span>
  )
}
