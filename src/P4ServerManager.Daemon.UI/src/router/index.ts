/**
 * router/index.ts
 *
 * Manual routes for ./src/pages/*.vue
 */

// Types
import type { RouteRecordRaw } from 'vue-router'
// Composables
import { createRouter, createWebHistory } from 'vue-router'

import DashboardPage from '@/pages/DashboardPage.vue'

export const routes: RouteRecordRaw[] = [
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

export default router
