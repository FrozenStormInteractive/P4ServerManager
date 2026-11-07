// Types
import type { HealthCheckStatus } from '@/api/types'

const looks: Record<HealthCheckStatus, { color: string, icon: string }> = {
  Running: { color: 'info', icon: 'mdi-progress-clock' },
  Healthy: { color: 'success', icon: 'mdi-check-circle' },
  ProblemsFound: { color: 'error', icon: 'mdi-alert-circle' },
  Failed: { color: 'warning', icon: 'mdi-alert' },
  Canceled: { color: 'grey', icon: 'mdi-cancel' },
  Interrupted: { color: 'grey', icon: 'mdi-power-plug-off' },
}

export function getHealthCheckStatusLook (status: HealthCheckStatus): { color: string, icon: string } {
  return looks[status]
}

/**
 * The time between two dates, in hours, minutes and seconds, leaving out the zero units on the left.
 */
export function formatDuration (start: string, end: string): string {
  const seconds = Math.max(0, Math.round((new Date(end).getTime() - new Date(start).getTime()) / 1000))
  const hours = Math.floor(seconds / 3600)
  const minutes = Math.floor((seconds % 3600) / 60)
  const parts = [hours > 0 ? `${hours}h` : '', hours > 0 || minutes > 0 ? `${minutes}m` : '', `${seconds % 60}s`]
  return parts.filter(Boolean).join(' ')
}
