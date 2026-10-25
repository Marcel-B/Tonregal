// SPDX-License-Identifier: AGPL-3.0-or-later
import { createApp } from 'vue'
import { createPinia } from 'pinia'
import PrimeVue from 'primevue/config'
import ToastService from 'primevue/toastservice'
import Aura from '@primeuix/themes/aura'
import App from './App.vue'
import { router } from './router'
import './style.css'

if (window.matchMedia('(prefers-color-scheme: dark)').matches) {
  document.documentElement.classList.add('dark')
}

createApp(App)
  .use(createPinia())
  .use(router)
  .use(PrimeVue, { theme: { preset: Aura, options: { darkModeSelector: '.dark' } } })
  .use(ToastService)
  .mount('#app')
