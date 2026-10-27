<!-- SPDX-License-Identifier: AGPL-3.0-or-later -->
<script setup lang="ts">
import { onMounted, ref } from 'vue'
import Button from 'primevue/button'
import Card from 'primevue/card'
import Message from 'primevue/message'
import Tag from 'primevue/tag'
import { api, type IngestKeyInfo } from '@/api'

/** Für Tonwerk-AI und andere Quellen der Ingest-API: Key ansehen, kopieren, neu erzeugen. */
const keys = ref<IngestKeyInfo[]>([])
const error = ref<string | null>(null)
const shown = ref(new Set<string>())
/** Quelle, deren „Neu erzeugen“ auf die Bestätigung wartet. */
const confirming = ref<string | null>(null)
const busy = ref<string | null>(null)
const copied = ref<string | null>(null)

/** Die Adresse, unter der Tonwerk Tonregal erreicht: die, unter der diese Seite offen ist. */
const origin = window.location.origin

onMounted(async () => {
  try {
    keys.value = await api.ingestKeys()
  } catch (e) {
    error.value = e instanceof Error ? e.message : String(e)
  }
})

function toggle(source: string) {
  const next = new Set(shown.value)
  if (!next.delete(source)) next.add(source)
  shown.value = next
}

async function regenerate(source: string) {
  confirming.value = null
  busy.value = source
  error.value = null
  try {
    const fresh = await api.regenerateIngestKey(source)
    keys.value = keys.value.map((k) => (k.source === source ? fresh : k))
    shown.value = new Set(shown.value).add(source)
  } catch (e) {
    error.value = e instanceof Error ? e.message : String(e)
  } finally {
    busy.value = null
  }
}

/** Die zwei Zeilen für ~/.config/tonwerk/tonregal.env auf dem Mac mit Tonwerks Server. */
const envLines = (key: string) => `TONREGAL_URL=${origin}\nTONREGAL_API_KEY=${key}`

/**
 * Tonregal läuft über Tailscale meist per http, wo der Browser navigator.clipboard verweigert;
 * dann über ein verstecktes Textfeld und execCommand.
 */
async function copy(id: string, text: string) {
  try {
    if (navigator.clipboard && window.isSecureContext) {
      await navigator.clipboard.writeText(text)
    } else {
      const area = document.createElement('textarea')
      area.value = text
      area.style.position = 'fixed'
      area.style.opacity = '0'
      document.body.append(area)
      area.select()
      document.execCommand('copy')
      area.remove()
    }
    copied.value = id
    setTimeout(() => copied.value === id && (copied.value = null), 2000)
  } catch {
    error.value = 'Kopieren ging nicht, bitte von Hand markieren.'
  }
}

const masked = (key: string) => '•'.repeat(Math.min(key.length, 24))
</script>

<template>
  <div class="flex flex-col gap-4 max-w-3xl">
    <h1 class="text-2xl font-semibold m-0">Verbindungen</h1>
    <p class="m-0 text-surface-500">
      Mit diesen Keys schicken andere Programme Stücke über die Ingest-API in die Bibliothek. Tonregal hat keine Anmeldung:
      wer diese Seite erreicht, sieht die Keys.
    </p>
    <Message v-if="error" severity="error" :closable="false">{{ error }}</Message>
    <Card v-for="entry in keys" :key="entry.source" :data-testid="`ingest-${entry.source}`">
      <template #title>
        <div class="flex items-center gap-2">
          <span>{{ entry.source }}</span>
          <Tag v-if="entry.fromConfig" severity="secondary" value="aus appsettings" />
        </div>
      </template>
      <template #content>
        <div class="flex flex-col gap-3">
          <div v-if="entry.key" class="flex flex-wrap items-center gap-2">
            <code data-testid="ingest-key" class="px-2 py-1 rounded bg-surface-100 dark:bg-surface-800 break-all">{{
              shown.has(entry.source) ? entry.key : masked(entry.key)
            }}</code>
            <Button
              :icon="shown.has(entry.source) ? 'pi pi-eye-slash' : 'pi pi-eye'"
              text
              rounded
              :aria-label="shown.has(entry.source) ? 'Verbergen' : 'Anzeigen'"
              @click="toggle(entry.source)"
            />
            <Button
              :icon="copied === `key-${entry.source}` ? 'pi pi-check' : 'pi pi-copy'"
              text
              rounded
              aria-label="Key kopieren"
              @click="copy(`key-${entry.source}`, entry.key)"
            />
          </div>
          <p v-else class="m-0 text-surface-500">Noch kein Key, die Ingest-API nimmt von dieser Quelle nichts an.</p>

          <div v-if="entry.key && entry.source === 'tonwerk-ai'" class="flex flex-col gap-1">
            <span class="text-sm text-surface-500">Für <code>~/.config/tonwerk/tonregal.env</code> auf dem Mac, auf dem Tonwerks Server läuft:</span>
            <div class="flex items-start gap-2">
              <pre data-testid="ingest-env" class="m-0 flex-1 px-2 py-1 rounded bg-surface-100 dark:bg-surface-800 text-sm overflow-x-auto">{{
                shown.has(entry.source) ? envLines(entry.key) : envLines(masked(entry.key))
              }}</pre>
              <Button
                :icon="copied === `env-${entry.source}` ? 'pi pi-check' : 'pi pi-copy'"
                text
                rounded
                aria-label="Zeilen kopieren"
                @click="copy(`env-${entry.source}`, envLines(entry.key))"
              />
            </div>
          </div>

          <p v-if="entry.fromConfig" class="m-0 text-sm text-surface-500">
            Dieser Key steht in <code>appsettings.Production.json</code> und lässt sich nur dort ändern.
          </p>
          <div v-else-if="confirming === entry.source" class="flex flex-wrap items-center gap-2">
            <Message severity="warn" :closable="false">Der bisherige Key gilt danach nicht mehr.</Message>
            <Button label="Ja, neu erzeugen" severity="danger" @click="regenerate(entry.source)" />
            <Button label="Abbrechen" text @click="confirming = null" />
          </div>
          <div v-else>
            <Button
              :label="entry.key ? 'Neu erzeugen' : 'Key erzeugen'"
              icon="pi pi-key"
              :loading="busy === entry.source"
              :severity="entry.key ? 'secondary' : undefined"
              @click="entry.key ? (confirming = entry.source) : regenerate(entry.source)"
            />
          </div>
        </div>
      </template>
    </Card>
  </div>
</template>
