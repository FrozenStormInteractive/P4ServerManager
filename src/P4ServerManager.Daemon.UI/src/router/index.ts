/**
 * router/index.ts
 *
 * Manual routes for ./src/pages/*.vue
 */

// Types
import type { RouteLocationNormalized, RouteLocationRaw, RouteRecordRaw } from 'vue-router'
// Composables
import { createRouter, createWebHistory } from 'vue-router'

import { setUnauthorizedHandler } from '@/api/client'
import DashboardPage from '@/pages/DashboardPage.vue'
import { useAuthStore } from '@/stores/auth'

declare module 'vue-router' {
  interface RouteMeta {
    /**
     * The page needs no signed-in user.
     */
    public?: boolean
  }
}

export const routes: RouteRecordRaw[] = [
  {
    path: '/login',
    name: 'login',
    component: () => import('@/pages/LoginPage.vue'),
    meta: { public: true },
  },
  {
    path: '/setup',
    name: 'setup',
    component: () => import('@/pages/SetupPage.vue'),
    meta: { public: true },
  },
  {
    path: '/',
    name: 'dashboard',
    component: DashboardPage,
  },
  {
    path: '/instances',
    name: 'instances',
    component: () => import('@/pages/InstanceListPage.vue'),
  },
  {
    path: '/instances/:id',
    component: () => import('@/pages/InstancePage.vue'),
    props: true,
    // An instance has one page per section, listed by the instance page.
    children: [
      {
        path: '',
        name: 'instance',
        component: () => import('@/pages/InstanceDetailsPage.vue'),
        props: true,
      },
      {
        path: 'health',
        name: 'instance-health',
        component: () => import('@/pages/InstanceHealthPage.vue'),
        props: true,
      },
      {
        path: 'backups',
        name: 'instance-backups',
        component: () => import('@/pages/InstanceBackupsPage.vue'),
        props: true,
      },
    ],
  },
  {
    path: '/settings',
    name: 'settings',
    component: () => import('@/pages/SettingsPage.vue'),
    // The settings have one page per section, listed by the settings page.
    redirect: { name: 'settings-builds' },
    children: [
      {
        path: 'builds',
        name: 'settings-builds',
        component: () => import('@/pages/settings/SettingsBuildsPage.vue'),
      },
      {
        path: 'backups',
        name: 'settings-backups',
        component: () => import('@/pages/settings/SettingsBackupsPage.vue'),
      },
      {
        path: 'users',
        name: 'settings-users',
        component: () => import('@/pages/settings/SettingsUsersPage.vue'),
      },
    ],
  },
  {
    path: '/:path(.*)*',
    name: 'not-found',
    component: () => import('@/pages/NotFoundPage.vue'),
  },
]

const router = createRouter({
  history: createWebHistory(import.meta.env.BASE_URL),
  routes,
})

/**
 * Sends to the setup page while the daemon has no account, to the sign-in page without a session, and lets the
 * navigation go on otherwise.
 */
export async function authGuard (to: RouteLocationNormalized): Promise<RouteLocationRaw | true> {
  const auth = useAuthStore()
  let setupRequired = false
  let signedIn = false
  try {
    const session = await auth.ensureSession()
    setupRequired = session.setupRequired
    signedIn = session.user !== null
  } catch {
    // The daemon cannot be reached: the sign-in page tells it when the user tries.
  }

  if (setupRequired) {
    return to.name === 'setup' ? true : { name: 'setup' }
  }
  if (!signedIn) {
    if (to.name === 'login') {
      return true
    }
    return { name: 'login', query: to.meta.public || to.fullPath === '/' ? {} : { redirect: to.fullPath } }
  }
  return to.meta.public ? { name: 'dashboard' } : true
}

router.beforeEach(authGuard)

// A session that ends while the app is open sends the user to the sign-in page, back to the same page afterwards.
setUnauthorizedHandler(() => {
  useAuthStore().sessionEnded()
  const current = router.currentRoute.value
  if (!current.meta.public) {
    void router.push({ name: 'login', query: { redirect: current.fullPath } })
  }
})

export default router
