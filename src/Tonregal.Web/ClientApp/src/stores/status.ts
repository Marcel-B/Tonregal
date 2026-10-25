// SPDX-License-Identifier: AGPL-3.0-or-later
import { defineStore } from 'pinia'
import { ref } from 'vue'
import { HubConnectionBuilder, HubConnectionState, type HubConnection } from '@microsoft/signalr'

/** Live-Verbindung zum StatusHub; Import-, Analyse- und Backup-Events hängen sich hier an. */
export const useStatusStore = defineStore('status', () => {
  const connected = ref(false)
  let connection: HubConnection | null = null

  async function connect() {
    if (connection) return
    connection = new HubConnectionBuilder().withUrl('/hub').withAutomaticReconnect().build()
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

  return { connected, connect }
})
