// SPDX-License-Identifier: AGPL-3.0-or-later
import { createRouter, createWebHistory } from 'vue-router'
import DashboardView from './views/DashboardView.vue'

/** Einträge der Navigation. */
export const routes = [
  { path: '/', name: 'dashboard', component: DashboardView, meta: { title: 'Übersicht', icon: 'pi pi-home' } },
  { path: '/library', name: 'library', component: () => import('./views/LibraryView.vue'), meta: { title: 'Bibliothek', icon: 'pi pi-book' } },
  { path: '/inbox', name: 'inbox', component: () => import('./views/InboxView.vue'), meta: { title: 'Inbox', icon: 'pi pi-inbox' } },
  {
    path: '/legacy',
    name: 'legacy',
    component: () => import('./views/InboxView.vue'),
    props: { source: 'Legacy' },
    meta: { title: 'Altbestand', icon: 'pi pi-history' },
  },
  { path: '/analysis', name: 'analysis', component: () => import('./views/AnalysisView.vue'), meta: { title: 'Analyse', icon: 'pi pi-sliders-h' } },
  { path: '/duplicates', name: 'duplicates', component: () => import('./views/DuplicatesView.vue'), meta: { title: 'Dubletten', icon: 'pi pi-clone' } },
  { path: '/connections', name: 'connections', component: () => import('./views/ConnectionsView.vue'), meta: { title: 'Verbindungen', icon: 'pi pi-link' } },
]

export const router = createRouter({
  history: createWebHistory(),
  routes: [
    ...routes,
    { path: '/library/artists/:id(\\d+)', name: 'artist', component: () => import('./views/ArtistView.vue'), props: (r) => ({ id: Number(r.params.id) }) },
    { path: '/library/albums/:id(\\d+)', name: 'album', component: () => import('./views/AlbumView.vue'), props: (r) => ({ id: Number(r.params.id) }) },
  ],
})
