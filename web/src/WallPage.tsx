import { useEffect, useState } from 'react'
import { api, fmt, fmtSpan, type Summary, type TargetView } from './api'
import { serverNow } from './clock'
import { TimelineChart } from './TimelineChart'

const SPANS = [300, 600, 3600, 6 * 3600, 24 * 3600].map(s => s * 1000)

/**
 * Several targets side by side, each in its own pane: the web counterpart of docking target windows next
 * to each other. Panes are rearranged by dragging one onto another, can be made double width, removed
 * and added, and the arrangement is part of the page address, so a bookmark or a popped-out window keeps it.
 */
export function WallPage({ summaries, ids, slowTick }: { summaries: Summary[]; ids: number[]; slowTick: number }) {
  const [columns, setColumns] = useState(() => Number(localStorage.getItem('wallColumns')) || 2)
  const [span, setSpan] = useState(600_000)
  const [wide, setWide] = useState<number[]>(() => JSON.parse(localStorage.getItem('wallWide') ?? '[]'))
  const [dragging, setDragging] = useState<number | null>(null)
  const [over, setOver] = useState<number | null>(null)
  const order = ids.length > 0 ? ids.filter(id => summaries.some(s => s.target.id === id)) : summaries.map(s => s.target.id)
  const shown = order.map(id => summaries.find(s => s.target.id === id)!)
  const others = summaries.filter(s => !order.includes(s.target.id))
  const arrange = (next: number[]) => { location.hash = `/wall?ids=${next.join(',')}` }
  const drop = (onto: number, moved: number) => {
    if (!order.includes(moved) || moved === onto) return
    const next = order.filter(id => id !== moved)
    next.splice(next.indexOf(onto) + (order.indexOf(moved) < order.indexOf(onto) ? 1 : 0), 0, moved)
    arrange(next)
  }
  const toggleWide = (id: number) => {
    const next = wide.includes(id) ? wide.filter(x => x !== id) : [...wide, id]
    setWide(next); localStorage.setItem('wallWide', JSON.stringify(next))
  }
  return (
    <div className="page wide">
      <div className="toolbar">
        <a href="#/"><button>← All targets</button></a>
        <strong>{shown.length} target{shown.length === 1 ? '' : 's'} side by side</strong>
        <span className="muted small">Drag a pane by its title onto another to rearrange.</span>
        <span className="spacer" />
        {others.length > 0 && <label>Add <select value="" onChange={e => e.target.value && arrange([...order, Number(e.target.value)])}>
          <option value="">a target…</option>{others.map(s => <option key={s.target.id} value={s.target.id}>{s.target.name}</option>)}</select></label>}
        <label>Columns <select value={columns} onChange={e => { setColumns(Number(e.target.value)); localStorage.setItem('wallColumns', e.target.value) }}>
          {[1, 2, 3, 4].map(n => <option key={n}>{n}</option>)}</select></label>
        <label>Period <select value={span} onChange={e => setSpan(Number(e.target.value))}>{SPANS.map(s => <option key={s} value={s}>{fmtSpan(s)}</option>)}</select></label>
        <button onClick={() => window.open(location.href, '_blank', 'popup,width=1500,height=900')} title="Open this arrangement in a window of its own, for a second screen">Pop out</button>
      </div>
      {shown.length === 0 && <div className="empty">None of these targets are in the current workspace.</div>}
      <div className="wall" style={{ gridTemplateColumns: `repeat(${columns}, minmax(0, 1fr))` }}>
        {shown.map(s => (
          <div key={s.target.id} className={`pane-slot ${over === s.target.id && dragging !== s.target.id ? 'drop' : ''}`} style={wide.includes(s.target.id) && columns > 1 ? { gridColumn: 'span 2' } : undefined}
            onDragOver={e => { e.preventDefault(); setOver(s.target.id) }} onDragLeave={() => setOver(o => (o === s.target.id ? null : o))}
            onDrop={e => { e.preventDefault(); drop(s.target.id, Number(e.dataTransfer.getData('text/plain')) || dragging || 0); setDragging(null); setOver(null) }}>
            <Pane s={s} span={span} slowTick={slowTick} isWide={wide.includes(s.target.id)} canWiden={columns > 1}
              onDragStart={() => setDragging(s.target.id)} onDragEnd={() => { setDragging(null); setOver(null) }}
              onWide={() => toggleWide(s.target.id)} onRemove={() => arrange(order.filter(id => id !== s.target.id))} />
          </div>
        ))}
      </div>
    </div>
  )
}

function Pane({ s, span, slowTick, isWide, canWiden, onDragStart, onDragEnd, onWide, onRemove }:
  { s: Summary; span: number; slowTick: number; isWide: boolean; canWiden: boolean; onDragStart: () => void; onDragEnd: () => void; onWide: () => void; onRemove: () => void }) {
  const [view, setView] = useState<TargetView | null>(null)
  useEffect(() => {
    const to = Math.round(serverNow()), from = to - span
    const buckets = Math.min(400, Math.ceil(span / (span > 6 * 3600_000 ? 60_000 : s.target.intervalMs)))
    api<TargetView>(`/targets/${s.target.id}/view?from=${from}&to=${to}&hops=0&buckets=${buckets}`).then(setView).catch(() => { })
  }, [slowTick, s.target.id, s.target.intervalMs, span])
  const dest = view?.hops[view.hops.length - 1]
  const cfg = view?.config
  const d = view?.diagnosis
  return (
    <div className="chart-block pane">
      <div className="pane-head" draggable onDragStart={e => { e.dataTransfer.effectAllowed = 'move'; e.dataTransfer.setData('text/plain', String(s.target.id)); onDragStart() }} onDragEnd={onDragEnd} title="Drag onto another pane to move this one there">
        <span className="grip">⠿</span>
        <a href={`#/t/${s.target.id}`} draggable={false} title="Open the full route"><strong>{s.target.name}</strong></a>
        {s.alerting && <span title="An alert is active">🔔</span>}
        <span className="muted small">{s.target.kind === 'Icmp' ? `${view?.hops.length ?? s.hops} hops` : s.target.kind.toUpperCase()}{s.target.paused ? ' · paused' : ''}</span>
        <span className="spacer" />
        <span className="small">cur <strong>{fmt(s.cur)}</strong> · avg <strong>{fmt(dest?.avg)}</strong> ms · loss <strong className={(dest?.lossPct ?? 0) > 0 ? 'lossy' : ''}>{fmt(dest?.lossPct)}</strong>%</span>
        {canWiden && <button className="link" title={isWide ? 'Normal width' : 'Double width'} onClick={onWide}>{isWide ? '⇥⇤' : '⇤⇥'}</button>}
        <button className="link" title="Open this target in a window of its own" onClick={() => window.open(`${location.pathname}${location.search}#/t/${s.target.id}`, '_blank', 'popup,width=1250,height=850')}>⧉</button>
        <button className="link" title="Take this pane off the view (the target keeps running)" onClick={onRemove}>×</button>
      </div>
      {(s.error || d) && <div className={`pane-note ${s.error ? 'bad' : d!.severity}`}>{s.error ?? d!.headline}</div>}
      <TimelineChart timeline={view?.timelines[0]} from={view?.from ?? 0} to={view?.to ?? 1} events={view?.events} warnMs={s.warnMs} badMs={s.badMs} height={120}
        scaleMs={cfg?.scaleMode === 'fixed' ? cfg.scaleMs : undefined} lossScale={cfg?.lossScalePct} showMinMax={cfg?.showMinMax}
        jitter={cfg?.showJitter ? { scale: cfg.jitterScaleMs, target: cfg.jitterTargetMs } : undefined} />
    </div>
  )
}
