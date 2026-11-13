import { useCallback, useEffect, useRef, useState } from 'react'
import { api, copyText, download, fmt, fmtSpan, fmtTime, newId, type AppState, type HopStat, type Summary, type SummaryDef, type TargetView, type TimelineEvent } from './api'
import { serverNow } from './clock'
import { TimelineChart } from './TimelineChart'
import { PopupMenu, useShortcut, type MenuItem } from './ui'

const SPANS = [60, 300, 600, 1800, 3600, 3 * 3600, 6 * 3600, 12 * 3600, 24 * 3600, 2 * 86400, 7 * 86400, 30 * 86400].map(s => s * 1000)
const INTERVALS = [500, 1000, 2500, 5000, 10000, 15000, 30000, 60000, 300000]
const FOCUS = [5, 30, 60, 300, 600, 1800, 3600]
const ALL = -1   // focus on everything recorded
const COLUMNS: [string, string][] = [['count', 'Count'], ['lost', 'Lost'], ['ip', 'Address'], ['name', 'Name'], ['owner', 'Network'], ['avg', 'Avg'], ['min', 'Min'],
  ['cur', 'Cur'], ['max', 'Max'], ['jitter', 'Jitter'], ['loss', 'Loss %'], ['mos', 'MOS'], ['notes', 'Notes'], ['graph', 'Latency graph']]
// Columns that stay out of the way until someone asks for them.
const OPTIONAL = ['lost', 'notes']

/**
 * Where the line that joins the hops' averages runs: the average where there is one, and a straight line
 * between the neighbours across hops that did not answer.
 */
function routeLine(avgs: (number | null)[]): (number | null)[] {
  return avgs.map((avg, i) => {
    if (avg != null) return avg
    let before = i - 1, after = i + 1
    while (before >= 0 && avgs[before] == null) before--
    while (after < avgs.length && avgs[after] == null) after++
    if (before < 0 || after >= avgs.length) return null
    return avgs[before]! + ((avgs[after]! - avgs[before]!) * (i - before)) / (after - before)
  })
}

interface Props { summary: Summary; state: AppState; tick: number; onBack: () => void; onChanged: () => void }

export function TargetPage({ summary, state, tick, onBack, onChanged }: Props) {
  const target = summary.target
  const readOnly = state.readOnly
  const [span, setSpan] = useState(600_000)
  const [end, setEnd] = useState<number | null>(target.closed && summary.lastTs ? summary.lastTs : null)
  const [focusSeconds, setFocusSeconds] = useState<number | null>(null)
  const [focusAt, setFocusAt] = useState<number | null>(null)
  const [shown, setShown] = useState<number[]>([])
  const [view, setView] = useState<TargetView | null>(null)
  const [error, setError] = useState('')
  const [loadError, setLoadError] = useState('')
  const [note, setNote] = useState('')
  const [menu, setMenu] = useState<{ at: { x: number; y: number }; items: MenuItem[] } | null>(null)
  const [hidden, setHidden] = useState<string[]>(() => JSON.parse(localStorage.getItem('hiddenColumns') ?? '[]'))
  const [extra, setExtra] = useState<string[]>(() => JSON.parse(localStorage.getItem('extraColumns') ?? '[]'))
  const busy = useRef(false)
  const lastLoad = useRef(0)
  const show = (key: string) => OPTIONAL.includes(key) ? extra.includes(key) : !hidden.includes(key)

  const range = useCallback(() => {
    const to = end ?? serverNow()
    const from = to - span
    const focusTo = focusSeconds == null || focusSeconds === ALL ? to : Math.min(focusAt ?? to, to)
    const focusFrom = focusSeconds == null ? from : focusSeconds === ALL ? 0 : focusTo - focusSeconds * 1000
    return { from: Math.round(from), to: Math.round(to), focusFrom: Math.round(focusFrom), focusTo: Math.round(focusTo) }
  }, [end, span, focusSeconds, focusAt])

  const load = useCallback(async () => {
    if (busy.current) return
    busy.current = true
    try {
      const r = range()
      const perBucket = span > 6 * 3600_000 ? 60_000 : target.intervalMs
      const buckets = Math.max(20, Math.min(Math.floor((window.innerWidth - 110) / 3), Math.ceil(span / perBucket)))
      const v = await api<TargetView>(`/targets/${target.id}/view?from=${r.from}&to=${r.to}&focusFrom=${r.focusFrom}&focusTo=${r.focusTo}&hops=${[0, ...shown].join(',')}&buckets=${buckets}`)
      setView(v); setLoadError('')
    } catch (e) { setLoadError((e as Error).message) } finally { busy.current = false; lastLoad.current = Date.now() }
  }, [range, shown, span, target.id, target.intervalMs])

  useEffect(() => { void load() }, [load])
  // Live: reload when new samples arrive, but no more often than the picture can visibly change.
  useEffect(() => {
    if (end == null && Date.now() - lastLoad.current >= Math.max(900, span / 400)) void load()
  }, [tick]) // eslint-disable-line react-hooks/exhaustive-deps

  const attempt = async (work: () => Promise<unknown>, done = '') => {
    try { await work(); setError(''); setNote(done) } catch (e) { setNote(''); setError((e as Error).message) }
  }
  const patch = (body: object) => attempt(async () => { await api(`/targets/${target.id}`, 'PATCH', body); onChanged() })
  const pan = (delta: number) => {
    const now = serverNow()
    const next = (end ?? now) + delta
    setEnd(next >= now ? null : next)
  }
  const zoom = (factor: number, centre: number) => {
    const next = Math.min(SPANS[SPANS.length - 1], Math.max(30_000, span * factor))
    const to = end ?? serverNow()
    const nextEnd = centre + ((to - centre) / span) * next
    setSpan(next)
    setEnd(nextEnd >= serverNow() ? null : nextEnd)
  }
  const focusOn = (ts: number) => {
    const seconds = focusSeconds == null || focusSeconds === ALL ? 60 : focusSeconds
    if (seconds !== focusSeconds) setFocusSeconds(seconds)
    setFocusAt(ts + (seconds * 1000) / 2)
  }
  const addComment = async (ts: number) => {
    const text = prompt(`Note for ${fmtTime(ts, true)}`)
    if (text?.trim()) { await api(`/targets/${target.id}/events`, 'POST', { ts: Math.round(ts), text }); void load() }
  }
  const editEvent = async (e: TimelineEvent) => {
    if (readOnly || e.kind !== 'comment') return
    const text = prompt('Edit the note (clear the text to delete it)', e.text)
    if (text == null) return
    await api(`/events/${e.id}`, text.trim() ? 'PUT' : 'DELETE', { text })
    void load()
  }
  const toggleHop = (hop: number) => setShown(s => (s.includes(hop) ? s.filter(h => h !== hop) : [...s, hop].sort((a, b) => a - b)))
  const exportUrl = (kind: string, extra = '') => {
    const r = range()
    return `/api/targets/${target.id}/export/${kind}?from=${r.from}&to=${r.to}&hops=${[0, ...shown].join(',')}${extra}`
  }
  const copy = (text: string) => attempt(() => copyText(text), 'Copied.')
  // Everything this target recorded as one file (*.traceline), which the Sessions page of any Traceline imports.
  const wholeSession = `/api/targets/${target.id}/export/session?from=0&to=${Number.MAX_SAFE_INTEGER}`
  const copyPicture = () => attempt(async () => {
    const blob = await (await fetch(exportUrl('image.png'))).blob()
    try { await navigator.clipboard.write([new ClipboardItem({ 'image/png': blob })]) }
    catch { throw new Error('This browser did not allow copying a picture. Use "Picture (PNG)" to save it instead.') }
  }, 'Picture copied.')
  const copyRoute = () => attempt(async () => copyText(await (await fetch(exportUrl('trace.txt'))).text()), 'Text copied.')
  useShortcut('ctrl+s', () => { location.href = wholeSession }, !state.readOnly)
  useShortcut('ctrl+shift+c', () => void copyPicture(), !state.readOnly && state.imageSupported)
  useShortcut('ctrl+alt+c', () => void copyRoute(), !state.readOnly)

  const addToSummary = (hop: number) => attempt(async () => {
    const list = await api<SummaryDef[]>('/summaries')
    const names = list.map((s, i) => `${i + 1}: ${s.name}`).join('\n')
    const answer = prompt(`Add to which summary?\n${names}${names ? '\n' : ''}Type a number, or a name for a new summary.`, list.length === 1 ? '1' : '')
    if (!answer?.trim()) return
    const existing = list[Number(answer) - 1]
    const item = { targetId: target.id, hop }
    const next = existing
      ? list.map(s => s === existing ? { ...s, items: [...s.items.filter(i => i.targetId !== item.targetId || i.hop !== item.hop), item] } : s)
      : [...list, { id: newId(), name: answer.trim(), workspaceId: state.activeWorkspace, items: [item] }]
    await api('/summaries', 'PUT', next)
    onChanged()
  }, 'Added to the summary.')

  const hopMenu = (e: React.MouseEvent, h: HopStat, isDestination: boolean) => {
    e.preventDefault()
    const items: MenuItem[] = []
    if (!isDestination) items.push({ label: shown.includes(h.hop) ? 'Hide this timeline' : 'Show this timeline', onClick: () => toggleHop(h.hop) })
    if (h.ip) items.push({ label: 'Copy address', onClick: () => copy(h.ip!) })
    if (h.name) items.push({ label: 'Copy name', onClick: () => copy(h.name!) })
    if (!readOnly) {
      items.push({ separator: true, label: '' })
      items.push({ label: 'Add to a summary…', onClick: () => addToSummary(isDestination ? 0 : h.hop) })
      if (h.ip) items.push({ label: 'Set a name for this address…', onClick: () => {
        const alias = prompt(`Name for ${h.ip} (clear it to go back to the DNS name${h.dnsName ? ` ${h.dnsName}` : ''})`, h.name && h.name !== h.dnsName ? h.name : '')
        if (alias != null) void attempt(async () => { await api(`/hosts/${h.ip}`, 'PUT', { alias }); await load() })
      } })
      if (h.ip) items.push({ label: h.notes ? 'Edit notes…' : 'Add notes…', onClick: () => {
        const notes = prompt(`Notes about ${h.ip} (clear the text to remove them). They show in the Notes column, on every target that passes through this address.`, h.notes ?? '')
        if (notes != null) void attempt(async () => { await api(`/hosts/${h.ip}`, 'PUT', { notes }); await load() }, notes.trim() && !extra.includes('notes') ? 'Saved. Turn on the Notes column under "Columns" to see it.' : '')
      } })
      if (h.ip) items.push({ label: 'Look up this name again', onClick: () => attempt(async () => {
        await api(`/hosts/${h.ip}/resolve`, 'POST')
        setTimeout(() => void load(), 3000)
      }, 'Looking it up; the name refreshes in a few seconds.') })
      if (target.kind === 'Icmp') items.push({ label: 'Create an alert for this hop…', onClick: () => {
        sessionStorage.setItem('newAlert', JSON.stringify({ watch: isDestination ? '' : h.ip ? `ip:${h.ip}` : `hop:${h.hop}`, targetIds: [target.id], name: `${target.name} hop ${h.hop}` }))
        location.hash = '/alerts'
      } })
      if (h.ip && h.ip !== target.address) items.push({ label: 'Trace to this address', onClick: () => attempt(async () => { await api('/targets', 'POST', { input: h.ip }); onChanged() }, `Started tracing ${h.ip}.`) })
    }
    setMenu({ at: { x: e.clientX, y: e.clientY }, items })
  }

  const exportMenu = (e: React.MouseEvent) => {
    const open = (url: string) => window.open(url, '_blank')
    const save = (url: string) => { location.href = url }
    const items: MenuItem[] = [
      { label: 'Report (open, then print or save)', onClick: () => open(exportUrl('report')) },
      { label: 'Report as a file to send', onClick: () => save(exportUrl('report.html')) },
      { separator: true, label: '' },
      { label: 'Picture (PNG)', disabled: !state.imageSupported, onClick: () => attempt(async () => download(`${target.name}.png`, await (await fetch(exportUrl('image.png'))).blob(), 'image/png')) },
      { label: 'Picture (GIF)', disabled: !state.imageSupported, onClick: () => save(exportUrl('image.gif')) },
      { label: 'Picture (BMP)', disabled: !state.imageSupported, onClick: () => save(exportUrl('image.bmp')) },
      { label: 'Copy picture', disabled: !state.imageSupported, onClick: () => void copyPicture() },
      { separator: true, label: '' },
      { label: 'Route as a text file', onClick: () => attempt(async () => download(`${target.name}.txt`, await (await fetch(exportUrl('trace.txt'))).text())) },
      { label: 'Copy route as text', onClick: () => void copyRoute() },
      { separator: true, label: '' },
      { label: 'Every sample (CSV)', onClick: () => save(exportUrl('samples.csv')) },
      { label: 'Statistics per 5 minutes (CSV)', onClick: () => save(exportUrl('stats.csv', '&group=5')) },
      { label: 'Statistics per hour (CSV)', onClick: () => save(exportUrl('stats.csv', '&group=60')) },
      { label: 'Statistics per day (CSV)', onClick: () => save(exportUrl('stats.csv', '&group=1440')) },
      { separator: true, label: '' },
      { label: 'Session file: everything recorded', onClick: () => save(wholeSession) },
      { label: 'Session file: this period only', onClick: () => save(exportUrl('session')) },
    ]
    const rect = (e.currentTarget as HTMLElement).getBoundingClientRect()
    setMenu({ at: { x: rect.right - 280, y: rect.bottom + 4 }, items })
  }

  // From the "Columns" button, or by right-clicking the column headings.
  const columnMenu = (e: React.MouseEvent) => {
    e.preventDefault()
    const rect = (e.currentTarget as HTMLElement).getBoundingClientRect()
    const items: MenuItem[] = COLUMNS.map(([key, label]) => ({ label, checked: show(key), onClick: () => {
      if (OPTIONAL.includes(key)) {
        const next = show(key) ? extra.filter(k => k !== key) : [...extra, key]
        setExtra(next); localStorage.setItem('extraColumns', JSON.stringify(next))
        return
      }
      const next = show(key) ? [...hidden, key] : hidden.filter(k => k !== key)
      setHidden(next); localStorage.setItem('hiddenColumns', JSON.stringify(next))
    } }))
    items.push({ separator: true, label: '' }, { label: 'Reset columns to default', disabled: hidden.length === 0 && extra.length === 0, onClick: () => {
      setHidden([]); setExtra([]); localStorage.removeItem('hiddenColumns'); localStorage.removeItem('extraColumns')
    } })
    setMenu({ at: e.type === 'contextmenu' ? { x: e.clientX, y: e.clientY } : { x: rect.left, y: rect.bottom + 4 }, items })
  }

  const r = view ?? { ...range() }
  const hops = view?.hops ?? []
  const cfg = view?.config
  const warnMs = cfg?.warnMs ?? summary.warnMs, badMs = cfg?.badMs ?? summary.badMs
  // One stray slow reply should not flatten every bar: the scale follows the averages, up to the largest maximum.
  const scale = Math.max(10, Math.min(Math.max(...hops.map(h => h.max ?? 0)), 4 * Math.max(...hops.map(h => h.avg ?? 0)))) * 1.05
  const destinationPeak = Math.max(10, ...(view?.timelines.find(t => t.hop === 0)?.max.map(v => v ?? 0) ?? [])) * 1.15
  const timelineScale = cfg?.scaleMode === 'fixed' ? cfg.scaleMs : cfg?.scaleMode === 'destination' ? destinationPeak : undefined
  const focusBox = focusSeconds == null || focusSeconds === ALL ? null : { from: r.focusFrom, to: r.focusTo }
  const line = routeLine(hops.map(h => h.avg))
  const lossScale = cfg?.lossScalePct || 30
  const d = view?.diagnosis
  const columns = COLUMNS.filter(([key]) => show(key)).length + 1

  return (
    <div className="page">
      <div className="toolbar">
        <button onClick={onBack}>← All targets</button>
        <div className="title">
          <strong>{target.name}</strong>
          <span className="muted"> {target.kind === 'Icmp' ? target.address : `${target.kind.toUpperCase()} probe`}{target.closed ? ' · closed session' : target.paused ? ' · paused' : ''}</span>
        </div>
        <span className="spacer" />
        {!readOnly && !target.closed && <>
          <button onClick={() => patch({ paused: !target.paused })}>{target.paused ? '▶ Resume' : '⏸ Pause'}</button>
          <label>Interval
            <select value={target.intervalMs} onChange={e => patch({ intervalMs: Number(e.target.value) })}>
              {[...new Set([...INTERVALS, target.intervalMs])].sort((a, b) => a - b).map(i => <option key={i} value={i}>{fmtSpan(i)}</option>)}
            </select>
          </label>
          {state.configs.length > 1 && target.kind === 'Icmp' && <label>Settings
            <select value={target.configId || 'default'} onChange={e => patch({ configId: e.target.value })}>
              {state.configs.map(c => <option key={c.id} value={c.id}>{c.name}</option>)}
            </select>
          </label>}
          {target.kind === 'Icmp' && <label className="check"><input type="checkbox" checked={target.finalHopOnly} onChange={e => patch({ finalHopOnly: e.target.checked })} /> Destination only</label>}
        </>}
        {!readOnly && target.closed && <button className="primary" onClick={() => patch({ closed: false })}>Reopen and resume tracing</button>}
        <label>Focus
          <select value={focusSeconds ?? 'auto'} onChange={e => { setFocusSeconds(e.target.value === 'auto' ? null : Number(e.target.value)); setFocusAt(null) }}>
            <option value="auto">Auto (same as timeline)</option>
            {FOCUS.map(s => <option key={s} value={s}>{fmtSpan(s * 1000)}</option>)}
            <option value={ALL}>All that is recorded</option>
          </select>
        </label>
        <label>Timeline
          <select value={SPANS.includes(span) ? span : 'custom'} onChange={e => setSpan(Number(e.target.value))}>
            {!SPANS.includes(span) && <option value="custom">{fmtSpan(span)}</option>}
            {SPANS.map(s => <option key={s} value={s}>{fmtSpan(s)}</option>)}
          </select>
        </label>
        {((end != null && !target.closed) || focusAt != null) && <button className="primary" onClick={() => { setEnd(null); setFocusAt(null) }}>Back to now</button>}
        <button onClick={columnMenu}>Columns ▾</button>
        <button title="Open this target in a window of its own" onClick={() => window.open(location.href, '_blank', 'popup,width=1250,height=850')}>⧉</button>
        {!readOnly && <button onClick={exportMenu}>Export ▾</button>}
        {!readOnly && !target.closed && <button title="Clear everything recorded for this target and start tracing afresh" onClick={() => {
          if (confirm(`Clear everything recorded for ${target.name} and start over? This cannot be undone.`))
            void attempt(async () => { await api(`/targets/${target.id}/data`, 'DELETE'); await api(`/targets/${target.id}`, 'PATCH', { paused: false }); onChanged(); await load() }, 'Cleared. Tracing starts afresh.')
        }}>Start over…</button>}
        {!readOnly && !target.closed && <button title="Stop tracing this target. Its recorded session can be reopened from Sessions." onClick={() => {
          if (confirm(`Close ${target.name}? Tracing stops; what was recorded is kept or discarded according to the auto-save setting.`))
            void attempt(async () => { await api(`/targets/${target.id}/close`, 'POST'); onChanged(); onBack() })
        }}>Close</button>}
      </div>
      {menu && <PopupMenu at={menu.at} items={menu.items} onClose={() => setMenu(null)} />}

      {loadError && <div className="banner bad">{loadError}</div>}
      {error && <div className="banner bad" onClick={() => setError('')}>{error}</div>}
      {note && <div className="banner ok" onClick={() => setNote('')}>{note}</div>}
      {summary.error && <div className="banner bad">{summary.error}</div>}
      {d && <div className={`banner ${d.severity}`}>
        <strong>{d.headline}</strong>
        {d.details.map((line, i) => <div key={i}>{line}</div>)}
      </div>}

      <div className="scroll-x">
        <table className="hops">
          <thead onContextMenu={columnMenu}>
            <tr>
              <th>Hop</th>
              {show('count') && <th>Count</th>}{show('lost') && <th title="Samples that got no answer">Lost</th>}{show('ip') && <th className="left">Address</th>}{show('name') && <th className="left">Name</th>}{show('owner') && <th className="left">Network</th>}
              {show('avg') && <th>Avg</th>}{show('min') && <th>Min</th>}{show('cur') && <th>Cur</th>}{show('max') && <th>Max</th>}{show('jitter') && <th>Jitter</th>}{show('loss') && <th>Loss %</th>}{show('mos') && <th>MOS</th>}{show('notes') && <th className="left">Notes</th>}
              {show('graph') && <th className="graph-head"><span>0</span><span>{fmt(scale, 0)} ms</span></th>}
            </tr>
          </thead>
          <tbody>
            {hops.map((h, i) => {
              const isDestination = i === hops.length - 1
              // The route line enters each row half-way from the hop before and leaves half-way to the hop after.
              const mid = (a: number | null | undefined, b: number | null) => a == null || b == null ? null : (a + b) / 2
              return <HopRow key={h.hop} h={h} scale={scale} warnMs={warnMs} badMs={badMs} isDestination={isDestination} show={show} showMinMax={cfg?.showMinMax !== false}
                lossScale={lossScale} route={[mid(line[i - 1], line[i]), line[i], mid(line[i + 1], line[i])]}
                charted={isDestination || shown.includes(h.hop)} onToggle={() => { if (!isDestination) toggleHop(h.hop) }} onMenu={e => hopMenu(e, h, isDestination)} />
            })}
            {hops.length === 0 && <tr><td colSpan={columns} className="muted left">{target.paused ? 'Paused. No data in this period.' : end == null && focusAt == null ? 'Waiting for the first samples…' : 'No data in this period.'}</td></tr>}
          </tbody>
        </table>
      </div>
      <div className="muted small">
        Statistics cover {focusSeconds === ALL ? 'everything recorded' : `${fmtTime(r.focusFrom, span > 20 * 3600_000)} – ${fmtTime(r.focusTo, span > 20 * 3600_000)}`}{cfg && state.configs.length > 1 ? ` · settings: ${cfg.name}` : ''}.
        {view?.coarse ? ' This range is drawn from one-minute summaries.' : ''} Click a hop to graph it, right-click it for more. On a timeline: drag to move, scroll to zoom, double-click to focus the table on that moment{readOnly ? '' : ', right-click to add a note'}.
      </div>

      <div>
        {[...shown, 0].map(hop => {
          const stat = hop === 0 ? hops[hops.length - 1] : hops.find(h => h.hop === hop)
          return (
            <div key={hop} className="chart-block">
              <div className="chart-title">{hop === 0 ? 'Destination' : `Hop ${hop}`} · {stat?.name ?? stat?.ip ?? target.name}{stat?.owner ? ` · ${stat.owner}` : ''}</div>
              <TimelineChart timeline={view?.timelines.find(t => t.hop === hop)} from={r.from} to={r.to} events={hop === 0 ? view?.events : undefined}
                focus={focusBox} warnMs={warnMs} badMs={badMs} height={hop === 0 ? 150 : 110} scaleMs={timelineScale} lossScale={cfg?.lossScalePct}
                showMinMax={cfg?.showMinMax} jitter={cfg?.showJitter ? { scale: cfg.jitterScaleMs, target: cfg.jitterTargetMs } : undefined}
                onPan={pan} onZoom={zoom} onFocus={focusOn} onComment={readOnly ? undefined : addComment} onEvent={editEvent} />
            </div>
          )
        })}
      </div>

      {view && view.events.length > 0 && <>
        <h3>Notes and events in this period</h3>
        <table className="plain">
          <tbody>
            {[...view.events].reverse().slice(0, 50).map(e => (
              <tr key={e.id}>
                <td className="nowrap">{fmtTime(e.ts, true)}</td>
                <td><span className={`tag ${e.kind}`}>{{ comment: 'Note', route: 'Route change', ip: 'Address', alert: 'Alert' }[e.kind]}</span></td>
                <td className="left wide">{e.text}</td>
                <td>{!readOnly && e.kind === 'comment' && <button className="link" onClick={() => editEvent(e)}>Edit</button>}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </>}
    </div>
  )
}

function HopRow({ h, scale, warnMs, badMs, isDestination, charted, show, showMinMax, lossScale, route, onToggle, onMenu }:
  { h: HopStat; scale: number; warnMs: number; badMs: number; isDestination: boolean; charted: boolean; show: (key: string) => boolean; showMinMax: boolean
    lossScale: number; route: (number | null)[]; onToggle: () => void; onMenu: (e: React.MouseEvent) => void }) {
  const pct = (ms: number) => `${Math.min(100, (ms / scale) * 100)}%`
  // The route line, in a 100 by 100 box stretched over the row: in at the top, through this hop's average, out at the bottom.
  const points = route.map((ms, i) => ms == null ? null : `${Math.min(100, (ms / scale) * 100).toFixed(2)},${i * 50}`).filter(p => p != null)
  const level = h.avg == null ? '' : h.avg > badMs ? 'bad' : h.avg > warnMs ? 'warn' : 'good'
  const silent = h.count > 0 && h.lost === h.count
  return (
    <tr className={`${isDestination ? 'destination' : ''} ${charted ? 'charted' : ''}`} onClick={onToggle} onContextMenu={onMenu}
      title={isDestination ? 'The destination is always graphed. Right-click for more.' : 'Click to show or hide this hop\'s timeline. Right-click for more.'}>
      <td><span className={`hopnum ${silent ? 'silent' : level}`}>{h.hop}</span></td>
      {show('count') && <td>{h.count}</td>}
      {show('lost') && <td>{h.lost || ''}</td>}
      {show('ip') && <td className="left mono">{h.ip ?? '—'}</td>}
      {show('name') && <td className="left clip" title={h.dnsName && h.dnsName !== h.name ? `${h.name} (${h.dnsName})` : h.name ?? ''}>{h.name ?? (silent ? <span className="muted">no reply</span> : '')}</td>}
      {show('owner') && <td className="left clip" title={h.asn ? `${h.asn} ${h.owner ?? ''}` : h.owner ?? ''}>{h.owner ?? ''}</td>}
      {show('avg') && <td>{fmt(h.avg)}</td>}{show('min') && <td>{fmt(h.min)}</td>}{show('cur') && <td>{fmt(h.cur)}</td>}{show('max') && <td>{fmt(h.max)}</td>}{show('jitter') && <td>{fmt(h.jitter)}</td>}
      {show('loss') && <td className={(h.lossPct ?? 0) > 0 && !silent ? 'lossy' : ''}>{fmt(h.lossPct)}</td>}
      {show('mos') && <td>{isDestination ? fmt(h.mos, 2) : ''}</td>}
      {show('notes') && <td className="left clip" title={h.notes ?? ''}>{h.notes ?? ''}</td>}
      {show('graph') && <td className="graph">
        <div className="bar">
          <div className="band good" style={{ width: pct(warnMs) }} />
          <div className="band warn" style={{ left: pct(warnMs), width: `calc(${pct(badMs)} - ${pct(warnMs)})` }} />
          <div className="band bad" style={{ left: pct(badMs), right: 0 }} />
          {showMinMax && h.min != null && h.max != null && <div className="minmax" style={{ left: pct(h.min), width: `calc(${pct(h.max)} - ${pct(h.min)})` }} />}
          {h.max != null && h.max > scale && <div className="over" title={`Maximum ${fmt(h.max)} ms is off the scale`}>›</div>}
          {(h.lossPct ?? 0) > 0 && !silent && <div className="lossbar" title={`${fmt(h.lossPct)}% of samples lost`} style={{ width: `${Math.min(100, ((h.lossPct ?? 0) / lossScale) * 100)}%` }} />}
          {points.length > 1 && <svg className="routeline" viewBox="0 0 100 100" preserveAspectRatio="none"><polyline points={points.join(' ')} /></svg>}
          {h.avg != null && <div className="avg" style={{ left: pct(h.avg) }} />}
          {h.cur != null && <div className="cur" style={{ left: pct(h.cur) }}>×</div>}
        </div>
      </td>}
    </tr>
  )
}
