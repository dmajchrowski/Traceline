import { useCallback, useEffect, useState } from 'react'
import { api, download, fmtTime, newId, pickFile, wsPath, type AppState, type ShareLink, type Summary, type TraceConfig, type Workspace } from './api'
import { Banner } from './ui'

type Message = { ok: boolean; text: string } | null

export function SettingsPage({ state, summaries, onChanged }: { state: AppState; summaries: Summary[]; onChanged: () => void }) {
  const [s, setS] = useState(state.settings)
  const [message, setMessage] = useState<Message>(null)
  const set = (patch: Partial<typeof s>) => setS(x => ({ ...x, ...patch }))
  const num = (key: keyof typeof s) => <input type="number" value={s[key] as number} onChange={e => set({ [key]: Number(e.target.value) })} />
  const check = (key: keyof typeof s, label: string) => <label className="check"><input type="checkbox" checked={s[key] as boolean} onChange={e => set({ [key]: e.target.checked })} /> {label}</label>

  const save = async () => {
    try {
      const result = await api<{ restartNeeded: boolean }>('/settings', 'PUT', s)
      setMessage({ ok: true, text: result.restartNeeded ? 'Saved. Restart Traceline for the port, network or HTTPS change to take effect.' : 'Saved.' })
      onChanged()
    } catch (e) { setMessage({ ok: false, text: (e as Error).message }) }
  }

  return (
    <div className="page narrow">
      <Banner message={message} />
      <Configurations state={state} onChanged={onChanged} setMessage={setMessage} />

      <form className="form" onSubmit={e => { e.preventDefault(); void save() }}>
        <h2>General</h2>
        <label>Name of this site <input value={s.siteName} placeholder={state.machine} onChange={e => set({ siteName: e.target.value })} /> <span className="muted small">shown in notifications</span></label>
        {check('reverseDns', 'Look up the names of hops')}
        {check('ownerLookup', 'Look up which network owns each hop')}
        <div className="muted small">Owner lookups send public hop addresses to Team Cymru's free DNS service. Private addresses are labelled locally and never sent.</div>
        {check('commentOnIpChange', 'Add a note to the timeline when a target\'s address changes')}
        {check('keepAwake', 'Keep this computer awake while something is being traced')}
        {check('allowRunProgram', 'Allow alerts to run programs')}
        <div className="muted small">Anyone who can edit alerts can then start programs on this computer, with the rights Traceline runs under.</div>

        <h2>Stored data</h2>
        <label>Keep every sample for {num('rawDays')} days, and one-minute summaries for {num('rollupDays')} days</label>
        <label>When a target is closed{' '}
          <select value={s.autoSaveMode} onChange={e => set({ autoSaveMode: e.target.value as typeof s.autoSaveMode })}>
            <option value="always">always keep its session</option>
            <option value="after">keep its session if it ran long enough</option>
            <option value="selected">keep its session only if marked "keep"</option>
          </select>
          {s.autoSaveMode === 'after' && <>at least {num('autoSaveMinutes')} minutes</>}
        </label>
        {state.dataFolder && <div className="muted small">Data folder: <code>{state.dataFolder}</code>. To back up, stop Traceline and copy this folder.</div>}

        <h2>Access</h2>
        <div className="muted small">{state.hasUsers ? '' : 'There are no accounts, so the UI only answers on this computer and needs no sign-in. '}{state.listeningOnNetwork ? 'Other computers can reach the UI.' : ''}</div>
        {check('listenOnNetwork', 'Let other computers on the network open the UI (needs an administrator account)')}
        <label>Port {num('port')}</label>
        {check('httpsEnabled', 'Also serve the UI over HTTPS (encrypted)')}
        {s.httpsEnabled && <>
          <label>HTTPS port {num('httpsPort')}</label>
          {check('httpsOnly', 'Send browsers on other computers from the plain port to HTTPS')}
          <label>Certificate file <input className="grow" value={s.certificatePath} placeholder="Empty = a certificate Traceline makes for itself" onChange={e => set({ certificatePath: e.target.value })} /></label>
          {s.certificatePath && <label>Its password <input type="password" autoComplete="new-password" value={s.certificatePassword} placeholder={state.https.passwordSet ? '(unchanged)' : 'if the file has one'} onChange={e => set({ certificatePassword: e.target.value })} /></label>}
          <div className="muted small">The file is a .pfx or .p12, or a .pem with the key inside it or next to it as a .key file. With a self-made certificate the connection is encrypted, but browsers show a warning
            because nobody they trust has vouched for it. For a certificate that browsers accept (from Let's Encrypt, for example), get one for your domain and point at its file here, or put a reverse proxy in front.</div>
        </>}
        {state.https.error && <div className="banner bad">HTTPS is not running: {state.https.error}</div>}
        {state.https.active && <div className="muted small">HTTPS is running on port {state.https.port}{state.https.selfMade ? ' with a self-made certificate' : ''}{state.https.expires ? `, valid until ${new Date(state.https.expires).toLocaleDateString()}` : ''}.</div>}
        <div className="muted small">Windows Firewall must allow the ports.</div>
        <label>Agent key <input className="grow" value={s.agentKey} placeholder="Empty = this Traceline is not a remote agent" onChange={e => set({ agentKey: e.target.value })} />
          <button type="button" onClick={() => set({ agentKey: newId() + newId() })}>Generate</button></label>
        <div className="muted small">With a key set, another Traceline that knows the key can trace <em>from</em> this computer (packet type "Remote agent" in a configuration there). That needs network access above.</div>
        <div><button className="primary">Save settings</button></div>
      </form>

      <Users state={state} onChanged={onChanged} setMessage={setMessage} />
      <Workspaces state={state} summaries={summaries} onChanged={onChanged} setMessage={setMessage} />
      <Shares summaries={summaries} />
      <p className="muted small">Traceline {state.version}</p>
    </div>
  )
}

const newConfig = (): TraceConfig => ({
  id: newId(), name: '', description: '', packetType: 'Icmp', tcpPort: 443, timeoutMs: 3000, packetSize: 56, sendDelayMs: 10, maxHops: 30, interface: '',
  startFinalHopOnly: false, allowFragment: true, removeSilentHops: true, silentMinutes: 5, silentSamples: 15,
  agentUrl: '', agentKey: '', agentPacketType: 'Icmp', warnMs: 100, badMs: 200, scaleMode: 'each', scaleMs: 200, lossScalePct: 30,
  showMinMax: true, showJitter: false, jitterScaleMs: 60, jitterTargetMs: 30,
})

function Configurations({ state, onChanged, setMessage }: { state: AppState; onChanged: () => void; setMessage: (m: Message) => void }) {
  const [configs, setConfigs] = useState<TraceConfig[]>([])
  const [edit, setEdit] = useState<TraceConfig | null>(null)
  const [interfaces, setInterfaces] = useState<{ address: string; name: string }[]>([])
  const load = useCallback(async () => setConfigs(await api<TraceConfig[]>('/configs')), [])
  useEffect(() => { void load(); api<{ address: string; name: string }[]>('/interfaces').then(setInterfaces).catch(() => { }) }, [load])
  const save = async (next: TraceConfig[]) => {
    try { await api('/configs', 'PUT', next); setEdit(null); setMessage({ ok: true, text: 'Configurations saved.' }); await load(); onChanged() }
    catch (e) { setMessage({ ok: false, text: (e as Error).message }) }
  }
  return (
    <>
      <h2>Configurations</h2>
      <p className="muted">A configuration is a named set of tracing and display settings. Every target uses one; pick it in the "Settings" box next to the target. {state.configs.length === 1 ? 'With only the default there is nothing to pick.' : ''}</p>
      <table className="plain"><tbody>
        {configs.map(c => (
          <tr key={c.id}>
            <td className="left wide"><strong>{c.name}</strong> <span className="muted small">{c.packetType === 'Agent' ? `via agent ${c.agentUrl}` : c.packetType === 'Tcp' ? `TCP port ${c.tcpPort}` : c.packetType.toUpperCase()} · {c.warnMs}/{c.badMs} ms</span>
              {c.description && <div className="muted small">{c.description}</div>}</td>
            <td className="nowrap"><button className="link" onClick={() => setEdit(c)}>Edit</button> <button className="link" onClick={() => setEdit({ ...c, id: newId(), name: `${c.name} (copy)` })}>Copy</button>{' '}
              {c.id !== 'default' && <button className="link" onClick={() => confirm(`Delete "${c.name}"? Targets using it go back to the default.`) && save(configs.filter(x => x.id !== c.id))}>Delete</button>}</td>
          </tr>
        ))}
      </tbody></table>
      {!edit && <p><button onClick={() => setEdit(newConfig())}>New configuration</button></p>}
      {edit && <ConfigEditor config={edit} interfaces={interfaces} onCancel={() => setEdit(null)}
        onSave={c => save(configs.some(x => x.id === c.id) ? configs.map(x => x.id === c.id ? c : x) : [...configs, c])} />}
    </>
  )
}

function ConfigEditor({ config, interfaces, onSave, onCancel }: { config: TraceConfig; interfaces: { address: string; name: string }[]; onSave: (c: TraceConfig) => void; onCancel: () => void }) {
  const [c, setC] = useState(config)
  const set = (patch: Partial<TraceConfig>) => setC(x => ({ ...x, ...patch }))
  const num = (key: keyof TraceConfig) => <input type="number" value={c[key] as number} onChange={e => set({ [key]: Number(e.target.value) })} />
  const check = (key: keyof TraceConfig, label: string) => <label className="check"><input type="checkbox" checked={c[key] as boolean} onChange={e => set({ [key]: e.target.checked })} /> {label}</label>
  const packets = <><option value="Icmp">ICMP (ping) — works everywhere, no special rights</option><option value="Udp">UDP — needs administrator rights</option><option value="Tcp">TCP — needs administrator rights</option></>
  return (
    <form className="card form" onSubmit={e => { e.preventDefault(); onSave(c) }}>
      <label>Name <input value={c.name} required autoFocus onChange={e => set({ name: e.target.value })} /></label>
      <label>Description <input className="grow" value={c.description} onChange={e => set({ description: e.target.value })} /></label>
      <fieldset className="form"><legend>Engine</legend>
        <label>Packet type <select value={c.packetType} onChange={e => set({ packetType: e.target.value as TraceConfig['packetType'] })}>{packets}<option value="Agent">Remote agent — another Traceline does the tracing</option></select></label>
        {c.packetType === 'Agent' && <>
          <label>Agent address <input className="grow" placeholder="http://other-computer:7878" value={c.agentUrl} onChange={e => set({ agentUrl: e.target.value })} /></label>
          <label>Agent key <input className="grow" type="password" autoComplete="off" value={c.agentKey} onChange={e => set({ agentKey: e.target.value })} /></label>
          <label>The agent sends <select value={c.agentPacketType} onChange={e => set({ agentPacketType: e.target.value as TraceConfig['agentPacketType'] })}>{packets}</select></label>
        </>}
        {(c.packetType === 'Tcp' || (c.packetType === 'Agent' && c.agentPacketType === 'Tcp')) && <label>TCP port {num('tcpPort')} <span className="muted small">443 passes almost every firewall; otherwise use the port of the service you are diagnosing</span></label>}
        {(c.packetType === 'Tcp' || c.packetType === 'Udp') && <div className="muted small">UDP and TCP tracing work when Traceline runs as a service or as administrator. Each target keeps one flow, so the route shown is one real route.</div>}
        <label>Wait for replies {num('timeoutMs')} ms</label>
        <label>Pause between the packets of one round {num('sendDelayMs')} ms</label>
        <label>Packet size {num('packetSize')} bytes</label>
        {check('allowFragment', 'Allow packets to be split up on the way (fragmentation)')}
        <div className="muted small">Turn this off, and raise the packet size, to find the largest packet a route carries in one piece. It applies to ICMP and UDP over IPv4.</div>
        <label>Longest route to look for {num('maxHops')} hops</label>
        {c.packetType !== 'Agent' && <label>Network interface <select value={c.interface} onChange={e => set({ interface: e.target.value })}>
          <option value="">Let the system choose</option>
          {[...new Set([...interfaces.map(i => i.address), c.interface].filter(Boolean))].map(a => <option key={a} value={a}>{a}{interfaces.find(i => i.address === a) ? ` (${interfaces.find(i => i.address === a)!.name})` : ''}</option>)}
        </select></label>}
        {check('startFinalHopOnly', 'Start new targets as destination-only')}
        {check('removeSilentHops', 'Forget the address of a hop that has stopped answering')}
        {c.removeSilentHops && <label>after {num('silentMinutes')} minutes and {num('silentSamples')} samples without an answer</label>}
      </fieldset>
      <fieldset className="form"><legend>Display</legend>
        <label>Latency turns yellow above {num('warnMs')} ms and red above {num('badMs')} ms</label>
        <label>Timeline scale <select value={c.scaleMode} onChange={e => set({ scaleMode: e.target.value as TraceConfig['scaleMode'] })}>
          <option value="each">Each graph to its own data</option><option value="destination">All graphs to the destination's data</option><option value="fixed">Fixed</option>
        </select>{c.scaleMode === 'fixed' && <>{num('scaleMs')} ms</>}</label>
        <label>Packet loss fills the graph at {num('lossScalePct')} %</label>
        {check('showMinMax', 'Draw the minimum-to-maximum range')}
        {check('showJitter', 'Draw a jitter graph under each timeline')}
        {c.showJitter && <label>Jitter scale {num('jitterScaleMs')} ms, with a target line at {num('jitterTargetMs')} ms</label>}
      </fieldset>
      <div><button className="primary">Save configuration</button> <button type="button" onClick={onCancel}>Cancel</button></div>
    </form>
  )
}

function Users({ state, onChanged, setMessage }: { state: AppState; onChanged: () => void; setMessage: (m: Message) => void }) {
  const [users, setUsers] = useState<{ name: string; role: string }[]>([])
  const [name, setName] = useState('')
  const [password, setPassword] = useState('')
  const [role, setRole] = useState('admin')
  const load = useCallback(async () => setUsers(await api<{ name: string; role: string }[]>('/users')), [])
  useEffect(() => { void load() }, [load])
  const act = async (work: () => Promise<unknown>, done: string) => {
    try { await work(); setMessage({ ok: true, text: done }); setName(''); setPassword(''); await load(); onChanged() }
    catch (e) { setMessage({ ok: false, text: (e as Error).message }) }
  }
  const roles: Record<string, string> = { admin: 'Administrator: everything', editor: 'Editor: targets, notes, alerts, discovery, sessions', viewer: 'Viewer: look only' }
  return (
    <>
      <h2>Accounts</h2>
      <p className="muted">{state.hasUsers ? 'Everyone signs in with one of these accounts.' : 'Without accounts nobody has to sign in. Create an administrator first; after that, signing in is required.'}</p>
      <table className="plain"><tbody>
        {users.map(u => (
          <tr key={u.name}>
            <td className="left wide"><strong>{u.name}</strong> <span className="muted small">{roles[u.role]}</span>{u.name === state.user && <span className="tag">you</span>}</td>
            <td className="nowrap"><button className="link" onClick={() => { setName(u.name); setRole(u.role) }}>Change</button>{' '}
              <button className="link" onClick={() => confirm(`Remove the account "${u.name}"?`) && act(() => api(`/users/${encodeURIComponent(u.name)}`, 'DELETE'), 'Account removed.')}>Remove</button></td>
          </tr>
        ))}
        {users.length === 0 && <tr><td className="muted left">No accounts.</td></tr>}
      </tbody></table>
      <form className="card form" onSubmit={e => { e.preventDefault(); void act(() => api('/users', 'POST', { name, password, role: users.length === 0 ? 'admin' : role }), 'Account saved.') }}>
        <label>{users.some(u => u.name.toLowerCase() === name.trim().toLowerCase()) ? 'Change account' : 'New account'} <input placeholder="Name" value={name} autoComplete="off" onChange={e => setName(e.target.value)} /></label>
        <label>Password <input type="password" autoComplete="new-password" value={password} placeholder="At least 8 characters" onChange={e => setPassword(e.target.value)} />
          {users.some(u => u.name.toLowerCase() === name.trim().toLowerCase()) && <span className="muted small">leave empty to keep the current one</span>}</label>
        <label>Role <select value={users.length === 0 ? 'admin' : role} disabled={users.length === 0} onChange={e => setRole(e.target.value)}>
          {Object.entries(roles).map(([key, label]) => <option key={key} value={key}>{label}</option>)}</select></label>
        <div><button className="primary">Save account</button></div>
      </form>
    </>
  )
}

function Workspaces({ state, summaries, onChanged, setMessage }: { state: AppState; summaries: Summary[]; onChanged: () => void; setMessage: (m: Message) => void }) {
  const act = async (work: () => Promise<unknown>, done: string) => {
    try { await work(); setMessage({ ok: true, text: done }); onChanged() } catch (e) { setMessage({ ok: false, text: (e as Error).message }) }
  }
  const exportOne = (w: Workspace) => act(async () => download(`${w.name}.traceline-workspace`, await (await fetch(`/api/workspaces/${wsPath(w.id)}/export`)).text(), 'application/json'), 'Workspace exported.')
  const importOne = async () => {
    const file = await pickFile('.traceline-workspace,.json,application/json')
    if (!file) return
    await act(async () => {
      let doc: unknown
      try { doc = JSON.parse(await file.text()) } catch { throw new Error('That file is not a Traceline workspace.') }
      await api('/workspaces/import', 'POST', doc)
    }, 'Workspace imported. Switch to it to start its targets.')
  }
  // Empties the workspace that is in use: closes its targets and removes its summary screens. The others are deleted whole.
  const clear = (w: Workspace) => {
    const count = summaries.length
    if (!confirm(`Clear the workspace "${w.name}"? ${count === 0 ? 'It has no targets' : count === 1 ? 'Its one target is closed' : `Its ${count} targets are closed`}${count > 0 ? ' (tracing stops; what was recorded is kept or discarded according to the auto-save setting)' : ''}, and its summary screens are removed.`)) return
    void act(async () => {
      for (const s of summaries) await api(`/targets/${s.target.id}/close`, 'POST')
      await api('/summaries', 'PUT', [])
    }, `Workspace "${w.name}" cleared.`)
  }
  return (
    <>
      <h2>Workspaces</h2>
      <p className="muted">A workspace is a set of targets and summary screens, for example "home" and "office". Only the active workspace is traced; the others keep their recorded data. Switch with the box at the top of the page.</p>
      <table className="plain"><tbody>
        {state.workspaces.map(w => (
          <tr key={w.id}>
            <td className="left wide"><strong>{w.name}</strong>{w.id === state.activeWorkspace && <span className="tag">active</span>}</td>
            <td className="nowrap">
              {w.id !== state.activeWorkspace && <button className="link" onClick={() => act(() => api(`/workspaces/${wsPath(w.id)}/activate`, 'POST'), `Switched to ${w.name}.`)}>Switch to</button>}{' '}
              <button className="link" onClick={() => { const name = prompt('Name', w.name); if (name?.trim()) void act(() => api(`/workspaces/${wsPath(w.id)}`, 'PATCH', { name }), 'Renamed.') }}>Rename</button>{' '}
              <button className="link" title="Saves the workspace's targets and summary screens as a file (.traceline-workspace). What was recorded is not in it." onClick={() => exportOne(w)}>Export</button>{' '}
              {w.id === state.activeWorkspace && <button className="link" title="Close all its targets and remove its summary screens" onClick={() => clear(w)}>Clear…</button>}
              {w.id !== state.activeWorkspace && <button className="link" onClick={() => confirm(`Delete the workspace "${w.name}" with all its targets and everything recorded for them? This cannot be undone.`) && act(() => api(`/workspaces/${wsPath(w.id)}`, 'DELETE'), 'Workspace deleted.')}>Delete</button>}
            </td>
          </tr>
        ))}
      </tbody></table>
      <p><button onClick={() => { const name = prompt('Name for the new workspace'); if (name?.trim()) void act(() => api('/workspaces', 'POST', { name }), 'Workspace created.') }}>New workspace</button>{' '}
        <button onClick={importOne}>Import a workspace file…</button></p>
    </>
  )
}

function Shares({ summaries }: { summaries: Summary[] }) {
  const [shares, setShares] = useState<ShareLink[]>([])
  const [name, setName] = useState('')
  const [hours, setHours] = useState(0)
  const [ids, setIds] = useState<number[]>([])
  const load = useCallback(async () => setShares(await api<ShareLink[]>('/shares')), [])
  useEffect(() => { void load() }, [load])
  const url = (s: ShareLink) => `${location.origin}/?share=${s.token}`
  return (
    <>
      <h2>Share links</h2>
      <p className="muted">A share link shows live results without signing in and cannot change anything. The other person must be able to reach this computer on the port above.</p>
      <table className="plain"><tbody>
        {shares.map(s => (
          <tr key={s.token}>
            <td className="left wide"><strong>{s.name}</strong> <span className="muted small">{s.targetIds.length === 0 ? 'all targets' : s.targetIds.map(id => summaries.find(x => x.target.id === id)?.target.name ?? `#${id}`).join(', ')}
              {s.expiresMs ? ` · expires ${fmtTime(s.expiresMs, true)}` : ' · no expiry'}</span>
              <div><input className="grow mono" readOnly value={url(s)} onFocus={e => e.currentTarget.select()} /></div></td>
            <td className="nowrap"><button className="link" onClick={() => navigator.clipboard?.writeText(url(s))}>Copy</button> <a className="link" href={url(s)} target="_blank" rel="noreferrer">Open</a>{' '}
              <button className="link" onClick={async () => { await api(`/shares/${s.token}`, 'DELETE'); void load() }}>Delete</button></td>
          </tr>
        ))}
        {shares.length === 0 && <tr><td className="muted left">No share links.</td></tr>}
      </tbody></table>
      <form className="card form" onSubmit={async e => { e.preventDefault(); await api('/shares', 'POST', { name, targetIds: ids, expiresInHours: hours }); setName(''); setIds([]); void load() }}>
        <label>New link, named <input value={name} placeholder="For my provider" onChange={e => setName(e.target.value)} /></label>
        <label>Expires after <input type="number" min={0} value={hours} onChange={e => setHours(Number(e.target.value))} /> hours <span className="muted">(0 = never)</span></label>
        <fieldset><legend>Shows</legend>
          <label className="check"><input type="checkbox" checked={ids.length === 0} onChange={() => setIds([])} /> All targets</label>
          {summaries.map(s => <label key={s.target.id} className="check"><input type="checkbox" checked={ids.includes(s.target.id)} onChange={() => setIds(x => x.includes(s.target.id) ? x.filter(i => i !== s.target.id) : [...x, s.target.id])} /> {s.target.name}</label>)}
        </fieldset>
        <div><button className="primary">Create link</button></div>
      </form>
    </>
  )
}
