<!-- SPDX-License-Identifier: AGPL-3.0-or-later -->
<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue'
import Button from 'primevue/button'
import Message from 'primevue/message'
import ProgressBar from 'primevue/progressbar'
import { api } from '@/api'
import { plan, uploadFolder, type UploadItem } from '@/upload'

const emit = defineEmits<{ uploaded: [] }>()

const dragging = ref(false)
const queue = ref<UploadItem[]>([])
const done = ref(0)
const failed = ref<string[]>([])
const skipped = ref(0)
const running = computed(() => done.value < queue.value.length)
const percent = computed(() => (queue.value.length ? Math.round((done.value / queue.value.length) * 100) : 0))

async function start(items: UploadItem[]) {
  const planned = plan(items, uploadFolder())
  const busy = running.value
  if (!busy) {
    queue.value = []
    done.value = 0
    failed.value = []
    skipped.value = 0
  }
  skipped.value += planned.skipped
  queue.value.push(...planned.items)
  if (busy) return // die laufende Schleife nimmt die neuen Dateien mit

  // Nacheinander, damit die SSD nicht mit vielen parallelen Schreibvorgängen kämpft.
  while (done.value < queue.value.length) {
    const item = queue.value[done.value]!
    try {
      await api.upload(item.file, item.path)
    } catch (e) {
      failed.value.push(`${item.path}: ${e instanceof Error ? e.message : String(e)}`)
    }
    done.value++
  }
  emit('uploaded')
}

async function readEntries(reader: FileSystemDirectoryReader): Promise<FileSystemEntry[]> {
  const all: FileSystemEntry[] = []
  // readEntries liefert große Ordner in Portionen, bis eine leere kommt.
  for (;;) {
    const batch = await new Promise<FileSystemEntry[]>((resolve, reject) => reader.readEntries(resolve, reject))
    if (!batch.length) return all
    all.push(...batch)
  }
}

async function collect(entry: FileSystemEntry): Promise<UploadItem[]> {
  if (entry.isFile) {
    const file = await new Promise<File>((resolve, reject) => (entry as FileSystemFileEntry).file(resolve, reject))
    return [{ file, path: entry.fullPath.replace(/^\//, '') }]
  }
  if (entry.isDirectory) {
    const children = await readEntries((entry as FileSystemDirectoryEntry).createReader())
    return (await Promise.all(children.map(collect))).flat()
  }
  return []
}

async function onDrop(event: DragEvent) {
  event.preventDefault()
  dragging.value = false
  const transfer = event.dataTransfer
  if (!transfer) return
  const entries = [...transfer.items].map((i) => i.webkitGetAsEntry?.()).filter((e): e is FileSystemEntry => !!e)
  const items = entries.length
    ? (await Promise.all(entries.map(collect))).flat()
    : [...transfer.files].map((file) => ({ file, path: file.name }))
  await start(items)
}

function onPick(event: Event) {
  const input = event.target as HTMLInputElement
  const items = [...(input.files ?? [])].map((file) => ({ file, path: file.webkitRelativePath || file.name }))
  input.value = ''
  void start(items)
}

// Drag & Drop auf die ganze Seite, solange die Inbox offen ist.
let depth = 0
const hasFiles = (e: DragEvent) => !!e.dataTransfer?.types.includes('Files')
function onDragEnter(e: DragEvent) {
  if (!hasFiles(e)) return
  depth++
  dragging.value = true
}
function onDragLeave(e: DragEvent) {
  if (!hasFiles(e)) return
  depth = Math.max(0, depth - 1)
  if (depth === 0) dragging.value = false
}
function onDragOver(e: DragEvent) {
  if (hasFiles(e)) e.preventDefault()
}
function onWindowDrop(e: DragEvent) {
  depth = 0
  if (hasFiles(e)) void onDrop(e)
}
onMounted(() => {
  window.addEventListener('dragenter', onDragEnter)
  window.addEventListener('dragleave', onDragLeave)
  window.addEventListener('dragover', onDragOver)
  window.addEventListener('drop', onWindowDrop)
})
onUnmounted(() => {
  window.removeEventListener('dragenter', onDragEnter)
  window.removeEventListener('dragleave', onDragLeave)
  window.removeEventListener('dragover', onDragOver)
  window.removeEventListener('drop', onWindowDrop)
})

const files = ref<HTMLInputElement>()
const folder = ref<HTMLInputElement>()
</script>

<template>
  <div class="flex items-center gap-2">
    <Button label="Dateien" icon="pi pi-upload" severity="secondary" size="small" :disabled="running" @click="files?.click()" />
    <Button label="Ordner" icon="pi pi-folder-open" severity="secondary" size="small" :disabled="running" @click="folder?.click()" />
    <input ref="files" type="file" multiple accept="audio/*,.mp3,.flac,.m4a,.aac,.ogg,.opus,.wav" class="hidden" data-testid="upload-files" @change="onPick" />
    <input ref="folder" type="file" webkitdirectory class="hidden" @change="onPick" />
  </div>

  <Teleport to="body">
    <div
      v-if="dragging"
      class="fixed inset-0 z-50 flex items-center justify-center bg-surface-950/60 backdrop-blur-sm pointer-events-none"
      data-testid="drop-overlay"
    >
      <div class="rounded-xl border-2 border-dashed border-primary-400 bg-surface-0 dark:bg-surface-900 px-10 py-8 text-center">
        <i class="pi pi-upload text-3xl text-primary-500 mb-3" />
        <div class="text-lg font-semibold">In die Inbox legen</div>
        <div class="text-sm text-surface-500">Ordner bleiben als Album zusammen</div>
      </div>
    </div>
  </Teleport>

  <div v-if="queue.length" class="basis-full" data-testid="upload-status">
    <div class="flex items-center gap-3 text-sm text-surface-500 mb-1">
      <span v-if="running">Lade hoch … {{ done }} von {{ queue.length }}</span>
      <span v-else>{{ queue.length - failed.length }} Datei(en) hochgeladen<template v-if="skipped">, {{ skipped }} übersprungen (kein Audio)</template></span>
    </div>
    <ProgressBar v-if="running" :value="percent" :show-value="false" class="h-1.5" />
    <Message v-for="f in failed" :key="f" severity="error" size="small" class="mt-2">{{ f }}</Message>
  </div>
</template>
