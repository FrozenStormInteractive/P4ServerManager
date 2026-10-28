const UNITS = ['B', 'KB', 'MB', 'GB', 'TB']

/**
 * A size in bytes in the largest unit that keeps it above 1, such as `12.4 MB`.
 */
export function formatBytes (bytes: number, locale = 'en'): string {
  let value = bytes
  let unit = 0
  while (value >= 1024 && unit < UNITS.length - 1) {
    value /= 1024
    unit++
  }
  const digits = unit === 0 ? 0 : 1
  const number = value.toLocaleString(locale, { minimumFractionDigits: digits, maximumFractionDigits: digits })
  return `${number} ${UNITS[unit]}`
}
