// SPDX-License-Identifier: AGPL-3.0-or-later
import { createRouter, createWebHistory } from 'vue-router'
import DashboardView from './views/DashboardView.vue'

export const routes = [
  { path: '/', name: 'dashboard', component: DashboardView, meta: { title: 'Übersicht', icon: 'pi pi-home' } },
  { path: '/library', name: 'library', component: () => import('./views/LibraryView.vue'), meta: { title: 'Bibliothek', icon: 'pi pi-book' } },
  { path: '/inbox', name: 'inbox', component: () => import('./views/InboxView.vue'), meta: { title: 'Inbox', icon: 'pi pi-inbox' } },
  { path: '/duplicates', name: 'duplicates', component: () => import('./views/DuplicatesView.vue'), meta: { title: 'Dubletten', icon: 'pi pi-clone' } },
]

export const router = createRouter({
  history: createWebHistory(),
  routes,
})
