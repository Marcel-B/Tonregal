<!-- SPDX-License-Identifier: AGPL-3.0-or-later -->
<script setup lang="ts">
import { computed, ref } from 'vue'
import Button from 'primevue/button'
import Checkbox from 'primevue/checkbox'
import Dialog from 'primevue/dialog'
import Message from 'primevue/message'
import { api, type AcceptReviewResponse, type ImportJob } from '@/api'
import { reviewCategories, type ReviewCategory } from '@/reviewCategories'

/**
 * „Alle prüfen übernehmen“: bündelt die zu prüfenden Dateien nach Grund, lässt Gründe abwählen und
 * stellt nach Bestätigung genau die gewählten an. Mit Vorschlag wie „Vorschlag übernehmen“, ohne
 * Vorschlag (abwählbar) wie „Ohne Treffer übernehmen“. Der Server prüft jede Datei dabei erneut.
 */
const props = defineProps<{ jobs: ImportJob[]; disabled?: boolean }>()
const emit = defineEmits<{ done: [result: AcceptReviewResponse] }>()

const visible = ref(false)
const excluded = ref<string[]>([])
const withoutSuggestion = ref(true)
const result = ref<AcceptReviewResponse | null>(null)
const error = ref<string | null>(null)
const sending = ref(false)

const categories = computed(() => reviewCategories(props.jobs))
const total = computed(() => categories.value.reduce((n, c) => n + c.withSuggestion.length + c.withoutSuggestion.length, 0))
const chosen = computed(() => categories.value.filter((c) => !excluded.value.includes(c.key)))
const suggested = computed(() => chosen.value.flatMap((c) => c.withSuggestion))
const unsuggested = computed(() => (withoutSuggestion.value ? chosen.value.flatMap((c) => c.withoutSuggestion) : []))
const count = computed(() => suggested.value.length + unsuggested.value.length)
const anyWithout = computed(() => categories.value.some((c) => c.withoutSuggestion.length))

const counts = (c: ReviewCategory) =>
  `${c.withSuggestion.length} mit Vorschlag` + (c.withoutSuggestion.length ? ` · ${c.withoutSuggestion.length} ohne` : '')
const resultText = computed(() => {
  const r = result.value
  if (!r) return ''
  let text = `${r.accepted + r.unmatched} Datei(en) werden übernommen`
  if (r.unmatched) text += ` (davon ${r.unmatched} mit ihren Tags)`
  text += '. Der Fortschritt steht oben auf der Seite.'
  if (r.skipped) text += ` ${r.skipped} Datei(en) waren nicht mehr zu prüfen und bleiben, wie sie sind.`
  return text
})

function open() {
  visible.value = true
  result.value = null
  error.value = null
}

function toggle(key: string, on: boolean) {
  excluded.value = on ? excluded.value.filter((k) => k !== key) : [...excluded.value, key]
}

async function confirm() {
  if (!count.value) return
  sending.value = true
  error.value = null
  try {
    const ids = [...suggested.value, ...unsuggested.value].map((j) => j.id)
    result.value = await api.acceptReview(ids, withoutSuggestion.value)
    emit('done', result.value)
  } catch (e) {
    error.value = e instanceof Error ? e.message : String(e)
  } finally {
    sending.value = false
  }
}
</script>

<template>
  <Button
    v-if="total"
    :label="`Alle zu prüfenden übernehmen (${total})`"
    icon="pi pi-check-square"
    severity="secondary"
    size="small"
    :disabled="disabled"
    data-testid="accept-review"
    @click="open"
  />
  <Dialog v-model:visible="visible" modal header="Zu prüfende Dateien übernehmen" class="w-full max-w-2xl" :dismissable-mask="!sending">
    <Message v-if="error" severity="error" class="mb-4">{{ error }}</Message>

    <Message v-if="result" severity="success" class="mb-4" data-testid="accept-result">
      {{ resultText }}
    </Message>

    <template v-else>
      <p class="mb-3 text-sm text-surface-500">
        Dateien mit Vorschlag gehen mit dem vorgeschlagenen Treffer in die Library, wie mit „Vorschlag übernehmen“. Wähl die
        Gründe ab, bei denen du lieber selbst hinschaust.
      </p>
      <div class="rounded-md border border-surface-200 dark:border-surface-700 text-sm" data-testid="accept-categories">
        <label
          v-for="category in categories"
          :key="category.key"
          class="flex items-center gap-3 px-3 py-2 border-b border-surface-100 dark:border-surface-800 last:border-0 cursor-pointer"
          :data-testid="`accept-category-${category.key}`"
        >
          <Checkbox
            :model-value="!excluded.includes(category.key)"
            binary
            :input-id="`accept-${category.key}`"
            @update:model-value="(on: boolean) => toggle(category.key, on)"
          />
          <span class="font-medium">{{ category.label }}</span>
          <span class="ml-auto text-surface-500 whitespace-nowrap">{{ counts(category) }}</span>
        </label>
      </div>
      <label v-if="anyWithout" class="mt-4 flex items-start gap-3 text-sm cursor-pointer" data-testid="accept-without">
        <Checkbox v-model="withoutSuggestion" binary input-id="accept-without-suggestion" />
        <span>
          Auch Dateien ohne Vorschlag übernehmen, mit Titel, Interpret und Album aus ihren Tags (ohne Album ins Sammelalbum),
          wie mit „Ohne Treffer übernehmen“.
        </span>
      </label>
      <p class="mt-4" data-testid="accept-summary">
        <strong>{{ count }} von {{ total }} Datei(en)</strong> werden übernommen{{
          unsuggested.length ? `, davon ${unsuggested.length} mit ihren Tags` : ''
        }}.
      </p>
    </template>

    <template #footer>
      <Button :label="result ? 'Schließen' : 'Abbrechen'" severity="secondary" size="small" :disabled="sending" @click="visible = false" />
      <Button
        v-if="!result"
        :label="`${count} Datei(en) übernehmen`"
        icon="pi pi-check"
        size="small"
        :disabled="!count"
        :loading="sending"
        data-testid="accept-confirm"
        @click="confirm"
      />
    </template>
  </Dialog>
</template>
