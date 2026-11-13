import { Fragment, useCallback, useEffect, useRef, useState } from 'react'
import { api, download, fmt, fmtSpan, listen, newId, pickFile, playSound, shareToken, wsPath, type AppState, type HopStat, type SessionInfo, type Summary, type SummaryDef, type Target, type TargetView, type Workspace } from './api'
import { AlertsPage } from './AlertsPage'
import { serverNow, setServerTime } from './clock'
import { DiscoveryPage } from './DiscoveryPage'
import { HelpPage } from './HelpPage'
import { SessionsPage } from './SessionsPage'
import { SettingsPage } from './SettingsPage'
import { TargetPage } from './TargetPage'
import { TimelineChart } from './TimelineChart'
import { WallPage } from './WallPage'
import { PopupMenu, runShortcut, shortcutOf, useShortcut, type MenuItem } from './ui'

type Toast = { id: number; text: string; state: string }

/** Something a keyboard shortcut asks of the page it leads to. `n` makes two requests for the same thing differ. */
export type Intent = { what: 'new' | 'load' | 'find' | 'import'; n: number }

/** How Traceline's own window (WebView2) and the page talk to each other. A normal browser has no such thing. */
interface HostBridge {
  postMessage(message: unknown): void
  addEventListener(type: 'message', on: (e: MessageEvent) => void): void
  removeEventListener(type: 'message', on: (e: MessageEvent) => void): void
}

function useHash(): [string, (h: string) => void] {
  const [hash, setHash] = useState(location.hash.slice(1) || '/')
  useEffect(() => {
    const on = () => setHash(location.hash.slice(1) || '/')
    window.addEventListener('hashchange', on)
    return () => window.removeEventListener('hashchange', on)
  }, [])
  return [hash, h => { location.hash = h }]
}

export function App() {
  const [state, setState] = useState<AppState | null>(null)
  const [summaries, setSummaries] = useState<Summary[]>([])
  const [extra, setExtra] = useState<Summary | null>(null)
  const [hash, go] = useHash()
  const [ticks, setTicks] = useState<Record<number, number>>({})
  const [slowTick, setSlowTick] = useState(0)
  const [toasts, setToasts] = useState<Toast[]>([])
  const [theme, setTheme] = useState(localStorage.getItem('theme') ?? '')
  const [failed, setFailed] = useState('')
  const [pps, setPps] = useState<number | null>(null)
  const [intent, setIntent] = useState<Intent | null>(null)
  const lastSummary = useRef(0)

  const loadState = useCallback(async () => {
    try {
      const s = await api<AppState>('/state')
      setServerTime(s.now); setState(s); setFailed('')
    } catch (e) { setFailed((e as Error).message) }
  }, [])
  const loadSummaries = useCallback(async () => {
    lastSummary.current = Date.now()
    try { setSummaries(await api<Summary[]>('/targets')) } catch { /* shown elsewhere */ }
  }, [])

  useEffect(() => { void loadState() }, [loadState])
  useEffect(() => {
    const on = () => void loadState()
    window.addEventListener('traceline-login', on)
    return () => window.removeEventListener('traceline-login', on)
  }, [loadState])
  useEffect(() => { document.documentElement.dataset.theme = theme; localStorage.setItem('theme', theme) }, [theme])

  const ready = state != null && !state.needsLogin && !state.shareInvalid
  useEffect(() => {
    if (!ready) return
    void loadSummaries()
    const stop = listen(m => {
      if (m.type === 'sample' && m.targetId != null) {
        setTicks(t => ({ ...t, [m.targetId!]: (t[m.targetId!] ?? 0) + 1 }))
        if (Date.now() - lastSummary.current > 2000) void loadSummaries()
      } else if (m.type === 'targets') {
        void loadSummaries(); setSlowTick(t => t + 1)
      } else if (m.type === 'alert') {
        const toast = { id: Date.now() + Math.random(), text: `${m.target}: ${m.text}`, state: String(m.state) }
        setToasts(t => [...t.slice(-4), toast])
        setTimeout(() => setToasts(t => t.filter(x => x.id !== toast.id)), 15000)
        if (localStorage.getItem('alertSound') !== 'off' && m.state === 'start') playSound(String(m.sound ?? ''))
        if ('Notification' in window && Notification.permission === 'granted') new Notification('Traceline', { body: toast.text })
      }
    })
    // A share link may not ask for the sending rate, so it is not shown there.
    const loadRate = () => { if (!shareToken) api<{ pps: number }>('/rate').then(r => setPps(r.pps)).catch(() => { }) }
    loadRate()
    const timer = setInterval(() => { setSlowTick(t => t + 1); void loadSummaries(); loadRate() }, 5000)
    return () => { stop(); clearInterval(timer) }
  }, [ready, loadSummaries])

  const notify = useCallback((text: string, ok: boolean) => {
    const toast = { id: Date.now() + Math.random(), text, state: ok ? 'end' : 'start' }
    setToasts(t => [...t.slice(-4), toast])
    setTimeout(() => setToasts(t => t.filter(x => x.id !== toast.id)), 15000)
  }, [])

  // Keyboard shortcuts: these four work from every page; a target's page adds its own (see ui.tsx).
  useEffect(() => {
    const on = (e: KeyboardEvent) => { const key = shortcutOf(e); if (key && runShortcut(key)) e.preventDefault() }
    window.addEventListener('keydown', on)
    return () => window.removeEventListener('keydown', on)
  }, [])
  const mayEdit = ready && (state.role === 'admin' || state.role === 'editor')
  const ask = (what: Intent['what'], page: string) => { go(page); setIntent(i => ({ what, n: (i?.n ?? 0) + 1 })) }
  useShortcut('ctrl+n', () => ask('new', '/'), mayEdit)
  useShortcut('ctrl+l', () => ask('load', '/'), mayEdit)
  useShortcut('ctrl+f', () => ask('find', '/'), ready)
  useShortcut('ctrl+o', () => ask('import', '/sessions'), mayEdit)

  // A session or workspace file that was opened with Traceline (double-clicked in Explorer, say) comes in through
  // Traceline's own window, which can read it; the page imports it as whoever is signed in, as its Import buttons do.
  const role = state?.role
  useEffect(() => {
    const host = (window as unknown as { chrome?: { webview?: HostBridge } }).chrome?.webview
    if (!ready || !host) return
    const open = async (m: { type?: string; name?: string; data?: string; text?: string }) => {
      const name = m.name ?? 'the file'
      if (m.type === 'open-failed') { notify(`Could not open ${name}: ${m.text}`, false); return }
      if (m.type !== 'open-file' || typeof m.data !== 'string') return
      try {
        const bytes = Uint8Array.from(atob(m.data), c => c.charCodeAt(0))
        if (/\.traceline-workspace(\.json)?$/i.test(name)) {
          const ws = await api<Workspace>('/workspaces/import', 'POST', JSON.parse(new TextDecoder().decode(bytes)))
          void loadState()
          notify(`Workspace "${ws.name}" imported. Switch to it to start its targets.`, true)
          if (role === 'admin') location.hash = '/settings'
        } else {
          const target = await api<Target>('/sessions/import', 'POST', new Blob([bytes]))
          void loadSummaries()
          notify(`${name} opened as a closed session. "Reopen and resume tracing" continues it.`, true)
          location.hash = `/t/${target.id}`
        }
      } catch (e) { notify(`Could not open ${name}: ${(e as Error).message}`, false) }
    }
    const on = (e: MessageEvent) => { if (e.data === 'ready?') host.postMessage('ready'); else if (e.data && typeof e.data === 'object') void open(e.data) }
    host.addEventListener('message', on)
    host.postMessage('ready')
    return () => host.removeEventListener('message', on)
  }, [ready, role, notify, loadState, loadSummaries])

  const targetId = hash.startsWith('/t/') ? Number(hash.slice(3)) : null
  const inList = summaries.find(s => s.target.id === targetId)
  // A closed session is not in the list of running targets; fetch it on its own so it can still be looked at.
  useEffect(() => {
    if (!ready || targetId == null || inList) { setExtra(null); return }
    api<Summary[]>(`/targets?id=${targetId}`).then(list => setExtra(list[0] ?? null)).catch(() => setExtra(null))
  }, [ready, targetId, inList, slowTick])

  if (failed && !state) return <div className="center"><div className="banner bad">Cannot reach Traceline: {failed}</div></div>
  if (!state) return <div className="center muted">Loading…</div>
  if (state.shareInvalid) return <div className="center"><div className="banner bad">This share link is not valid, or it has expired. Ask whoever sent it for a new one.</div></div>
  if (state.needsLogin) return <Login onDone={loadState} />

  const selected = inList ?? (extra?.target.id === targetId ? extra : undefined)
  const wall = hash.startsWith('/wall')
  const wallIds = wall ? (hash.split('?ids=')[1] ?? '').split(',').filter(Boolean).map(Number) : []
  const page = targetId != null || wall ? 'targets' : hash.slice(1).split('?')[0] || 'targets'
  const canEdit = state.role === 'admin' || state.role === 'editor'
  const nav: [string, string, string][] = [['targets', 'Targets', '/']]
  if (canEdit) nav.push(['alerts', 'Alerts', '/alerts'], ['discovery', 'Discovery', '/discovery'], ['sessions', 'Sessions', '/sessions'])
  if (state.role === 'admin') nav.push(['settings', 'Settings', '/settings'])
  if (state.role) nav.push(['help', 'Help', '/help'])
  const refresh = () => { void loadState(); void loadSummaries(); setSlowTick(t => t + 1) }

  return (
    <>
      <header>
        <a className="brand" href="#/">Traceline</a>
        {shareNameOrNav()}
        <span className="spacer" />
        {state.role && state.workspaces.length > 1 && <label className="muted small">Workspace{' '}
          <select value={state.activeWorkspace} disabled={!canEdit} onChange={async e => { await api(`/workspaces/${wsPath(e.target.value)}/activate`, 'POST'); go('/'); refresh() }}>
            {state.workspaces.map(w => <option key={w.id} value={w.id}>{w.name}</option>)}
          </select></label>}
        {pps != null && state.role && <span className="muted small pps" title="Packets Traceline is sending per second, over the last few seconds">{pps.toFixed(1)} pps</span>}
        <span className="muted small">{state.settings.siteName || state.machine}</span>
        <button className="link" title="Switch between light and dark" onClick={() => setTheme(t => (t === 'dark' ? 'light' : t === 'light' ? '' : 'dark'))}>
          {theme === 'dark' ? 'Dark' : theme === 'light' ? 'Light' : 'Auto'} theme
        </button>
        {state.user && <button className="link" onClick={async () => { await api('/logout', 'POST'); void loadState() }}>Sign out {state.user}</button>}
      </header>
      <main>
        {page === 'targets' && selected && <TargetPage key={selected.target.id} summary={selected} state={state} tick={ticks[selected.target.id] ?? 0} onBack={() => go('/')} onChanged={refresh} />}
        {page === 'targets' && !selected && targetId != null && <div className="empty">This target is not in the current workspace. <a href="#/">Back to all targets</a></div>}
        {wall && <WallPage summaries={summaries} ids={wallIds} slowTick={slowTick} />}
        {page === 'targets' && targetId == null && !wall && <SummaryPage summaries={summaries} state={state} slowTick={slowTick} onOpen={id => go(`/t/${id}`)} onChanged={refresh}
          intent={intent} onIntentDone={() => setIntent(null)} />}
        {page === 'alerts' && canEdit && <AlertsPage summaries={summaries} state={state} />}
        {page === 'discovery' && canEdit && <DiscoveryPage state={state} onChanged={refresh} />}
        {page === 'sessions' && canEdit && <SessionsPage state={state} onChanged={refresh} importNow={intent?.what === 'import'} onImportAsked={() => setIntent(null)} />}
        {page === 'settings' && state.role === 'admin' && <SettingsPage state={state} summaries={summaries} onChanged={refresh} />}
        {page === 'help' && state.role && <HelpPage state={state} />}
      </main>
      <div className="toasts">
        {toasts.map(t => <div key={t.id} className={`toast ${t.state === 'end' ? 'ok' : 'bad'}`} onClick={() => setToasts(x => x.filter(y => y.id !== t.id))}>{t.text}</div>)}
      </div>
    </>
  )

  function shareNameOrNav() {
    if (state!.shareName) return <span className="muted">{state!.shareName} · read-only view</span>
    return <nav>{nav.map(([key, label, to]) => <a key={key} className={page === key ? 'on' : ''} href={`#${to}`}>{label}</a>)}</nav>
  }
}

function Login({ onDone }: { onDone: () => void }) {
  const [name, setName] = useState('')
  const [password, setPassword] = useState('')
  const [error, setError] = useState('')
  return (
    <form className="center card" onSubmit={async e => {
      e.preventDefault()
      try { await api('/login', 'POST', { name, password }); onDone() } catch (err) { setError((err as Error).message) }
    }}>
      <h2>Traceline</h2>
      <label>Name <input autoFocus autoComplete="username" value={name} onChange={e => setName(e.target.value)} /></label>
      <label>Password <input type="password" autoComplete="current-password" value={password} onChange={e => setPassword(e.target.value)} /></label>
      {error && <div className="banner bad">{error}</div>}
      <button className="primary">Sign in</button>
    </form>
  )
}

const SUMMARY_SPANS = [600, 3600, 6 * 3600, 24 * 3600].map(s => s * 1000)

// What each summary column sorts by: the figures its row shows (for the chosen period), once that row has loaded them.
const SORTS = {
  name: (s: Summary) => s.target.name, address: (s: Summary) => s.target.address || null, hops: (s: Summary) => s.hops || null,
  count: (s: Summary, stat?: HopStat) => stat?.count ?? s.count, cur: (s: Summary, stat?: HopStat) => stat?.cur ?? s.cur,
  avg: (s: Summary, stat?: HopStat) => stat ? stat.avg : s.avg, loss: (s: Summary, stat?: HopStat) => stat ? stat.lossPct : s.lossPct,
  jitter: (s: Summary, stat?: HopStat) => stat ? stat.jitter : s.jitter, mos: (s: Summary, stat?: HopStat) => stat ? stat.mos : s.mos,
  lost: (_: Summary, stat?: HopStat) => stat?.lost ?? null, min: (_: Summary, stat?: HopStat) => stat?.min ?? null, max: (_: Summary, stat?: HopStat) => stat?.max ?? null,
  settings: (s: Summary) => s.configName, notes: (_: Summary, stat?: HopStat) => stat?.notes || null,
} satisfies Record<string, (s: Summary, stat?: HopStat) => string | number | null>
type SortKey = keyof typeof SORTS

// The columns of the list after the target's name, in order. The ones in SUMMARY_EXTRA stay out of the way until asked for.
const SUMMARY_COLUMNS: [SortKey | 'graph', string][] = [['address', 'Address'], ['hops', 'Hop'], ['count', 'Count'], ['lost', 'Lost'], ['cur', 'Cur'], ['avg', 'Avg'], ['min', 'Min'], ['max', 'Max'],
  ['loss', 'Loss %'], ['jitter', 'Jitter'], ['mos', 'MOS'], ['settings', 'Settings'], ['notes', 'Notes'], ['graph', 'Latency graph']]
const SUMMARY_EXTRA = ['lost', 'min', 'max', 'settings', 'notes']

/** The tab behind "+": start a new trace, from what was traced before or from scratch. Not a summary id. */
const NEW_TRACE = '+'
/** Offered on the "New trace" tab while there is little history to offer: public DNS servers that answer everywhere. */
const WELL_KNOWN = [['1.1.1.1', 'Cloudflare DNS'], ['8.8.8.8', 'Google DNS'], ['9.9.9.9', 'Quad9 DNS']]

/** "yesterday", "3 days ago": how long ago a session ended, as people say it. */
function ago(ms: number | null) {
  if (!ms) return ''
  const now = new Date(serverNow()), then = new Date(ms)
  const day = (d: Date) => new Date(d.getFullYear(), d.getMonth(), d.getDate()).getTime()
  const days = Math.round((day(now) - day(then)) / 86_400_000)
  if (days <= 0) {
    const minutes = Math.round((now.getTime() - ms) / 60_000)
    return minutes < 1 ? 'just now' : minutes < 60 ? `${minutes} min ago` : 'today'
  }
  return days === 1 ? 'yesterday' : days < 7 ? `${days} days ago` : then.toLocaleDateString()
}

function SummaryPage({ summaries, state, slowTick, onOpen, onChanged, intent, onIntentDone }:
  { summaries: Summary[]; state: AppState; slowTick: number; onOpen: (id: number) => void; onChanged: () => void; intent: Intent | null; onIntentDone: () => void }) {
  const [input, setInput] = useState('')
  const [interval, setIntervalMs] = useState(2500)
  const [configId, setConfigId] = useState('default')
  const [error, setError] = useState('')
  const [defs, setDefs] = useState<SummaryDef[]>([])
  const [tab, setTab] = useState('')
  const [span, setSpan] = useState(600_000)
  const [picked, setPicked] = useState<number[]>([])
  const [menu, setMenu] = useState<{ at: { x: number; y: number }; items: MenuItem[] } | null>(null)
  const [find, setFind] = useState('')
  const [findAsked, setFindAsked] = useState(false)
  const findBox = useRef<HTMLInputElement>(null)
  // Which columns show: null is the usual set; otherwise the list of the ones that are on.
  const [columns, setColumns] = useState<string[] | null>(() => JSON.parse(localStorage.getItem('summaryColumns') ?? 'null'))
  const shows = (key: string) => columns ? columns.includes(key) : !SUMMARY_EXTRA.includes(key)
  const [sort, setSort] = useState<{ key: SortKey; dir: 1 | -1 } | null>(null)
  // The figures each row shows, kept here so that sorting uses the same numbers the reader sees.
  const stats = useRef(new Map<string, HopStat>())
  const [, redraw] = useState(0)
  const canEdit = !state.readOnly

  const loadDefs = useCallback(async () => { if (canEdit || state.role) try { setDefs(await api<SummaryDef[]>('/summaries')) } catch { /* viewers of a share link have none */ } }, [canEdit, state.role])
  useEffect(() => { void loadDefs() }, [loadDefs, slowTick])
  const saveDefs = async (next: SummaryDef[]) => { try { await api('/summaries', 'PUT', next); setDefs(next); setError('') } catch (e) { setError((e as Error).message) } }

  // The "New trace" tab: what was traced before and is closed now, most recent first, each address once.
  const newTrace = tab === NEW_TRACE
  const [sessions, setSessions] = useState<SessionInfo[] | null>(null)
  const inputBox = useRef<HTMLInputElement>(null)
  useEffect(() => {
    if (!newTrace) return
    let live = true
    api<SessionInfo[]>('/sessions').then(s => { if (live) setSessions(s) }).catch(() => { if (live) setSessions([]) })
    return () => { live = false }
  }, [newTrace, slowTick])
  const recent = (sessions ?? []).filter(s => s.state === 'Closed').sort((a, b) => (b.last ?? 0) - (a.last ?? 0))
    .filter((s, i, all) => all.findIndex(x => x.target.input.toLowerCase() === s.target.input.toLowerCase()) === i).slice(0, 8)
  const known = recent.length >= 5 ? [] : WELL_KNOWN.filter(([host]) => ![...recent.map(s => s.target), ...summaries.map(s => s.target)].some(t => t.input === host || t.address === host))

  const current = defs.find(d => d.id === tab)
  const listed = current
    ? current.items.map(i => ({ s: summaries.find(s => s.target.id === i.targetId), hop: i.hop })).filter(r => r.s) as { s: Summary; hop: number }[]
    : summaries.map(s => ({ s, hop: 0 }))
  const wanted = find.trim().toLowerCase()
  const rows = listed.filter(r => !wanted || [r.s.target.name, r.s.target.input, r.s.target.address, r.s.configName].some(text => text?.toLowerCase().includes(wanted)))
  if (sort) {
    const value = SORTS[sort.key]
    rows.sort((a, b) => {
      const x = value(a.s, stats.current.get(`${a.s.target.id}:${a.hop}`)), y = value(b.s, stats.current.get(`${b.s.target.id}:${b.hop}`))
      // Targets with nothing to compare (no data yet) go last whichever way the column is sorted.
      if (x == null || y == null) return x == null ? (y == null ? 0 : 1) : -1
      return (typeof x === 'string' ? x.localeCompare(y as string, undefined, { numeric: true }) : x - (y as number)) * sort.dir
    })
  }
  const sortable = (key: SortKey, label: string, className = '') =>
    <th className={`sortable ${className}`} title="Click to sort; click again for the other way round, and once more for the original order"
      onClick={() => setSort(s => s?.key !== key ? { key, dir: 1 } : s.dir === 1 ? { key, dir: -1 } : null)}>{label}{sort?.key === key ? (sort.dir === 1 ? ' ▲' : ' ▼') : ''}</th>

  const add = async (text: string) => {
    try {
      const added = await api<{ id: number }[]>('/targets', 'POST', { input: text, intervalMs: interval, configId })
      setInput(''); setError('')
      if (current) await saveDefs(defs.map(d => d === current ? { ...d, items: [...d.items, ...added.map(a => ({ targetId: a.id, hop: 0 }))] } : d))
      onChanged()
      if (added.length === 1 && !current) onOpen(added[0].id)
      else if (newTrace) setTab('')
    } catch (err) { setError((err as Error).message) }
  }
  // A recent target is a closed session: tracing starts again and what it recorded before stays with it.
  const reopen = async (id: number) => {
    try { await api(`/targets/${id}`, 'PATCH', { closed: false, paused: false }); setError(''); onChanged(); onOpen(id) }
    catch (err) { setError((err as Error).message) }
  }
  const newSummary = () => {
    const name = prompt('Name for the new summary')
    if (!name?.trim()) return
    const def = { id: newId(), name: name.trim(), workspaceId: state.activeWorkspace, items: [] }
    void saveDefs([...defs, def]); setTab(def.id)
  }
  const loadList = async () => {
    const file = await pickFile('.txt,.lst,.csv,text/plain')
    if (file) await add(await file.text())
  }
  // What a keyboard shortcut asked for on the way here: the "New trace" tab, a list to load, or the box to find a target.
  const asked = useRef(onIntentDone)
  asked.current = onIntentDone
  useEffect(() => {
    if (!intent || intent.what === 'import') return
    if (intent.what === 'new') { setTab(NEW_TRACE); setPicked([]); setTimeout(() => inputBox.current?.focus()) }
    else if (intent.what === 'load') void loadList()
    else { setTab(t => t === NEW_TRACE ? '' : t); setFindAsked(true); setTimeout(() => findBox.current?.focus()) }
    asked.current()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [intent?.n])
  const columnsMenu = (e: React.MouseEvent) => {
    e.preventDefault()
    const set = (next: string[] | null) => { setColumns(next); if (next) localStorage.setItem('summaryColumns', JSON.stringify(next)); else localStorage.removeItem('summaryColumns') }
    const now = SUMMARY_COLUMNS.map(([key]) => key as string).filter(shows)
    const items: MenuItem[] = SUMMARY_COLUMNS.map(([key, label]) => ({ label, checked: shows(key), onClick: () => set(shows(key) ? now.filter(k => k !== key) : [...now, key]) }))
    items.push({ separator: true, label: '' }, { label: 'Reset columns to default', disabled: columns === null, onClick: () => set(null) })
    const box = (e.currentTarget as HTMLElement).getBoundingClientRect()
    setMenu({ at: e.type === 'contextmenu' ? { x: e.clientX, y: e.clientY } : { x: box.left, y: box.bottom + 4 }, items })
  }
  const saveList = () => download('traceline-targets.txt',
    ['# Traceline target list: one target per line. Load it with "Load a list".', ...new Set(rows.map(r => r.s.target.input))].join('\r\n') + '\r\n')
  // Does the same to several targets: the ticked ones, unless a right-click menu names others.
  const bulk = async (work: (id: number) => Promise<unknown>, ids = picked) => {
    try { for (const id of ids) await work(id); setError('') } catch (e) { setError((e as Error).message) }
    if (ids === picked) setPicked([])
    onChanged()
  }
  const addPickedTo = async (def: SummaryDef | null, ids = picked) => {
    const name = def ? null : prompt('Name for the new summary')
    if (!def && !name?.trim()) return
    const items = ids.map(id => ({ targetId: id, hop: 0 }))
    await saveDefs(def
      ? defs.map(d => d === def ? { ...d, items: [...d.items.filter(i => !items.some(x => x.targetId === i.targetId && x.hop === i.hop)), ...items] } : d)
      : [...defs, { id: newId(), name: name!.trim(), workspaceId: state.activeWorkspace, items }])
    if (ids === picked) setPicked([])
  }
  // Each target in a window of its own. A browser may let one click open only one window; Traceline's own window opens them all.
  const openWindows = (ids: number[]) => {
    const blocked = ids.filter(id => !window.open(`${location.pathname}${location.search}#/t/${id}`, '_blank', 'popup,width=1250,height=850')).length
    setError(blocked ? `The browser opened only ${ids.length - blocked} of ${ids.length} windows. Allow pop-ups for this page to get them all.` : '')
  }
  // Right-click on a target. The menu acts on the ticked targets when the row is one of them, and on that row alone otherwise.
  const rowMenu = (e: React.MouseEvent, s: Summary) => {
    e.preventDefault()
    const ids = picked.includes(s.target.id) ? picked : [s.target.id]
    const targets = summaries.filter(x => ids.includes(x.target.id)).map(x => x.target)
    const n = ids.length, many = n === 1 ? '' : ` ${n} targets`, named = n === 1 ? `"${s.target.name}"` : `these ${n} targets`
    const graphs: MenuItem = { label: n === 1 ? 'Show the time graph larger' : `Show ${n} time graphs side by side`, onClick: () => { location.hash = `/wall?ids=${ids.join(',')}` } }
    const windows: MenuItem = { label: n === 1 ? 'Open in a window of its own' : `Open ${n} windows, one for each target`, onClick: () => openWindows(ids) }
    const at = { x: e.clientX, y: e.clientY }
    if (!canEdit) { setMenu({ at, items: [graphs, windows] }); return }
    const allPaused = targets.length > 0 && targets.every(t => t.paused)
    const icmp = targets.filter(t => t.kind === 'Icmp')
    const destinationOnly = icmp.length > 0 && icmp.every(t => t.finalHopOnly)
    const onSummary = (d: SummaryDef) => ids.every(id => d.items.some(i => i.targetId === id))
    setMenu({ at, items: [
      graphs,
      { separator: true },
      { label: `Close${many} (stops tracing)`, onClick: () => confirm(`Close ${named}? Tracing stops; sessions are kept or discarded according to the auto-save setting.`) && bulk(id => api(`/targets/${id}/close`, 'POST'), ids) },
      { label: `${allPaused ? 'Resume' : 'Pause'}${many}`, onClick: () => bulk(id => api(`/targets/${id}`, 'PATCH', { paused: !allPaused }), ids) },
      { label: `Clear the data${n === 1 ? '' : ` of ${n} targets`} and start over…`, onClick: () => confirm(`Clear everything recorded for ${named} and start over? This cannot be undone.`)
        && bulk(async id => { await api(`/targets/${id}/data`, 'DELETE'); await api(`/targets/${id}`, 'PATCH', { paused: false }) }, ids) },
      { separator: true },
      { label: 'Show on summary', items: [
        ...defs.map(d => ({ label: d.name, checked: onSummary(d), onClick: () => { void saveDefs(defs.map(x => x !== d ? x : onSummary(d)
          ? { ...x, items: x.items.filter(i => !ids.includes(i.targetId)) }
          : { ...x, items: [...x.items, ...ids.filter(id => !x.items.some(i => i.targetId === id)).map(id => ({ targetId: id, hop: 0 }))] })) } })),
        ...(defs.length > 0 ? [{ separator: true }] : []),
        { label: 'New summary…', onClick: () => { void addPickedTo(null, ids) } },
      ] },
      { separator: true },
      { label: n === 1 ? 'Alerts for this target…' : `Alerts for these ${n} targets…`, onClick: () => { sessionStorage.setItem('alertsFor', JSON.stringify(ids)); location.hash = '/alerts' } },
      { separator: true },
      windows,
      { separator: true },
      { label: `Trace the destination only${n === 1 ? '' : ` (${icmp.length} of ${n} targets)`}`, checked: destinationOnly, disabled: icmp.length === 0,
        onClick: () => bulk(id => api(`/targets/${id}`, 'PATCH', { finalHopOnly: !destinationOnly }), icmp.map(t => t.id)) },
    ] })
  }
  const tabMenu = (e: React.MouseEvent, def: SummaryDef) => {
    e.preventDefault()
    if (!canEdit) return
    setMenu({ at: { x: e.clientX, y: e.clientY }, items: [
      { label: 'Rename…', onClick: () => { const name = prompt('Name', def.name); if (name?.trim()) void saveDefs(defs.map(d => d === def ? { ...d, name: name.trim() } : d)) } },
      { label: 'Delete this summary', onClick: () => { if (confirm(`Delete the summary "${def.name}"? Its targets keep running.`)) { void saveDefs(defs.filter(d => d !== def)); setTab('') } } },
    ] })
  }

  return (
    <div className="page">
      {menu && <PopupMenu at={menu.at} items={menu.items} onClose={() => setMenu(null)} />}
      {canEdit && <form className="toolbar" onSubmit={e => { e.preventDefault(); void add(input) }}>
        <input className="grow" ref={inputBox} placeholder="Host name or address to trace, e.g. example.com" value={input} onChange={e => setInput(e.target.value)} autoFocus />
        <label>Interval
          <select value={interval} onChange={e => setIntervalMs(Number(e.target.value))}>
            {[1000, 2500, 5000, 15000, 60000].map(i => <option key={i} value={i}>{fmtSpan(i)}</option>)}
          </select>
        </label>
        {state.configs.length > 1 && <label>Settings
          <select value={configId} onChange={e => setConfigId(e.target.value)}>{state.configs.map(c => <option key={c.id} value={c.id}>{c.name}</option>)}</select>
        </label>}
        <button className="primary">Start</button>
        <button type="button" onClick={loadList} title="A text file with one target per line">Load a list…</button>
        {listed.length > 0 && <button type="button" onClick={saveList} title="Save the targets shown below as a text file, one per line, that &quot;Load a list&quot; can read">Save list</button>}
      </form>}
      {canEdit && <div className="muted small">
        Several at once: separate with commas. Other probes: <code>tcp://host:443</code> (connect time), <code>https://example.com/</code> (response time),{' '}
        <code>dns://example.com</code> or <code>dns://example.com@1.1.1.1</code> (lookup time). Force a protocol with <code>ipv4:host</code> or <code>ipv6:host</code>.
      </div>}
      {error && <div className="banner bad">{error}</div>}

      <div className="tabs">
        <a className={tab === '' ? 'on' : ''} onClick={() => { setTab(''); setPicked([]) }}>All targets</a>
        {defs.map(d => <a key={d.id} className={tab === d.id ? 'on' : ''} onClick={() => { setTab(d.id); setPicked([]) }} onContextMenu={e => tabMenu(e, d)} title={canEdit ? 'Right-click to rename or delete' : ''}>{d.name}</a>)}
        {canEdit && <a className={newTrace ? 'on' : ''} title="Start a new trace, or make a new summary" onClick={() => { setTab(NEW_TRACE); setPicked([]); inputBox.current?.focus() }}>{newTrace ? 'New trace' : '+'}</a>}
        <span className="spacer" />
        {!newTrace && (listed.length > 3 || findAsked || find !== '') && <input className="find" type="search" ref={findBox} placeholder="Find a target…" value={find} onChange={e => setFind(e.target.value)} title="Shows only the targets whose name or address contains this text (Ctrl+F)" />}
        {!newTrace && rows.length > 0 && <button title="Show these targets side by side, each in its own pane" onClick={() => { location.hash = `/wall?ids=${(picked.length > 0 ? picked : [...new Set(rows.map(r => r.s.target.id))]).join(',')}` }}>
          {picked.length > 0 ? `Tile ${picked.length} selected` : 'Tile'}</button>}
        {!newTrace && <label className="muted small">Period{' '}
          <select value={span} onChange={e => setSpan(Number(e.target.value))}>{SUMMARY_SPANS.map(s => <option key={s} value={s}>{fmtSpan(s)}</option>)}</select></label>}
        {!newTrace && rows.length > 0 && <button onClick={columnsMenu} title="Choose the columns of this list. Right-clicking the column headings does the same.">Columns ▾</button>}
      </div>

      {newTrace && <div className="newtrace">
        <div className="card">
          <h3>Trace a recent target again</h3>
          {sessions === null ? <div className="muted">Looking up what was traced before…</div>
            : recent.length === 0 ? <div className="muted">Nothing that was traced before is closed at the moment. A target you close shows up here.</div>
            : <div className="recent">{recent.map(s =>
              <button key={s.target.id} type="button" title="Starts tracing it again; what was recorded before stays with it" onClick={() => reopen(s.target.id)}>
                <strong>{s.target.name}</strong>
                {s.target.address && s.target.address !== s.target.name && <span className="muted mono">{s.target.address}</span>}
                <span className="spacer" /><span className="muted small">{ago(s.last)}</span><span className="go">▶</span>
              </button>)}</div>}
          {known.length > 0 && <>
            <div className="muted small label">{recent.length === 0 ? 'Well-known targets to start with' : 'Or a well-known target'}</div>
            <div className="recent">{known.map(([host, what]) =>
              <button key={host} type="button" title={`Starts a trace to ${host}`} onClick={() => add(host)}>
                <strong>{host}</strong><span className="muted">{what}</span><span className="spacer" /><span className="go">▶</span>
              </button>)}</div>
          </>}
          <div className="muted small label">Or type a host name or address in the box at the top and press Start.</div>
        </div>
        <div className="card">
          <h3>Make a summary</h3>
          <div className="muted">A summary is a tab of its own for a group of targets, or for single hops you want to keep an eye on.</div>
          <p><button type="button" onClick={newSummary}>New summary…</button></p>
        </div>
      </div>}

      {picked.length > 0 && <div className="toolbar bulk">
        <strong>{picked.length} selected</strong>
        <button onClick={() => bulk(id => api(`/targets/${id}`, 'PATCH', { paused: true }))}>Pause</button>
        <button onClick={() => bulk(id => api(`/targets/${id}`, 'PATCH', { paused: false }))}>Resume</button>
        <label>Interval <select value="" onChange={e => e.target.value && bulk(id => api(`/targets/${id}`, 'PATCH', { intervalMs: Number(e.target.value) }))}>
          <option value="">change…</option>{[500, 1000, 2500, 5000, 15000, 60000, 300000].map(i => <option key={i} value={i}>{fmtSpan(i)}</option>)}</select></label>
        {state.configs.length > 1 && <label>Settings <select value="" onChange={e => e.target.value && bulk(id => api(`/targets/${id}`, 'PATCH', { configId: e.target.value }))}>
          <option value="">change…</option>{state.configs.map(c => <option key={c.id} value={c.id}>{c.name}</option>)}</select></label>}
        <label>Add to summary <select value="" onChange={e => e.target.value && addPickedTo(defs.find(d => d.id === e.target.value) ?? null)}>
          <option value="">choose…</option>{defs.map(d => <option key={d.id} value={d.id}>{d.name}</option>)}<option value="new">New summary…</option></select></label>
        {current && <button onClick={() => { void saveDefs(defs.map(d => d === current ? { ...d, items: d.items.filter(i => !picked.includes(i.targetId)) } : d)); setPicked([]) }}>Remove from this summary</button>}
        <button onClick={() => confirm(`Close ${picked.length} target(s)? Tracing stops; sessions are kept or discarded according to the auto-save setting.`) && bulk(id => api(`/targets/${id}/close`, 'POST'))}>Close</button>
      </div>}

      {newTrace ? null : rows.length === 0
        ? <div className="empty">{wanted && listed.length > 0 ? `No target here matches "${find.trim()}".` : state.readOnly ? 'Nothing to show here.' : current ? 'This summary is empty. Add targets above, tick targets on "All targets", or right-click a hop on a target\'s page.' : 'Nothing is being traced yet. Enter a host above to start, or press + for targets that were traced before.'}</div>
        : <div className="scroll-x"><table className="summary">
          <thead onContextMenu={columnsMenu}><tr>
            {canEdit && <th><input type="checkbox" title="Select all" checked={picked.length > 0 && picked.length === new Set(rows.map(r => r.s.target.id)).size}
              onChange={e => setPicked(e.target.checked ? [...new Set(rows.map(r => r.s.target.id))] : [])} /></th>}
            <th></th>{sortable('name', 'Target', 'left')}
            {SUMMARY_COLUMNS.filter(([key]) => shows(key)).map(([key, label]) => key === 'graph' ? <th key={key} className="left wide">Last {fmtSpan(span)}</th>
              : <Fragment key={key}>{sortable(key, label, key === 'address' || key === 'settings' || key === 'notes' ? 'left' : '')}</Fragment>)}</tr></thead>
          <tbody>{rows.map(r => <SummaryRow key={`${r.s.target.id}:${r.hop}`} s={r.s} hop={r.hop} span={span} slowTick={slowTick} canEdit={canEdit} showConfig={state.configs.length > 1 && !shows('settings')} shows={shows}
            onStat={stat => { stats.current.set(`${r.s.target.id}:${r.hop}`, stat); if (sort) redraw(n => n + 1) }}
            picked={picked.includes(r.s.target.id)} onPick={on => setPicked(p => on ? [...p, r.s.target.id] : p.filter(id => id !== r.s.target.id))} onOpen={() => onOpen(r.s.target.id)}
            onMenu={e => rowMenu(e, r.s)} />)}</tbody>
        </table></div>}
      {!newTrace && rows.length > 0 && <div className="muted small">Cur is the latest sample; the other figures cover the period chosen above. Click a row for the full route, or right-click it for a menu. Right-click the column headings to choose the columns.{canEdit ? ' On a ticked row the menu acts on all ticked targets.' : ''}</div>}
    </div>
  )
}

function SummaryRow({ s, hop, span, slowTick, canEdit, showConfig, shows, picked, onPick, onOpen, onMenu, onStat }:
  { s: Summary; hop: number; span: number; slowTick: number; canEdit: boolean; showConfig: boolean; shows: (key: string) => boolean; picked: boolean; onPick: (on: boolean) => void; onOpen: () => void
    onMenu: (e: React.MouseEvent) => void; onStat: (stat: HopStat) => void }) {
  const [view, setView] = useState<TargetView | null>(null)
  const report = useRef(onStat)
  report.current = onStat
  useEffect(() => {
    const to = serverNow(), from = to - span
    const buckets = Math.min(150, Math.ceil(span / (span > 6 * 3600_000 ? 60_000 : s.target.intervalMs)))
    api<TargetView>(`/targets/${s.target.id}/view?from=${Math.round(from)}&to=${Math.round(to)}&hops=${hop}&buckets=${buckets}`).then(v => {
      setView(v)
      const shown = hop === 0 ? v.hops[v.hops.length - 1] : v.hops.find(h => h.hop === hop)
      if (shown) report.current(shown)
    }).catch(() => { })
  }, [slowTick, s.target.id, s.target.intervalMs, hop, span])
  const stat = view ? (hop === 0 ? view.hops[view.hops.length - 1] : view.hops.find(h => h.hop === hop)) : undefined
  const avg = stat?.avg ?? null, loss = stat?.lossPct ?? null
  const status = s.target.paused ? 'paused' : s.error || !stat || stat.count === 0 ? 'unknown'
    : (loss ?? 0) >= 10 || (avg ?? 0) > s.badMs || avg == null ? 'bad'
      : (loss ?? 0) >= 2 || (avg ?? 0) > s.warnMs ? 'warn' : 'good'
  return (
    <tr onClick={onOpen} onContextMenu={onMenu} className="clickable">
      {canEdit && <td onClick={e => e.stopPropagation()}><input type="checkbox" checked={picked} onChange={e => onPick(e.target.checked)} /></td>}
      <td><span className={`dot ${status}`} title={status} />{s.alerting && <span title="An alert is active"> 🔔</span>}</td>
      <td className="left"><strong>{s.target.name}</strong>{s.target.kind !== 'Icmp' && <span className="tag">{s.target.kind.toUpperCase()}</span>}
        {showConfig && s.target.configId && <span className="tag">{s.configName}</span>}
        {s.error && <div className="lossy small">{s.error}</div>}{s.target.paused && <span className="muted"> paused</span>}</td>
      {shows('address') && <td className="left mono">{hop === 0 ? (s.target.kind === 'Icmp' || s.target.kind === 'Tcp' ? s.target.address : '') : stat?.ip ?? ''}
        {hop !== 0 && stat?.name && <span className="muted small"> {stat.name}</span>}</td>}
      {shows('hops') && <td>{hop === 0 ? (s.hops || '') : hop}</td>}
      {shows('count') && <td>{stat?.count ?? ''}</td>}
      {shows('lost') && <td className={(stat?.lost ?? 0) > 0 ? 'lossy' : ''}>{stat?.lost ?? ''}</td>}
      {shows('cur') && <td>{hop === 0 ? fmt(s.cur) : fmt(stat?.cur)}</td>}
      {shows('avg') && <td>{fmt(avg)}</td>}
      {shows('min') && <td>{fmt(stat?.min)}</td>}
      {shows('max') && <td>{fmt(stat?.max)}</td>}
      {shows('loss') && <td className={(loss ?? 0) > 0 ? 'lossy' : ''}>{fmt(loss)}</td>}
      {shows('jitter') && <td>{fmt(stat?.jitter)}</td>}
      {shows('mos') && <td>{fmt(stat?.mos, 2)}</td>}
      {shows('settings') && <td className="left">{s.configName}</td>}
      {shows('notes') && <td className="left">{stat?.notes ?? ''}</td>}
      {shows('graph') && <td className="wide"><TimelineChart timeline={view?.timelines[0]} from={view?.from ?? 0} to={view?.to ?? 1} warnMs={s.warnMs} badMs={s.badMs} height={34} compact
        scaleMs={view?.config.scaleMode === 'fixed' ? view.config.scaleMs : undefined} lossScale={view?.config.lossScalePct} showMinMax={view?.config.showMinMax} /></td>}
    </tr>
  )
}
