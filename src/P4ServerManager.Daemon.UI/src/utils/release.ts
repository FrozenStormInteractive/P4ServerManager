const RELEASE_PATTERN = /^r(\d{2})\.(\d)$/

/**
 * Orders two p4d releases as named on the Perforce FTP, such as `r25.1`: negative when `a` is older than `b`. A name
 * that is not a release sorts first.
 */
export function compareReleases (a: string, b: string): number {
  return toNumber(a) - toNumber(b)
}

function toNumber (release: string): number {
  const match = RELEASE_PATTERN.exec(release)
  return match ? Number(match[1]) * 10 + Number(match[2]) : -1
}
