<!-- SPDX-License-Identifier: AGPL-3.0-or-later -->
<script setup lang="ts">
import { computed, onMounted, watch } from 'vue'
import Button from 'primevue/button'
import { useToast } from 'primevue/usetoast'
import { useStatusStore } from '@/stores/status'

const emit = defineEmits<{ finished: [] }>()
const status = useStatusStore()
const toast = useToast()

onMounted(() => status.refreshScan().catch(() => {}))

const label = computed(() => {
  const scan = status.scan
  if (!scan?.running) return 'Library scannen'
  const p = scan.progress
  return p ? `Scanne … ${p.processed}/${p.total}` : 'Scanne …'
})

watch(
  () => status.scan?.running,
  (running, wasRunning) => {
    if (running || !wasRunning) return
    const scan = status.scan
    if (scan?.lastError) {
      toast.add({ severity: 'error', summary: 'Scan fehlgeschlagen', detail: scan.lastError, life: 8000 })
    } else if (scan?.lastResult) {
      const r = scan.lastResult
      toast.add({
        severity: r.errors.length ? 'warn' : 'success',
        summary: 'Scan fertig',
        detail: `${r.added} neu, ${r.updated} geändert, ${r.removed} entfernt` + (r.errors.length ? `, ${r.errors.length} Fehler` : ''),
        life: 5000,
      })
    }
    emit('finished')
  },
)
</script>

<template>
  <Button :label="label" icon="pi pi-refresh" :loading="status.scan?.running" severity="secondary" size="small" @click="status.startScan()" />
</template>
