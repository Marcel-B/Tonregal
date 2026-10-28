<!-- SPDX-License-Identifier: AGPL-3.0-or-later -->
<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import Button from 'primevue/button'
import Card from 'primevue/card'
import Checkbox from 'primevue/checkbox'
import InputNumber from 'primevue/inputnumber'
import InputText from 'primevue/inputtext'
import Message from 'primevue/message'
import MultiSelect from 'primevue/multiselect'
import Select from 'primevue/select'
import Tag from 'primevue/tag'
import { api, type KeyCount, type SmartPlaylist, type SmartPlaylistInput, type SmartPlaylistSort } from '@/api'
import { allKeys, compatibleKeys } from '@/keys'

/** Regeln für Navidromes Smart Playlists; Tonregal schreibt die .nsp-Dateien, Navidrome füllt die Listen. */
const playlists = ref<SmartPlaylist[]>([])
const libraryKeys = ref<KeyCount[]>([])
const error = ref<string | null>(null)
/** Formular: null = zu, id null = neue Playlist. */
const editing = ref<{ id: number | null; input: SmartPlaylistInput } | null>(null)
const saving = ref(false)
const confirmingDelete = ref<number | null>(null)
const shownNsp = ref<number | null>(null)

const sorts: { value: SmartPlaylistSort; label: string }[] = [
  { value: 'Random', label: 'Zufällig' },
  { value: 'Newest', label: 'Neueste zuerst' },
  { value: 'Bpm', label: 'Nach BPM' },
  { value: 'Title', label: 'Nach Titel' },
  { value: 'Artist', label: 'Nach Künstler' },
]

/** Tonarten der Bibliothek zuerst (mit Anzahl), dann die übrigen der 24. */
const keyOptions = computed(() => {
  const counts = new Map(libraryKeys.value.map((k) => [k.key, k.tracks]))
  const known = libraryKeys.value.map((k) => ({ value: k.key, label: `${k.key} (${k.tracks})` }))
  return [...known, ...allKeys.filter((k) => !counts.has(k)).map((k) => ({ value: k, label: k }))]
})

onMounted(async () => {
  try {
    ;[playlists.value, libraryKeys.value] = await Promise.all([api.smartPlaylists(), api.smartPlaylistKeys()])
  } catch (e) {
    error.value = e instanceof Error ? e.message : String(e)
  }
})

const blank = (): SmartPlaylistInput => ({
  name: '',
  comment: null,
  bpmMin: null,
  bpmMax: null,
  keys: [],
  genre: null,
  addedWithinDays: null,
  neverPlayed: false,
  sort: 'Random',
  limit: null,
})

function edit(playlist: SmartPlaylist | null) {
  error.value = null
  editing.value = playlist
    ? {
        id: playlist.id,
        input: {
          name: playlist.name,
          comment: playlist.comment,
          bpmMin: playlist.bpmMin,
          bpmMax: playlist.bpmMax,
          keys: [...playlist.keys],
          genre: playlist.genre,
          addedWithinDays: playlist.addedWithinDays,
          neverPlayed: playlist.neverPlayed,
          sort: playlist.sort,
          limit: playlist.limit,
        },
      }
    : { id: null, input: blank() }
}

/** Ergänzt die gewählten Tonarten um ihre Nachbarn im Quintenzirkel und die Paralleltonart. */
function addCompatible() {
  if (!editing.value) return
  const keys = editing.value.input.keys
  editing.value.input.keys = [...new Set([...keys, ...keys.flatMap(compatibleKeys)])]
}

async function save() {
  if (!editing.value) return
  saving.value = true
  error.value = null
  try {
    const { id, input } = editing.value
    const saved = id === null ? await api.createSmartPlaylist(input) : await api.updateSmartPlaylist(id, input)
    playlists.value = [...playlists.value.filter((p) => p.id !== saved.id), saved].sort((a, b) => a.name.localeCompare(b.name, 'de'))
    editing.value = null
  } catch (e) {
    error.value = e instanceof Error ? e.message : String(e)
  } finally {
    saving.value = false
  }
}

async function remove(id: number) {
  confirmingDelete.value = null
  error.value = null
  try {
    await api.deleteSmartPlaylist(id)
    playlists.value = playlists.value.filter((p) => p.id !== id)
  } catch (e) {
    error.value = e instanceof Error ? e.message : String(e)
  }
}

/** Die Regeln als kurze Stichworte für die Liste. */
function rules(p: SmartPlaylist): string[] {
  const list: string[] = []
  if (p.bpmMin !== null || p.bpmMax !== null)
    list.push(p.bpmMax === null ? `ab ${p.bpmMin} BPM` : p.bpmMin === null ? `bis ${p.bpmMax} BPM` : `${p.bpmMin}–${p.bpmMax} BPM`)
  if (p.keys.length) list.push(p.keys.join(', '))
  if (p.genre) list.push(`Genre „${p.genre}“`)
  if (p.addedWithinDays !== null) list.push(`neu seit ${p.addedWithinDays} Tagen`)
  if (p.neverPlayed) list.push('nie gehört')
  return list
}

const sortLabel = (sort: SmartPlaylistSort) => sorts.find((s) => s.value === sort)?.label ?? sort
</script>

<template>
  <div class="flex flex-col gap-4 max-w-3xl">
    <div class="flex flex-wrap items-center justify-between gap-2">
      <h1 class="text-2xl font-semibold m-0">Smart Playlists</h1>
      <Button v-if="!editing" label="Neue Smart Playlist" icon="pi pi-plus" @click="edit(null)" />
    </div>
    <p class="m-0 text-surface-500">
      Tonregal schreibt die Regeln als <code>.nsp</code>-Dateien nach <code>Library/Playlists</code>. Navidrome liest sie beim nächsten
      Scan ein und füllt die Listen selbst, auch in Amperfy und anderen Apps. „Nie gehört“ kennt nur Navidrome, die Anzahl hier zählt
      diese Regel nicht mit.
    </p>
    <Message v-if="error" severity="error" :closable="false">{{ error }}</Message>

    <Card v-if="editing" data-testid="editor">
      <template #title>{{ editing.id === null ? 'Neue Smart Playlist' : 'Smart Playlist bearbeiten' }}</template>
      <template #content>
        <form class="flex flex-col gap-3" @submit.prevent="save">
          <label class="flex flex-col gap-1">
            <span class="text-sm">Name</span>
            <InputText v-model="editing.input.name" data-testid="name" required />
          </label>
          <div class="flex flex-wrap gap-3">
            <label class="flex flex-col gap-1">
              <span class="text-sm">BPM von</span>
              <InputNumber v-model="editing.input.bpmMin" :min="1" :max="999" input-class="w-24" />
            </label>
            <label class="flex flex-col gap-1">
              <span class="text-sm">BPM bis</span>
              <InputNumber v-model="editing.input.bpmMax" :min="1" :max="999" input-class="w-24" />
            </label>
            <label class="flex flex-col gap-1">
              <span class="text-sm">Neu seit Tagen</span>
              <InputNumber v-model="editing.input.addedWithinDays" :min="1" :max="3650" input-class="w-24" />
            </label>
          </div>
          <div class="flex flex-col gap-1">
            <span class="text-sm">Tonarten (eine davon)</span>
            <div class="flex flex-wrap items-center gap-2">
              <MultiSelect
                v-model="editing.input.keys"
                :options="keyOptions"
                option-label="label"
                option-value="value"
                display="chip"
                filter
                placeholder="alle"
                class="min-w-64"
              />
              <Button
                label="+ passende"
                text
                :disabled="editing.input.keys.length === 0"
                title="Quinte hoch und runter und die Paralleltonart dazunehmen"
                @click="addCompatible"
              />
            </div>
          </div>
          <label class="flex flex-col gap-1">
            <span class="text-sm">Genre enthält</span>
            <InputText v-model="editing.input.genre" />
          </label>
          <label class="flex items-center gap-2">
            <Checkbox v-model="editing.input.neverPlayed" binary />
            <span>Nur nie gehörte Stücke</span>
          </label>
          <div class="flex flex-wrap gap-3">
            <label class="flex flex-col gap-1">
              <span class="text-sm">Sortierung</span>
              <Select v-model="editing.input.sort" :options="sorts" option-label="label" option-value="value" />
            </label>
            <label class="flex flex-col gap-1">
              <span class="text-sm">Höchstens Stücke</span>
              <InputNumber v-model="editing.input.limit" :min="1" :max="10000" input-class="w-28" />
            </label>
          </div>
          <label class="flex flex-col gap-1">
            <span class="text-sm">Kommentar (zeigt Navidrome an)</span>
            <InputText v-model="editing.input.comment" />
          </label>
          <div class="flex gap-2">
            <Button type="submit" label="Speichern" icon="pi pi-check" :loading="saving" />
            <Button label="Abbrechen" text @click="editing = null" />
          </div>
        </form>
      </template>
    </Card>

    <p v-if="playlists.length === 0 && !error" class="m-0 text-surface-500">Noch keine Smart Playlists.</p>
    <Card v-for="p in playlists" :key="p.id" :data-testid="`playlist-${p.id}`">
      <template #title>
        <div class="flex flex-wrap items-center justify-between gap-2">
          <span>{{ p.name }}</span>
          <span class="text-sm font-normal text-surface-500" data-testid="matching">
            {{ p.neverPlayed ? 'höchstens ' : '' }}{{ p.matching }} Stück{{ p.matching === 1 ? '' : 'e' }}
          </span>
        </div>
      </template>
      <template #content>
        <div class="flex flex-col gap-3">
          <div class="flex flex-wrap gap-2">
            <Tag v-for="rule in rules(p)" :key="rule" :value="rule" severity="secondary" />
            <Tag :value="sortLabel(p.sort) + (p.limit ? `, höchstens ${p.limit}` : '')" severity="contrast" />
          </div>
          <p v-if="p.comment" class="m-0 text-sm text-surface-500">{{ p.comment }}</p>
          <pre
            v-if="shownNsp === p.id"
            class="m-0 px-2 py-1 rounded bg-surface-100 dark:bg-surface-800 text-sm overflow-x-auto"
          >{{ p.path }}

{{ p.nsp }}</pre>
          <div v-if="confirmingDelete === p.id" class="flex flex-wrap items-center gap-2">
            <Message severity="warn" :closable="false">Löscht die Datei und die Playlist in Navidrome.</Message>
            <Button label="Ja, löschen" severity="danger" @click="remove(p.id)" />
            <Button label="Abbrechen" text @click="confirmingDelete = null" />
          </div>
          <div v-else class="flex flex-wrap gap-2">
            <Button label="Bearbeiten" icon="pi pi-pencil" severity="secondary" :disabled="!!editing" @click="edit(p)" />
            <Button
              :label="shownNsp === p.id ? 'Datei verbergen' : 'Datei ansehen'"
              icon="pi pi-file"
              text
              @click="shownNsp = shownNsp === p.id ? null : p.id"
            />
            <Button label="Löschen" icon="pi pi-trash" text severity="danger" @click="confirmingDelete = p.id" />
          </div>
        </div>
      </template>
    </Card>
  </div>
</template>
