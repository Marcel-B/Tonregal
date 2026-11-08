<!-- SPDX-License-Identifier: AGPL-3.0-or-later -->
<script setup lang="ts">
import { computed, ref } from 'vue'
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import InputText from 'primevue/inputtext'
import Menu from 'primevue/menu'
import Message from 'primevue/message'
import { useToast } from 'primevue/usetoast'
import type { MenuItem } from 'primevue/menuitem'
import { usePlaylistsStore } from '@/stores/playlists'

/**
 * Knopf mit Menü „Zur Playlist“: eine vorhandene wählen oder gleich eine neue anlegen. Ohne `label` ein runder
 * Symbolknopf, mit `plain` ein Textknopf wie im großen Player.
 */
const props = withDefaults(
  defineProps<{ trackIds: number[]; label?: string; title?: string; disabled?: boolean; size?: 'small' | 'large'; plain?: boolean }>(),
  { label: undefined, title: 'Zur Playlist hinzufügen', disabled: false, size: 'small', plain: false },
)

const playlists = usePlaylistsStore()
const menu = ref<InstanceType<typeof Menu> | null>(null)
const error = ref<string | null>(null)
const naming = ref(false)
const name = ref('')
const busy = ref(false)

// In Tests und Seiten ohne ToastService bleibt nur die Meldung am Knopf.
const toast = (() => {
  try {
    return useToast()
  } catch {
    return null
  }
})()

const unique = computed(() => [...new Set(props.trackIds)])

const items = computed<MenuItem[]>(() => [
  ...playlists.list.map((p) => ({ label: p.name, icon: 'pi pi-list', command: () => add(p.id, p.name) })),
  ...(playlists.list.length ? [{ separator: true }] : []),
  { label: 'Neue Playlist …', icon: 'pi pi-plus', command: () => ((naming.value = true), (name.value = '')) },
])

async function open(event: Event) {
  error.value = null
  menu.value?.toggle(event)
  if (!playlists.loaded) {
    try {
      await playlists.load()
    } catch (e) {
      error.value = e instanceof Error ? e.message : String(e)
    }
  }
}

function count(n: number) {
  return n === 1 ? '1 Stück' : `${n} Stücke`
}

async function add(id: number, playlistName: string) {
  busy.value = true
  error.value = null
  try {
    const { added, skipped } = await playlists.add(id, unique.value)
    const detail =
      added === 0 ? `Stand schon in „${playlistName}“.` : `${count(added)} zu „${playlistName}“ hinzugefügt${skipped ? `, ${skipped} waren schon darin` : ''}.`
    toast?.add({ severity: added ? 'success' : 'info', summary: 'Playlist', detail, life: 3000 })
  } catch (e) {
    error.value = e instanceof Error ? e.message : String(e)
  } finally {
    busy.value = false
  }
}

async function createAndAdd() {
  busy.value = true
  error.value = null
  try {
    const created = await playlists.create(name.value)
    naming.value = false
    await add(created.id, created.name)
  } catch (e) {
    error.value = e instanceof Error ? e.message : String(e)
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <span class="inline-flex items-center gap-1">
    <Button
      icon="pi pi-bookmark"
      :label="label"
      :text="!label || plain"
      :rounded="!label"
      :severity="label && !plain ? 'secondary' : undefined"
      :size="size"
      :aria-label="title"
      :title="title"
      :disabled="disabled || !unique.length"
      :loading="busy"
      aria-haspopup="true"
      data-testid="add-to-playlist"
      @click="open"
    />
    <span v-if="error && !naming" class="text-sm text-red-500" data-testid="add-to-playlist-error">{{ error }}</span>
    <Menu ref="menu" :model="items" popup data-testid="add-to-playlist-menu" />
    <Dialog v-model:visible="naming" header="Neue Playlist" modal :style="{ width: '22rem' }">
      <form class="flex flex-col gap-3" @submit.prevent="createAndAdd">
        <InputText v-model="name" placeholder="Name" autofocus required maxlength="100" data-testid="new-playlist-name" />
        <Message v-if="error" severity="error" :closable="false">{{ error }}</Message>
        <div class="flex justify-end gap-2">
          <Button label="Abbrechen" text @click="naming = false" />
          <Button type="submit" label="Anlegen" :loading="busy" :disabled="!name.trim()" />
        </div>
      </form>
    </Dialog>
  </span>
</template>
