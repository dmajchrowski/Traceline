import { useCallback, useEffect, useState } from 'react'
import { api, fmtTime, pickFile, type AppState, type SessionInfo } from './api'
import { Banner } from './ui'

/** Every recorded session: targets being traced, paused ones, closed ones and those of other workspaces. */
export function SessionsPage({ state, onChanged, importNow, onImportAsked }: { state: AppState; onChanged: () => void; importNow?: boolean; onImportAsked?: () => void }) {
  const [sessions, setSessions] = useState<SessionInfo[]>([])
  const [message, setMessage] = useState<{ ok: boolean; text: string } | null>(null)
  const [picked, setPicked] = useState<number[]>([])
  const [sort, setSort] = useState<{ key: 'name' | 'state' | 'first' | 'last' | 'samples'; down: boolean }>({ key: 'state', down: false })
  const load = useCallback(async () => { try { setSessions(await api<SessionInfo[]>('/sessions')) } catch (e) { setMessage({ ok: false, text: (e as Error).message }) } }, [])
  useEffect(() => { void load() }, [load])
  const act = async (work: () => Promise<unknown>, done = '') => {
    try { await work(); setMessage(done ? { ok: true, text: done } : null); await load(); onChanged() } catch (e) { setMessage({ ok: false, text: (e as Error).message }) }
  }
  const patch = (id: number, body: object) => act(() => api(`/targets/${id}`, 'PATCH', body))
  const exportUrl = (s: SessionInfo) => `/api/targets/${s.target.id}/export/session?from=0&to=${Number.MAX_SAFE_INTEGER}`
  const importFile = async () => {
    const file = await pickFile('.traceline')
    if (file) await act(() => api('/sessions/import', 'POST', file), 'Session imported as a closed session. Open it to look at it, or reopen it to continue tracing.')
  }
  // Ctrl+O, from any page, leads here and asks for the file at once.
  useEffect(() => {
    if (!importNow) return
    onImportAsked?.()
    void importFile()
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [importNow])
  const order = ['Tracing', 'Paused', 'Other workspace', 'Closed']
  const value = (s: SessionInfo) => sort.key === 'name' ? s.target.name.toLowerCase() : sort.key === 'state' ? order.indexOf(s.state) : sort.key === 'samples' ? s.samples : (s[sort.key] ?? 0)
  const sorted = [...sessions].sort((a, b) => (value(a) < value(b) ? -1 : value(a) > value(b) ? 1 : 0) * (sort.down ? -1 : 1))
  const head = (key: typeof sort.key, label: string, left = true) =>
    <th className={left ? 'left clickable' : 'clickable'} onClick={() => setSort(s => ({ key, down: s.key === key ? !s.down : false }))}>{label}{sort.key === key ? (sort.down ? ' ▾' : ' ▴') : ''}</th>
  const chosen = sessions.filter(s => picked.includes(s.target.id))

  return (
    <div className="page">
      <Banner message={message} />
      <h2>Sessions</h2>
      <p className="muted">Everything recorded, per target. Closing a target stops tracing but keeps its session here (subject to the auto-save setting), where it can be opened, reopened, exported or deleted.
        Old samples are removed automatically according to the retention in Settings.</p>
      <div className="toolbar">
        <button onClick={importFile} title="A file ending in .traceline: what one target recorded, exported from this or another Traceline (Ctrl+O)">Import a session file…</button>
        <button title={'A comma-separated text export in the "one column per hop, one row per sample" layout, with sample times'} onClick={async () => {
          const file = await pickFile('.txt,.csv,text/plain')
          if (file) await act(async () => api('/sessions/import-text', 'POST', new Blob([await file.text()])), 'Text export imported as a closed session.')
        }}>Import a text export…</button>
        {chosen.length > 0 && <>
          <strong>{chosen.length} selected</strong>
          <button onClick={() => chosen.forEach(s => { const a = document.createElement('a'); a.href = exportUrl(s); a.click() })}>Export</button>
          <button onClick={() => confirm(`Delete ${chosen.length} session(s) and everything recorded for them? This cannot be undone.`) && act(async () => { for (const s of chosen) await api(`/targets/${s.target.id}`, 'DELETE'); setPicked([]) }, 'Deleted.')}>Delete</button>
        </>}
      </div>
      <div className="scroll-x"><table className="plain">
        <thead><tr>
          <th><input type="checkbox" checked={picked.length > 0 && picked.length === sessions.length} onChange={e => setPicked(e.target.checked ? sessions.map(s => s.target.id) : [])} /></th>
          {head('name', 'Target')}<th className="left">Address</th>{head('state', 'State')}{state.workspaces.length > 1 && <th className="left">Workspace</th>}
          {head('first', 'Session start')}{head('last', 'Session end')}{head('samples', 'Samples', false)}<th className="left">Follow address changes</th><th className="left">Keep</th><th></th></tr></thead>
        <tbody>
          {sorted.map(s => (
            <tr key={s.target.id}>
              <td><input type="checkbox" checked={picked.includes(s.target.id)} onChange={e => setPicked(p => e.target.checked ? [...p, s.target.id] : p.filter(id => id !== s.target.id))} /></td>
              <td className="left"><a href={`#/t/${s.target.id}`}><strong>{s.target.name}</strong></a></td>
              <td className="left mono">{s.target.address}</td>
              <td className="left">{s.state}</td>
              {state.workspaces.length > 1 && <td className="left">{s.workspace}</td>}
              <td className="left nowrap">{s.first ? fmtTime(s.first, true) : '—'}</td>
              <td className="left nowrap">{s.last ? fmtTime(s.last, true) : '—'}</td>
              <td>{s.samples.toLocaleString()}</td>
              <td className="left"><input type="checkbox" checked={s.target.followDns} title="Follow the name to a new address when its DNS entry changes" onChange={e => patch(s.target.id, { followDns: e.target.checked })} /></td>
              <td className="left"><input type="checkbox" checked={s.target.keep} title="Keep this session when the target is closed, whatever the auto-save setting says" onChange={e => patch(s.target.id, { keep: e.target.checked })} /></td>
              <td className="nowrap">
                <a className="link" href={`#/t/${s.target.id}`}>Open</a>{' '}
                {s.state === 'Closed' || s.state === 'Other workspace'
                  ? <button className="link" title="Start tracing it again in the current workspace" onClick={() => patch(s.target.id, { closed: false, paused: false })}>Reopen</button>
                  : <>
                    <button className="link" onClick={() => patch(s.target.id, { paused: !s.target.paused })}>{s.target.paused ? 'Resume' : 'Pause'}</button>{' '}
                    <button className="link" onClick={() => act(() => api(`/targets/${s.target.id}/close`, 'POST'))}>Close</button>
                  </>}{' '}
                <button className="link" onClick={() => { const name = prompt('Name for this target', s.target.name); if (name?.trim()) void patch(s.target.id, { name: name.trim() }) }}>Rename</button>{' '}
                <a className="link" href={exportUrl(s)} title="Saves everything recorded for this target as a session file (.traceline)">Export</a>{' '}
                <button className="link" onClick={() => confirm(`Clear everything recorded for ${s.target.name}? This cannot be undone.`) && act(() => api(`/targets/${s.target.id}/data`, 'DELETE'))}>Clear data</button>{' '}
                <button className="link" onClick={() => confirm(`Delete ${s.target.name} and everything recorded for it? This cannot be undone.`) && act(() => api(`/targets/${s.target.id}`, 'DELETE'))}>Delete</button>
              </td>
            </tr>
          ))}
          {sessions.length === 0 && <tr><td className="muted left" colSpan={11}>No sessions yet.</td></tr>}
        </tbody>
      </table></div>
    </div>
  )
}
