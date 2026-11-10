<!-- SPDX-License-Identifier: AGPL-3.0-or-later -->
<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref, watch } from 'vue'
import type { Dancer } from '@/dancer'

/**
 * Die Figur, die im großen Player tanzt, solange Musik läuft. three.js und das Modell kommen erst beim ersten Zeigen;
 * ohne WebGL oder wenn das Modell nicht lädt, bleibt der Platz einfach leer.
 */
const props = defineProps<{ playing: boolean }>()

const canvas = ref<HTMLCanvasElement | null>(null)
const ready = ref(false)
let dancer: Dancer | null = null
let gone = false

onMounted(async () => {
  try {
    const { createDancer } = await import('@/dancer')
    if (gone || !canvas.value) return
    const made = await createDancer(canvas.value, `${import.meta.env.BASE_URL}tanzfigur.glb`)
    if (gone) return made.dispose()
    dancer = made
    dancer.setPlaying(props.playing)
    ready.value = true
  } catch {
    // Kein WebGL (alte Geräte, jsdom) oder Modell nicht da: dann eben keine Figur.
  }
})

watch(
  () => props.playing,
  (playing) => dancer?.setPlaying(playing),
)

onBeforeUnmount(() => {
  gone = true
  dancer?.dispose()
  dancer = null
})
</script>

<template>
  <canvas ref="canvas" class="dancer" :class="{ ready }" aria-hidden="true" data-testid="dancer" />
</template>

<style scoped>
.dancer {
  display: block;
  pointer-events: none;
  opacity: 0;
  transition: opacity 0.6s ease-out;
}

.dancer.ready {
  opacity: 1;
}
</style>
