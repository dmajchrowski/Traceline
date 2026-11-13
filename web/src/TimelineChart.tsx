import { useEffect, useRef, useState } from 'react'
import { fmt, fmtTime, type Timeline, type TimelineEvent } from './api'

interface Props {
  timeline: Timeline | undefined
  from: number
  to: number
  events?: TimelineEvent[]
  focus?: { from: number; to: number } | null
  warnMs: number
  badMs: number
  height?: number
  compact?: boolean
  /** Fixed top of the latency scale in ms; otherwise the graph scales to its own data. */
  scaleMs?: number
  /** Loss percentage that fills the full height (default 30). */
  lossScale?: number
  showMinMax?: boolean
  /** Draws a jitter strip under the graph, scaled to jitterScale ms with a line at jitterTarget ms. */
  jitter?: { scale: number; target: number }
  onPan?: (deltaMs: number) => void
  onZoom?: (factor: number, centre: number) => void
  onFocus?: (ts: number) => void
  onComment?: (ts: number) => void
  onEvent?: (e: TimelineEvent) => void
}

const css = (name: string) => getComputedStyle(document.documentElement).getPropertyValue(name).trim()

/**
 * Latency and loss over time for one hop. Each column is one time bucket: a grey bar from minimum to
 * maximum, a line through the averages and a red bar from the top for packet loss. The background is
 * banded good / warning / bad.
 */
export function TimelineChart(p: Props) {
  const canvas = useRef<HTMLCanvasElement>(null)
  const [width, setWidth] = useState(600)
  const [hover, setHover] = useState<{ x: number; ts: number } | null>(null)
  const drag = useRef<{ x: number; moved: boolean } | null>(null)
  const jitterH = p.jitter && !p.compact ? 34 : 0
  const height = (p.height ?? 120) + jitterH
  const padLeft = p.compact ? 0 : 46
  const padBottom = p.compact ? 0 : 18
  const plotW = Math.max(10, width - padLeft)
  const plotH = height - padBottom - jitterH
  const tsAt = (x: number) => p.from + ((x - padLeft) / plotW) * (p.to - p.from)
  const xAt = (ts: number) => padLeft + ((ts - p.from) / (p.to - p.from)) * plotW

  useEffect(() => {
    const el = canvas.current!.parentElement!
    const observer = new ResizeObserver(() => setWidth(el.clientWidth))
    observer.observe(el)
    setWidth(el.clientWidth)
    return () => observer.disconnect()
  }, [])

  const t = p.timeline
  const peak = t ? Math.max(0, ...t.max.map(v => v ?? 0)) : 0
  const scale = p.scaleMs ?? Math.max(10, peak * 1.15)
  const lossScale = p.lossScale ?? 30

  useEffect(() => {
    const c = canvas.current!
    const ratio = window.devicePixelRatio || 1
    c.width = Math.round(width * ratio); c.height = Math.round(height * ratio)
    const g = c.getContext('2d')!
    g.setTransform(ratio, 0, 0, ratio, 0, 0)
    g.clearRect(0, 0, width, height)
    const y = (ms: number) => plotH - (Math.min(ms, scale) / scale) * plotH

    // Bands
    g.fillStyle = css('--band-bad'); g.fillRect(padLeft, 0, plotW, plotH)
    g.fillStyle = css('--band-warn'); g.fillRect(padLeft, y(p.badMs), plotW, plotH - y(p.badMs))
    g.fillStyle = css('--band-good'); g.fillRect(padLeft, y(p.warnMs), plotW, plotH - y(p.warnMs))

    if (p.focus && p.focus.to > p.from && p.focus.from < p.to) {
      g.fillStyle = css('--focus')
      const x0 = Math.max(padLeft, xAt(p.focus.from)), x1 = Math.min(width, xAt(p.focus.to))
      g.fillRect(x0, 0, x1 - x0, plotH)
    }

    if (t) {
      const n = t.avg.length
      const bw = Math.max(1, (t.bucketMs / (p.to - p.from)) * plotW)
      const bx = (i: number) => xAt(t.from + i * t.bucketMs)
      if (p.showMinMax !== false) {
        g.fillStyle = css('--range')
        for (let i = 0; i < n; i++) {
          const a = t.avg[i]
          if (a == null) continue
          const top = y(t.max[i] ?? a), bottom = y(t.min[i] ?? a)
          g.fillRect(bx(i), top, Math.max(1, bw - (bw > 3 ? 1 : 0)), Math.max(1, bottom - top))
        }
      }
      g.strokeStyle = css('--line'); g.lineWidth = 1.3; g.beginPath()
      let pen = false
      for (let i = 0; i < n; i++) {
        const a = t.avg[i]
        if (a == null) { pen = false; continue }
        const x = bx(i) + bw / 2
        if (pen) g.lineTo(x, y(a)); else g.moveTo(x, y(a))
        pen = true
        if (bw > 6) { g.moveTo(x - bw / 2 + 1, y(a)); g.lineTo(x + bw / 2 - 1, y(a)); g.moveTo(x, y(a)) }
      }
      g.stroke()
      g.fillStyle = css('--loss')
      for (let i = 0; i < n; i++) {
        const loss = t.loss[i]
        if (loss == null || loss <= 0) continue
        g.fillRect(bx(i), 0, Math.max(1, bw - (bw > 3 ? 1 : 0)), Math.max(3, (Math.min(loss, lossScale) / lossScale) * plotH))
      }

      if (jitterH > 0 && p.jitter) {
        const top = plotH + 4, h = jitterH - 6
        const jy = (ms: number) => top + h - (Math.min(ms, p.jitter!.scale) / p.jitter!.scale) * h
        g.fillStyle = css('--bg'); g.fillRect(padLeft, top, plotW, h)
        g.strokeStyle = css('--muted'); g.setLineDash([4, 3]); g.beginPath(); g.moveTo(padLeft, jy(p.jitter.target)); g.lineTo(width, jy(p.jitter.target)); g.stroke(); g.setLineDash([])
        g.lineWidth = 1.2
        let on = false
        let over = false
        g.beginPath()
        for (let i = 0; i < n; i++) {
          const j = t.jitter?.[i]
          if (j == null) { on = false; continue }
          const x = bx(i) + bw / 2
          if (on) g.lineTo(x, jy(j)); else g.moveTo(x, jy(j))
          on = true
          if (j > p.jitter.scale) over = true
        }
        g.strokeStyle = css(over ? '--loss' : '--note'); g.stroke()
        if (!p.compact) {
          g.fillStyle = css('--muted'); g.font = '10px system-ui, sans-serif'; g.textAlign = 'right'; g.textBaseline = 'middle'
          g.fillText('jitter', padLeft - 4, top + h / 2)
        }
      }
    }

    if (!p.compact) {
      g.fillStyle = css('--muted'); g.font = '11px system-ui, sans-serif'; g.textBaseline = 'middle'; g.textAlign = 'right'
      g.fillText(`${fmt(scale, 0)} ms`, padLeft - 4, 7)
      g.fillText('0', padLeft - 4, plotH - 6)
      g.textAlign = 'center'; g.textBaseline = 'alphabetic'
      const span = p.to - p.from
      const ticks = Math.max(2, Math.floor(plotW / 110))
      for (let k = 0; k <= ticks; k++) {
        const ts = p.from + (span * k) / ticks
        g.textAlign = k === 0 ? 'left' : k === ticks ? 'right' : 'center'
        g.fillText(fmtTime(ts, span > 20 * 3600_000), xAt(ts), height - 4)
      }
    }

    for (const e of p.events ?? []) {
      if (e.ts < p.from || e.ts > p.to) continue
      const x = xAt(e.ts)
      g.fillStyle = css(e.kind === 'comment' ? '--note' : e.kind === 'alert' ? '--loss' : '--route')
      g.beginPath(); g.moveTo(x, plotH - 9); g.lineTo(x - 5, plotH); g.lineTo(x + 5, plotH); g.closePath(); g.fill()
    }

    if (hover && hover.x >= padLeft) {
      g.strokeStyle = css('--muted'); g.lineWidth = 1; g.beginPath(); g.moveTo(hover.x + 0.5, 0); g.lineTo(hover.x + 0.5, plotH); g.stroke()
    }
  })

  // Wheel zooming must be a non-passive listener to stop the page from scrolling.
  useEffect(() => {
    const c = canvas.current!
    if (!p.onZoom) return
    const wheel = (e: WheelEvent) => {
      e.preventDefault()
      const rect = c.getBoundingClientRect()
      p.onZoom!(e.deltaY > 0 ? 1.25 : 0.8, tsAt(e.clientX - rect.left))
    }
    c.addEventListener('wheel', wheel, { passive: false })
    return () => c.removeEventListener('wheel', wheel)
  })

  const eventNear = (x: number) => (p.events ?? []).find(e => Math.abs(xAt(e.ts) - x) < 6)
  const bucket = hover && t ? Math.floor((hover.ts - t.from) / t.bucketMs) : -1
  const near = hover ? eventNear(hover.x) : undefined
  const tip = hover && hover.x >= padLeft
    ? near
      ? `${fmtTime(near.ts, true)} · ${near.text}`
      : t && bucket >= 0 && bucket < t.avg.length
        ? `${fmtTime(hover.ts, true)} · ${t.avg[bucket] == null
          ? (t.loss[bucket] != null ? 'all lost' : 'no data')
          : `avg ${fmt(t.avg[bucket])} ms (${fmt(t.min[bucket])} – ${fmt(t.max[bucket])})`}${t.loss[bucket] ? ` · ${fmt(t.loss[bucket])}% loss` : ''}${p.jitter && t.jitter?.[bucket] != null ? ` · jitter ${fmt(t.jitter[bucket])}` : ''}`
        : fmtTime(hover.ts, true)
    : null

  return (
    <div className="chart" style={{ height }}>
      <canvas
        ref={canvas}
        style={{ width: '100%', height, cursor: p.onPan ? 'grab' : 'default', touchAction: 'none' }}
        onPointerDown={e => { if (p.onPan && e.button === 0) { drag.current = { x: e.clientX, moved: false }; e.currentTarget.setPointerCapture(e.pointerId) } }}
        onPointerMove={e => {
          const rect = e.currentTarget.getBoundingClientRect()
          const x = e.clientX - rect.left
          setHover({ x, ts: tsAt(x) })
          if (drag.current && p.onPan) {
            const dx = e.clientX - drag.current.x
            if (Math.abs(dx) >= 2) { p.onPan((-dx / plotW) * (p.to - p.from)); drag.current = { x: e.clientX, moved: true } }
          }
        }}
        onPointerUp={e => {
          const wasDrag = drag.current?.moved
          drag.current = null
          if (wasDrag) return
          const rect = e.currentTarget.getBoundingClientRect()
          const hit = eventNear(e.clientX - rect.left)
          if (hit && p.onEvent) p.onEvent(hit)
        }}
        onPointerLeave={() => setHover(null)}
        onDoubleClick={e => p.onFocus?.(tsAt(e.clientX - e.currentTarget.getBoundingClientRect().left))}
        onContextMenu={e => { if (p.onComment) { e.preventDefault(); p.onComment(tsAt(e.clientX - e.currentTarget.getBoundingClientRect().left)) } }}
      />
      {tip && <div className="tip" style={hover!.x > width / 2 ? { right: width - hover!.x + 8 } : { left: hover!.x + 8 }}>{tip}</div>}
    </div>
  )
}
