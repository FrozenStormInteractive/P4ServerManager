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
    name: 'instance',
    component: () => import('@/pages/InstanceDetailsPage.vue'),
    props: true,
  },
  {
    path: '/settings',
    name: 'settings',
    component: () => import('@/pages/SettingsPage.vue'),
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
