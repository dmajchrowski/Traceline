// Types mirror the JSON the server sends; see src/Traceline.App/Program.cs.

export interface Target {
  id: number; input: string; name: string; kind: 'Icmp' | 'Tcp' | 'Http' | 'Dns'; host: string; address: string
  intervalMs: number; finalHopOnly: boolean; paused: boolean; followDns: boolean
  configId: string; workspaceId: string; closed: boolean; keep: boolean
}
export interface TraceConfig {
  id: string; name: string; description: string
  packetType: 'Icmp' | 'Udp' | 'Tcp' | 'Agent'; tcpPort: number; timeoutMs: number; packetSize: number; sendDelayMs: number; maxHops: number
  interface: string; startFinalHopOnly: boolean; allowFragment: boolean; removeSilentHops: boolean; silentMinutes: number; silentSamples: number
  agentUrl: string; agentKey: string; agentPacketType: 'Icmp' | 'Udp' | 'Tcp'
  warnMs: number; badMs: number; scaleMode: 'each' | 'destination' | 'fixed'; scaleMs: number; lossScalePct: number
  showMinMax: boolean; showJitter: boolean; jitterScaleMs: number; jitterTargetMs: number
}
export interface Summary {
  target: Target; error: string | null; lastTs: number | null; hops: number
  cur: number | null; avg: number | null; lossPct: number | null; jitter: number | null; mos: number | null; count: number; alerting: boolean
  warnMs: number; badMs: number; configName: string
}
export interface HopStat {
  hop: number; ip: string | null; name: string | null; asn: string | null; owner: string | null; count: number; lost: number
  avg: number | null; min: number | null; max: number | null; cur: number | null; jitter: number | null; lossPct: number | null; mos: number | null
  dnsName: string | null; notes: string | null
}
export interface Timeline {
  hop: number; from: number; bucketMs: number
  min: (number | null)[]; avg: (number | null)[]; max: (number | null)[]; loss: (number | null)[]; jitter: (number | null)[]
}
export interface TimelineEvent { id: number; targetId: number; ts: number; kind: 'comment' | 'route' | 'ip' | 'alert'; text: string }
export interface Diagnosis { severity: 'ok' | 'warn' | 'bad' | 'info'; headline: string; details: string[] }
export interface TargetView {
  from: number; to: number; focusFrom: number; focusTo: number; coarse: boolean
  hops: HopStat[]; timelines: Timeline[]; events: TimelineEvent[]; diagnosis: Diagnosis; config: TraceConfig
}
export interface Settings {
  rawDays: number; rollupDays: number; ownerLookup: boolean; reverseDns: boolean; listenOnNetwork: boolean; port: number; siteName: string
  autoSaveMode: 'always' | 'after' | 'selected'; autoSaveMinutes: number; allowRunProgram: boolean; commentOnIpChange: boolean; agentKey: string
  keepAwake: boolean; httpsEnabled: boolean; httpsPort: number; httpsOnly: boolean; certificatePath: string; certificatePassword: string
}
export interface Workspace { id: string; name: string }
export interface AppState {
  version: string; now: number; machine: string; needsLogin: boolean; readOnly: boolean; shareInvalid: boolean; shareName: string | null
  role: 'admin' | 'editor' | 'viewer' | null; user: string | null; hasUsers: boolean; listeningOnNetwork: boolean; imageSupported: boolean
  settings: Settings; configs: TraceConfig[]; workspaces: Workspace[]; activeWorkspace: string; dataFolder: string | null; vendorList: boolean
  https: { active: boolean; port: number; error: string | null; selfMade: boolean; expires: number | null; passwordSet: boolean }
}
export interface AlertRule {
  id: string; name: string; enabled: boolean; kind: 'OverTime' | 'OverSamples' | 'MosBelow' | 'RouteChanged' | 'IpInRoute' | 'Timer'
  watch: string; latencyMs: number; windowSeconds: number; percent: number; sampleCount: number; badCount: number; mos: number; ip: string; timerMinutes: number
  targetIds: number[]; notifyOnStart: boolean; notifyOnEnd: boolean; repeatMinutes: number; eachTime: boolean; eachTimeNotMet: boolean
  channelIds: string[]; addComment: boolean; logFile: string; message: string; subject: string; minMinutesBetween: number; attachImage: boolean; attachData: boolean
  saveImageFolder: string; exportDataFolder: string; runProgram: string; runArguments: string
  summaryAction: '' | 'add' | 'remove'; summaryId: string; routeAction: '' | 'full' | 'final'; sound: string
}
export interface Channel { type: string; id: string; name: string; enabled: boolean; [key: string]: unknown }
export interface AlertsInfo {
  rules: { rule: AlertRule; activeOn: number[] }[]
  deliveries: { ts: number; channel: string; ok: boolean; text: string }[]
  recent: TimelineEvent[]
  variables: string[]
}
export interface ShareLink { token: string; name: string; targetIds: number[]; expiresMs: number | null; createdMs: number }
export interface SessionInfo { target: Target; state: string; workspace: string | null; first: number | null; last: number | null; samples: number }
export interface SummaryDef { id: string; name: string; workspaceId: string; items: { targetId: number; hop: number }[] }
export interface Device { ip: string; mac: string; vendor: string; name: string; pingMs: number | null; methods: string[]; description: string }

/** The share token from the address bar, when this page was opened through a share link. */
export const shareToken = new URLSearchParams(location.search).get('share')

export async function api<T = unknown>(path: string, method = 'GET', body?: unknown): Promise<T> {
  const headers: Record<string, string> = {}
  const raw = body instanceof Blob || body instanceof ArrayBuffer
  if (body !== undefined) headers['Content-Type'] = raw ? 'application/octet-stream' : 'application/json'
  if (shareToken) headers['X-Share'] = shareToken
  const response = await fetch('/api' + path, { method, headers, body: body === undefined ? undefined : raw ? (body as Blob) : JSON.stringify(body) })
  if (response.status === 401 && path !== '/login') { window.dispatchEvent(new Event('traceline-login')); throw new Error('Please sign in.') }
  const text = await response.text()
  let data: { error?: string } | null = null
  try { data = text ? JSON.parse(text) : null } catch { /* not JSON */ }
  if (!response.ok) throw new Error(data?.error ?? `The server answered ${response.status}.`)
  return data as T
}

/** Subscribes to live updates; returns a function that stops them. */
export function listen(onMessage: (m: { type: string; targetId?: number; [k: string]: unknown }) => void): () => void {
  const source = new EventSource('/api/stream' + (shareToken ? `?share=${shareToken}` : ''))
  source.onmessage = e => onMessage(JSON.parse(e.data))
  return () => source.close()
}

export const fmt = (v: number | null | undefined, digits = 1) => (v == null ? '' : v.toFixed(v >= 100 ? 0 : digits))

export function fmtTime(ms: number, withDate = false) {
  const d = new Date(ms)
  const time = d.toLocaleTimeString([], { hour12: false })
  return withDate ? `${d.toLocaleDateString([], { month: 'short', day: 'numeric' })} ${time}` : time
}

export function fmtSpan(ms: number) {
  const s = Math.round(ms / 1000)
  if (ms < 10000) return `${ms / 1000} s`
  if (s < 120) return `${s} s`
  if (s < 7200) return `${Math.round(s / 60)} min`
  if (s < 172800) return `${Math.round(s / 3600)} h`
  return `${Math.round(s / 86400)} days`
}

// randomUUID exists only on pages from this computer or over HTTPS; a page opened from another computer over plain HTTP has no such function.
export const newId = () => typeof crypto.randomUUID === 'function'
  ? crypto.randomUUID().replace(/-/g, '')
  : Array.from(crypto.getRandomValues(new Uint8Array(16)), b => b.toString(16).padStart(2, '0')).join('')

/** The first workspace has an empty id, which cannot be put in a URL. */
export const wsPath = (id: string) => id || 'main'

/** Plays an alert sound from the server's list ("" = the default beep, "none" = nothing). */
export function playSound(name: string) {
  if (name === 'none') return
  void new Audio(`/api/sounds/${encodeURIComponent(name || 'Beep')}`).play().catch(() => { /* the browser wants a click on the page first */ })
}

/**
 * Copies text to the clipboard. Browsers only allow the modern way on some pages and with some settings,
 * so when it is refused the older way (a hidden text box and the "copy" command) is tried.
 */
export async function copyText(text: string) {
  try { await navigator.clipboard.writeText(text); return } catch { /* fall through to the older way */ }
  const box = document.createElement('textarea')
  box.value = text
  box.style.position = 'fixed'; box.style.opacity = '0'
  document.body.appendChild(box)
  box.select()
  const ok = document.execCommand('copy')
  box.remove()
  if (!ok) throw new Error('The browser did not allow copying to the clipboard.')
}

/** Saves text or binary content as a file from the browser. */
export function download(name: string, content: BlobPart, type = 'text/plain') {
  const a = document.createElement('a')
  a.href = URL.createObjectURL(new Blob([content], { type }))
  a.download = name
  a.click()
  setTimeout(() => URL.revokeObjectURL(a.href), 5000)
}

/** Asks the user for a file and returns it (or null if they cancel). */
export function pickFile(accept: string): Promise<File | null> {
  return new Promise(resolve => {
    const input = document.createElement('input')
    input.type = 'file'; input.accept = accept
    input.onchange = () => resolve(input.files?.[0] ?? null)
    input.oncancel = () => resolve(null)
    input.click()
  })
}
