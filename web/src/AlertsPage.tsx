import { useCallback, useEffect, useState } from 'react'
import { ChannelsPanel } from './Channels'
import { api, download, fmtTime, newId, pickFile, playSound, type AlertRule, type AlertsInfo, type AppState, type Channel, type Summary, type SummaryDef } from './api'

const KINDS: Record<AlertRule['kind'], string> = {
  OverTime: 'Latency or loss over a period',
  OverSamples: 'Latency or loss over a number of samples',
  MosBelow: 'Voice quality (MOS) below a value',
  RouteChanged: 'The route changed',
  IpInRoute: 'An address appears in the route',
  Timer: 'On a timer',
}

/** One line of the alert log: an alert that started, ended or repeated. */
interface AlertLogEntry { id: number; targetId: number; ts: number; text: string; target: string | null }

const newRule = (): AlertRule => ({
  id: newId(), name: '', enabled: true, kind: 'OverTime', watch: '', latencyMs: 0, windowSeconds: 600, percent: 10,
  sampleCount: 10, badCount: 10, mos: 3.5, ip: '', timerMinutes: 1440, targetIds: [], notifyOnStart: true, notifyOnEnd: true,
  repeatMinutes: 0, eachTime: false, eachTimeNotMet: false, channelIds: [], addComment: true, logFile: '', message: '', subject: '', minMinutesBetween: 0,
  attachImage: false, attachData: false, saveImageFolder: '', exportDataFolder: '', runProgram: '', runArguments: '',
  summaryAction: '', summaryId: '', routeAction: '', sound: '',
})

const watchText = (watch: string) => watch.startsWith('hop:') ? `hop ${watch.slice(4)}` : watch.startsWith('ip:') ? watch.slice(3) : 'the destination' 

function describe(r: AlertRule) {
  const bad = r.latencyMs > 0 ? `lost or slower than ${r.latencyMs} ms` : 'lost'
  switch (r.kind) {
    case 'OverTime': return `${r.percent}% or more of samples in ${Math.round(r.windowSeconds / 60)} min are ${bad}`
    case 'OverSamples': return `${r.badCount} or more of the last ${r.sampleCount} samples are ${bad}`
    case 'MosBelow': return `MOS below ${r.mos} over ${Math.round(r.windowSeconds / 60)} min`
    case 'RouteChanged': return 'the route changes'
    case 'IpInRoute': return `${r.ip || '(address)'} appears in the route`
    case 'Timer': return `every ${r.timerMinutes} minutes`
  }
}

export function AlertsPage({ summaries, state }: { summaries: Summary[]; state: AppState }) {
  const [info, setInfo] = useState<AlertsInfo | null>(null)
  const [channels, setChannels] = useState<Channel[]>([])
  const [editRule, setEditRule] = useState<AlertRule | null>(() => {
    // "Create an alert for this hop" on a target's page hands the details over through here.
    const handed = sessionStorage.getItem('newAlert')
    sessionStorage.removeItem('newAlert')
    return handed ? { ...newRule(), kind: 'OverSamples', ...JSON.parse(handed) } : null
  })
  // "Alerts for this target" in a target's right-click menu hands its targets over through here: the list then
  // shows only the alerts that watch them, and a new alert starts with them ticked.
  const [only, setOnly] = useState<number[]>(() => {
    const handed = sessionStorage.getItem('alertsFor')
    sessionStorage.removeItem('alertsFor')
    return handed ? JSON.parse(handed) as number[] : []
  })
  const fresh = (): AlertRule => ({ ...newRule(), targetIds: only })
  const [defs, setDefs] = useState<SummaryDef[]>([])
  const [sounds, setSounds] = useState<string[]>([])
  const [message, setMessage] = useState<{ ok: boolean; text: string } | null>(null)
  const [sound, setSound] = useState(localStorage.getItem('alertSound') !== 'off')
  const [permission, setPermission] = useState('Notification' in window ? Notification.permission : 'unsupported')
  // The alert log: 0 = the short list that comes with the page; otherwise the days to look back (-1 = everything kept).
  const [logDays, setLogDays] = useState(0)
  const [log, setLog] = useState<AlertLogEntry[] | null>(null)
  useEffect(() => {
    if (logDays === 0) { setLog(null); return }
    let live = true
    api<AlertLogEntry[]>(`/alerts/log${logDays > 0 ? `?days=${logDays}` : ''}`).then(l => { if (live) setLog(l) }).catch(e => { if (live) setMessage({ ok: false, text: (e as Error).message }) })
    return () => { live = false }
  }, [logDays, info?.recent[0]?.id])

  const load = useCallback(async () => {
    try { setInfo(await api<AlertsInfo>('/alerts')); setChannels(await api<Channel[]>('/channels')); setDefs(await api<SummaryDef[]>('/summaries')); setSounds(await api<string[]>('/sounds')) }
    catch (e) { setMessage({ ok: false, text: (e as Error).message }) }
  }, [])
  useEffect(() => { void load(); const t = setInterval(load, 10000); return () => clearInterval(t) }, [load])

  const rules = info?.rules.map(r => r.rule) ?? []
  const saveRules = async (next: AlertRule[]) => {
    try { await api('/alerts', 'PUT', next); setEditRule(null); setMessage(null); await load() }
    catch (e) { setMessage({ ok: false, text: (e as Error).message }) }
  }
  const targetNames = (ids: number[]) => ids.length === 0 ? 'all targets' : ids.map(id => summaries.find(s => s.target.id === id)?.target.name ?? `#${id}`).join(', ')
  const listed = (info?.rules ?? []).filter(({ rule }) => only.length === 0 || rule.targetIds.length === 0 || rule.targetIds.some(id => only.includes(id)))

  return (
    <div className="page narrow">
      {message && <div className={`banner ${message.ok ? 'ok' : 'bad'}`}>{message.text}</div>}

      <h2>Alerts</h2>
      <p className="muted">An alert watches the destination of its targets, or one hop on the way. It can tell you when a problem starts and when it ends, and act on it.</p>
      {only.length > 0 && <p>Showing the alerts that watch <strong>{targetNames(only)}</strong>. A new alert starts with {only.length === 1 ? 'that target' : 'those targets'} ticked.{' '}
        <button className="link" onClick={() => setOnly([])}>Show all alerts</button></p>}
      <table className="plain">
        <tbody>
          {listed.map(({ rule, activeOn }) => (
            <tr key={rule.id}>
              <td><input type="checkbox" checked={rule.enabled} title="Enabled" onChange={e => saveRules(rules.map(r => r.id === rule.id ? { ...r, enabled: e.target.checked } : r))} /></td>
              <td className="left wide"><strong>{rule.name}</strong>{activeOn.length > 0 && <span className="tag alert">active on {targetNames(activeOn)}</span>}
                <div className="muted small">When {describe(rule)} · watching {watchText(rule.watch)} of {targetNames(rule.targetIds)}</div></td>
              <td className="nowrap"><button className="link" onClick={() => setEditRule(rule)}>Edit</button> <button className="link" onClick={() => confirm(`Delete the alert "${rule.name}"?`) && saveRules(rules.filter(r => r.id !== rule.id))}>Delete</button></td>
            </tr>
          ))}
          {listed.length === 0 && <tr><td className="muted left">{rules.length === 0 ? 'No alerts yet.' : `No alert watches ${targetNames(only)} yet.`}</td></tr>}
        </tbody>
      </table>
      {!editRule && <p>
        <button className="primary" onClick={() => setEditRule(fresh())}>New alert</button>{' '}
        <button onClick={() => setEditRule({ ...fresh(), name: 'Target down', kind: 'OverSamples', sampleCount: 10, badCount: 10 })}>Template: target down</button>{' '}
        <button onClick={() => setEditRule({ ...fresh(), name: 'High latency', kind: 'OverTime', latencyMs: 250, percent: 10 })}>Template: high latency</button>{' '}
        <button onClick={() => setEditRule({ ...fresh(), name: 'Packet loss', kind: 'OverTime', latencyMs: 0, percent: 5 })}>Template: packet loss</button>
      </p>}
      {editRule && <RuleEditor rule={editRule} summaries={summaries} channels={channels} defs={defs} sounds={sounds} variables={info?.variables ?? []} state={state} onCancel={() => setEditRule(null)}
        onSave={r => saveRules(rules.some(x => x.id === r.id) ? rules.map(x => x.id === r.id ? r : x) : [...rules, r])} />}

      <ChannelsPanel channels={channels} state={state} reload={load} />

      <h2>In this browser</h2>
      <p>
        <label className="check"><input type="checkbox" checked={sound} onChange={e => { setSound(e.target.checked); localStorage.setItem('alertSound', e.target.checked ? 'on' : 'off') }} /> Play the alert's sound in this browser</label>{' '}
        {permission === 'granted' ? <span className="muted">Desktop notifications are on.</span>
          : permission === 'unsupported' ? <span className="muted">This browser has no desktop notifications.</span>
            : <button onClick={async () => setPermission(await Notification.requestPermission())}>Turn on desktop notifications</button>}
      </p>

      <p>Alert sounds: {sounds.map(s => <span key={s} className="tag sound">
        <a onClick={() => playSound(s)} title="Play">{s}</a>
        {s.includes('.') && <a title="Remove this sound" onClick={async () => { if (confirm(`Remove the sound "${s}"?`)) { await api(`/sounds/${encodeURIComponent(s)}`, 'DELETE'); void load() } }}> ×</a>}
      </span>)}{' '}
        <button onClick={async () => {
          const file = await pickFile('.wav,.mp3,.ogg,audio/*')
          if (!file) return
          try { await api(`/sounds/${encodeURIComponent(file.name)}`, 'POST', file); setMessage(null); void load() } catch (e) { setMessage({ ok: false, text: (e as Error).message }) }
        }}>Add a sound file…</button></p>

      <h2>Alert log</h2>
      <p>
        <label>Show{' '}
          <select value={logDays} onChange={e => setLogDays(Number(e.target.value))}>
            <option value={0}>the last 7 days (up to 100)</option><option value={30}>the last 30 days</option><option value={365}>the last year</option><option value={-1}>everything that is kept</option>
          </select></label>{' '}
        {(log ?? info?.recent ?? []).length > 0 && <button title="Saves the lines shown here as a file that a spreadsheet opens" onClick={() => {
          const lines = (log ?? info!.recent.map(e => ({ ...e, target: summaries.find(s => s.target.id === e.targetId)?.target.name ?? null })))
          const cell = (text: string) => `"${text.replace(/"/g, '""')}"`
          download('traceline-alert-log.csv', '\uFEFF' + ['Time,Target,What happened', ...lines.map(e => [cell(new Date(e.ts).toLocaleString()), cell(e.target ?? ''), cell(e.text)].join(','))].join('\r\n') + '\r\n', 'text/csv')
        }}>Save as a file</button>}
      </p>
      <table className="plain"><tbody>
        {(log ?? info?.recent.map(e => ({ ...e, target: summaries.find(s => s.target.id === e.targetId)?.target.name ?? null })) ?? []).map(e =>
          <tr key={e.id}><td className="nowrap">{fmtTime(e.ts, true)}</td><td className="left">{e.target}</td><td className="left wide">{e.text}</td></tr>)}
        {logDays === 0 && info?.recent.length === 0 && <tr><td className="muted left">Nothing in the last 7 days.</td></tr>}
        {logDays !== 0 && log?.length === 0 && <tr><td className="muted left">Nothing in that period.</td></tr>}
        {logDays !== 0 && log === null && <tr><td className="muted left">Loading…</td></tr>}
      </tbody></table>
      {log && log.length >= 5000 && <p className="muted small">Only the latest 5000 lines are shown.</p>}

      <h2>Deliveries since start</h2>
      <table className="plain"><tbody>
        {info?.deliveries.map((d, i) => <tr key={i}><td className="nowrap">{fmtTime(d.ts, true)}</td><td className="left">{d.channel}</td><td className={`left wide ${d.ok ? '' : 'lossy'}`}>{d.text}</td></tr>)}
        {info?.deliveries.length === 0 && <tr><td className="muted left">Nothing sent yet.</td></tr>}
      </tbody></table>
    </div>
  )
}

function RuleEditor({ rule, summaries, channels, defs, sounds, variables, state, onSave, onCancel }:
  { rule: AlertRule; summaries: Summary[]; channels: Channel[]; defs: SummaryDef[]; sounds: string[]; variables: string[]; state: AppState; onSave: (r: AlertRule) => void; onCancel: () => void }) {
  const [r, setR] = useState(rule)
  const set = (patch: Partial<AlertRule>) => setR(x => ({ ...x, ...patch }))
  const num = (key: keyof AlertRule, min = 0) => <input type="number" min={min} value={r[key] as number} onChange={e => set({ [key]: Number(e.target.value) })} />
  const toggle = <T,>(list: T[], item: T) => list.includes(item) ? list.filter(x => x !== item) : [...list, item]
  const latency = <label>Count a sample as bad when it is lost, or slower than {num('latencyMs')} ms <span className="muted">(0 = lost only)</span></label>
  return (
    <form className="card form" onSubmit={e => { e.preventDefault(); onSave(r) }}>
      <label>Name <input value={r.name} required onChange={e => set({ name: e.target.value })} autoFocus /></label>
      <label>Condition <select value={r.kind} onChange={e => set({ kind: e.target.value as AlertRule['kind'] })}>
        {Object.entries(KINDS).map(([k, label]) => <option key={k} value={k}>{label}</option>)}</select></label>
      {r.kind === 'OverTime' && <>{latency}<label>Alert when {num('percent')} % or more of the samples in the last <input type="number" min={1} value={Math.round(r.windowSeconds / 60)} onChange={e => set({ windowSeconds: Number(e.target.value) * 60 })} /> minutes are bad</label></>}
      {r.kind === 'OverSamples' && <>{latency}<label>Alert when {num('badCount', 1)} or more of the last {num('sampleCount', 1)} samples are bad</label></>}
      {r.kind === 'MosBelow' && <label>Alert when MOS is below <input type="number" step="0.1" value={r.mos} onChange={e => set({ mos: Number(e.target.value) })} /> over the last <input type="number" min={1} value={Math.round(r.windowSeconds / 60)} onChange={e => set({ windowSeconds: Number(e.target.value) * 60 })} /> minutes</label>}
      {r.kind === 'IpInRoute' && <label>Address <input value={r.ip} onChange={e => set({ ip: e.target.value })} /></label>}
      {r.kind === 'Timer' && <label>Every {num('timerMinutes', 1)} minutes <span className="muted">(sends the current state, e.g. as a daily report)</span></label>}

      {r.kind !== 'RouteChanged' && r.kind !== 'IpInRoute' && r.kind !== 'Timer' && <label>Watch{' '}
        <select value={r.watch === '' ? '' : r.watch.startsWith('hop:') ? 'hop' : 'ip'} onChange={e => set({ watch: e.target.value === '' ? '' : e.target.value === 'hop' ? 'hop:1' : 'ip:' })}>
          <option value="">The destination</option><option value="hop">A hop, by its number</option><option value="ip">A hop, by its address</option>
        </select>
        {r.watch.startsWith('hop:') && <input type="number" min={1} max={64} value={Number(r.watch.slice(4)) || 1} onChange={e => set({ watch: `hop:${e.target.value}` })} />}
        {r.watch.startsWith('ip:') && <input placeholder="Address of the hop" value={r.watch.slice(3)} onChange={e => set({ watch: `ip:${e.target.value.trim()}` })} />}
        {r.watch !== '' && <span className="muted small">Watching a hop needs full-route tracing, not destination-only.</span>}
      </label>}
      <fieldset><legend>Targets</legend>
        <label className="check"><input type="checkbox" checked={r.targetIds.length === 0} onChange={() => set({ targetIds: [] })} /> All targets</label>
        {summaries.map(s => <label key={s.target.id} className="check"><input type="checkbox" checked={r.targetIds.includes(s.target.id)} onChange={() => set({ targetIds: toggle(r.targetIds, s.target.id) })} /> {s.target.name}</label>)}
      </fieldset>
      {r.kind !== 'RouteChanged' && r.kind !== 'Timer' && <fieldset><legend>Notify</legend>
        <label className="check"><input type="checkbox" checked={r.notifyOnStart} onChange={e => set({ notifyOnStart: e.target.checked })} /> When the problem starts</label>
        <label className="check"><input type="checkbox" checked={r.notifyOnEnd} onChange={e => set({ notifyOnEnd: e.target.checked })} /> When it ends</label>
        <label>Again every {num('repeatMinutes')} minutes while it lasts <span className="muted">(0 = never)</span></label>
        <label className="check"><input type="checkbox" checked={r.eachTime} onChange={e => set({ eachTime: e.target.checked })} /> On every sample while the conditions are met</label>
        <label className="check"><input type="checkbox" checked={r.eachTimeNotMet} onChange={e => set({ eachTimeNotMet: e.target.checked })} /> On every sample while they are not met</label>
      </fieldset>}
      <fieldset><legend>Send through</legend>
        <label className="check"><input type="checkbox" checked={r.channelIds.length === 0} onChange={() => set({ channelIds: [] })} /> Every enabled channel</label>
        <label className="check"><input type="checkbox" checked={r.channelIds.includes('none')} onChange={() => set({ channelIds: r.channelIds.includes('none') ? [] : ['none'] })} /> No channel (only the actions below)</label>
        {channels.map(c => <label key={c.id} className="check"><input type="checkbox" checked={r.channelIds.includes(c.id)} onChange={() => set({ channelIds: toggle(r.channelIds, c.id) })} /> {c.name}</label>)}
      </fieldset>
      <label>Send at most one notification every {num('minMinutesBetween')} minutes <span className="muted">(0 = no limit; ones that come sooner are dropped)</span></label>
      <label>Play a sound{' '}
        <select value={r.sound} onChange={e => set({ sound: e.target.value })}>
          <option value="">Beep (default)</option><option value="none">No sound</option>
          {sounds.filter(s => s !== 'Beep').map(s => <option key={s} value={s}>{s}</option>)}
        </select>
        <button type="button" onClick={() => playSound(r.sound)}>Test</button>
        <span className="muted small">in browsers that have Traceline open, and from the tray icon</span>
      </label>
      <label className="check"><input type="checkbox" checked={r.addComment} onChange={e => set({ addComment: e.target.checked })} /> Mark it on the target's timeline</label>
      <label>Also append a line to this file <input className="grow" placeholder="C:\Logs\traceline-alerts.log (optional)" value={r.logFile} onChange={e => set({ logFile: e.target.value })} /></label>
      <label>Subject line <input className="grow" placeholder="Optional. Leave empty for the standard subject." value={r.subject} onChange={e => set({ subject: e.target.value })} /></label>
      <label>Message text <input className="grow" placeholder="Optional. Leave empty for the standard text." value={r.message} onChange={e => set({ message: e.target.value })} /></label>
      <fieldset><legend>Email extras</legend>
        <label className="check"><input type="checkbox" checked={r.attachImage} disabled={!state.imageSupported} onChange={e => set({ attachImage: e.target.checked })} /> A picture of the trace, shown inside the message</label>
        <label className="check"><input type="checkbox" checked={r.attachData} onChange={e => set({ attachData: e.target.checked })} /> The last hour of samples (CSV)</label>
      </fieldset>
      <fieldset className="form"><legend>More actions when the alert starts</legend>
        <label>Save a picture of the trace in <input className="grow" placeholder="Folder, e.g. C:\Traceline\{{Target}} (optional)" disabled={!state.imageSupported} value={r.saveImageFolder} onChange={e => set({ saveImageFolder: e.target.value })} /></label>
        <label>Save the last hour of samples (CSV) in <input className="grow" placeholder="Folder (optional)" value={r.exportDataFolder} onChange={e => set({ exportDataFolder: e.target.value })} /></label>
        <label>Start a program <input className="grow" placeholder="Full path (optional)" value={r.runProgram} onChange={e => set({ runProgram: e.target.value })} /></label>
        {r.runProgram && <label>with arguments <input className="grow" placeholder="e.g. {{Target}} {{State}}" value={r.runArguments} onChange={e => set({ runArguments: e.target.value })} /></label>}
        {r.runProgram && !state.settings.allowRunProgram && <div className="lossy small">Programs only start once "Allow alerts to run programs" is turned on in Settings.</div>}
        <label>Summary screen{' '}
          <select value={r.summaryAction} onChange={e => set({ summaryAction: e.target.value as AlertRule['summaryAction'] })}>
            <option value="">Leave alone</option><option value="add">Add the target while it lasts</option><option value="remove">Remove the target while it lasts</option>
          </select>
          {r.summaryAction && <select value={r.summaryId} onChange={e => set({ summaryId: e.target.value })}><option value="">Choose a summary…</option>{defs.map(d => <option key={d.id} value={d.id}>{d.name}</option>)}</select>}
          {r.summaryAction && defs.length === 0 && <span className="muted small">Create a summary on the Targets page first.</span>}
        </label>
        <label>Route tracing{' '}
          <select value={r.routeAction} onChange={e => set({ routeAction: e.target.value as AlertRule['routeAction'] })}>
            <option value="">Leave alone</option><option value="full">Trace the full route while it lasts</option><option value="final">Trace the destination only while it lasts</option>
          </select>
        </label>
        <div className="muted small">In message text, folders and arguments these are filled in: {variables.map(v => `{{${v}}}`).join(' ')}</div>
      </fieldset>
      <div><button className="primary">Save alert</button> <button type="button" onClick={onCancel}>Cancel</button></div>
    </form>
  )
}
