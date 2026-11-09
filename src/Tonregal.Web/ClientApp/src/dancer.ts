// SPDX-License-Identifier: AGPL-3.0-or-later
import {
  AnimationMixer,
  Box3,
  DirectionalLight,
  HemisphereLight,
  LoopOnce,
  Mesh,
  PerspectiveCamera,
  Scene,
  Texture,
  Vector3,
  WebGLRenderer,
  type AnimationAction,
  type Material,
} from 'three'
import { GLTFLoader } from 'three/examples/jsm/loaders/GLTFLoader.js'
import { MeshoptDecoder } from 'three/examples/jsm/libs/meshopt_decoder.module.js'

/**
 * Die tanzende Figur im großen Player: ein animiertes glTF (aus img-o-mat, mit gltf-transform auf Meshopt und kleine
 * WebP-Texturen verkleinert), das eine Bewegung nach der anderen tanzt. Eigenes Modul, damit three.js nur geladen
 * wird, wenn jemand die Figur sieht.
 */
export interface Dancer {
  /** Läuft Musik, tanzt sie; sonst bremst sie ab und bleibt stehen, und es wird nichts mehr gezeichnet. */
  setPlaying(playing: boolean): void
  dispose(): void
}

/** Bewegungen, die nicht zum Tanzen gehören. */
const notDancing = new Set(['Umfallen'])

/** Die nächste Bewegung: zufällig, aber nie zweimal dieselbe hintereinander. */
export function nextMove(names: readonly string[], last: string | null, random = Math.random): string | null {
  const choices = names.filter((n) => n !== last)
  if (choices.length === 0) return names[0] ?? null
  return choices[Math.floor(random() * choices.length)]!
}

export async function createDancer(canvas: HTMLCanvasElement, url: string): Promise<Dancer> {
  const renderer = new WebGLRenderer({ canvas, alpha: true, antialias: true, powerPreference: 'low-power' })
  renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, 2))

  const loader = new GLTFLoader().setMeshoptDecoder(MeshoptDecoder)
  let gltf
  try {
    gltf = await loader.loadAsync(url)
  } catch (error) {
    renderer.dispose()
    throw error
  }
  const figure = gltf.scene

  const scene = new Scene()
  scene.add(new HemisphereLight(0xffffff, 0x444466, 2.2))
  const sun = new DirectionalLight(0xffffff, 2)
  sun.position.set(1, 2, 3)
  scene.add(sun)
  scene.add(figure)

  const box = new Box3().setFromObject(figure)
  const size = box.getSize(new Vector3())
  const centre = box.getCenter(new Vector3())
  const camera = new PerspectiveCamera(30, 1, 0.01, 100)

  /**
   * Kamera so weit weg, dass die Figur mit erhobenen Armen und Sprüngen hineinpasst, und bei schmalem Rahmen auch mit
   * ausgebreiteten Armen, die etwa so weit reichen, wie sie groß ist.
   */
  function frameFigure() {
    const height = Math.max(size.y * 1.3, (size.y * 1.05) / camera.aspect)
    const distance = height / 2 / Math.tan((camera.fov * Math.PI) / 360)
    camera.position.set(centre.x, centre.y, centre.z + distance)
    camera.lookAt(centre)
    camera.updateProjectionMatrix()
  }
  frameFigure()

  const mixer = new AnimationMixer(figure)
  const actions = new Map<string, AnimationAction>()
  for (const clip of gltf.animations) {
    if (notDancing.has(clip.name)) continue
    const action = mixer.clipAction(clip)
    action.setLoop(LoopOnce, 1)
    action.clampWhenFinished = true
    actions.set(clip.name, action)
  }
  const names = [...actions.keys()]

  let current: AnimationAction | null = null
  let currentName: string | null = null
  function dance() {
    const name = nextMove(names, currentName)
    if (!name) return
    const action = actions.get(name)!
    action.reset().play()
    if (current && current !== action) action.crossFadeFrom(current, 0.3, false)
    current = action
    currentName = name
  }
  mixer.addEventListener('finished', dance)
  dance()

  function resize() {
    const width = canvas.clientWidth
    const height = canvas.clientHeight
    if (!width || !height) return
    renderer.setSize(width, height, false)
    camera.aspect = width / height
    frameFigure()
  }
  const observer = new ResizeObserver(() => {
    resize()
    if (!frame) renderer.render(scene, camera)
  })
  observer.observe(canvas)
  resize()

  // Abbremsen statt hart anhalten; steht sie, hört die Schleife auf, und ein pausiertes Stück kostet keinen Akku.
  let playing = false
  let frame = 0
  let last = 0
  mixer.timeScale = 0
  mixer.update(0)
  renderer.render(scene, camera)

  function tick(now: number) {
    const dt = last ? Math.min((now - last) / 1000, 0.1) : 0
    last = now
    const target = playing ? 1 : 0
    mixer.timeScale += (target - mixer.timeScale) * Math.min(1, dt * 5)
    if (!playing && mixer.timeScale < 0.01) mixer.timeScale = 0
    mixer.update(dt)
    renderer.render(scene, camera)
    frame = playing || mixer.timeScale > 0 ? requestAnimationFrame(tick) : 0
  }

  return {
    setPlaying(on: boolean) {
      playing = on
      if (!frame) {
        last = 0
        frame = requestAnimationFrame(tick)
      }
    },
    dispose() {
      cancelAnimationFrame(frame)
      frame = 0
      observer.disconnect()
      mixer.stopAllAction()
      figure.traverse((node) => {
        if (!(node instanceof Mesh)) return
        node.geometry.dispose()
        for (const material of ([] as Material[]).concat(node.material)) {
          for (const value of Object.values(material)) if (value instanceof Texture) value.dispose()
          material.dispose()
        }
      })
      renderer.dispose()
      renderer.forceContextLoss()
    },
  }
}
