// SPDX-License-Identifier: AGPL-3.0-or-later
import { defineStore } from 'pinia'
import { ref } from 'vue'
import { HubConnectionBuilder, HubConnectionState, type HubConnection } from '@microsoft/signalr'
import { api, type InboxStatus, type ScanStatus } from '@/api'

/** Live-Verbindung zum StatusHub; Import-, Analyse- und Backup-Events hängen sich hier an. */
export const useStatusStore = defineStore('status', () => {
  const connected = ref(false)
  const scan = ref<ScanStatus | null>(null)
  const inbox = ref<InboxStatus | null>(null)
  let connection: HubConnection | null = null

  async function connect() {
    if (connection) return
    connection = new HubConnectionBuilder().withUrl('/hub').withAutomaticReconnect().build()
    connection.on('scanStatus', (status: ScanStatus) => (scan.value = status))
    connection.on('inboxStatus', (status: InboxStatus) => (inbox.value = status))
    connection.onreconnected(() => (connected.value = true))
    connection.onreconnecting(() => (connected.value = false))
    connection.onclose(() => (connected.value = false))
    try {
      await connection.start()
    } catch {
      // withAutomaticReconnect greift erst nach dem ersten erfolgreichen Start
    }
    connected.value = connection.state === HubConnectionState.Connected
  }

  async function refreshScan() {
    scan.value = await api.scanStatus()
  }

  async function startScan() {
    scan.value = await api.startScan()
  }

  return { connected, scan, inbox, connect, refreshScan, startScan }
})
