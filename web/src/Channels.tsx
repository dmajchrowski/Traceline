import { useEffect, useState, type ReactNode } from 'react'
import { api, download, fmtTime, newId, pickFile, type AppState, type Channel } from './api'

// Notification channels, laid out like Service Monitor's Notifications page: the channels in a list on the left, and on
// the right either the selected channel with the steps to set it up, or the kinds of channel to choose a new one from.

/** A mail provider the server knows the settings of, so that only the address and an app password are asked for. */
interface Provider { id: string; name: string; host: string; port: number; security: string; addressExample: string; instructions: string; accountPageUrl: string; accountPageButton: string }
interface Setup { providers: Provider[]; hosts: Record<string, string> }
/** What happened to a channel last, shown under its form. `passing` marks a note that the next change makes untrue. */
interface Result { ok: boolean; text: string; busy?: boolean; passing?: boolean }

type IconKind = 'mail' | 'chat' | 'phone' | 'link'
const TYPES: Record<string, { label: string; name: string; icon: IconKind; about: string }> = {
  gmail: { label: 'Gmail', name: 'Gmail', icon: 'mail', about: 'Recommended. Send from any Gmail account with an app password.' },
  smtp: { label: 'Other SMTP server', name: 'SMTP server', icon: 'mail', about: 'A company mail relay, or any provider that accepts SMTP sign-in.' },
  ntfy: { label: 'Phone push (ntfy)', name: 'Phone push (ntfy)', icon: 'phone', about: 'Free push notifications to Android and iPhone. No account needed.' },
  teams: { label: 'Microsoft Teams', name: 'Microsoft Teams', icon: 'chat', about: 'Post to a Teams channel through a Workflows webhook.' },
  slack: { label: 'Slack', name: 'Slack', icon: 'chat', about: 'Post to a Slack channel through an incoming webhook.' },
  discord: { label: 'Discord', name: 'Discord', icon: 'chat', about: 'Post to a Discord channel through a webhook.' },
  telegram: { label: 'Telegram', name: 'Telegram', icon: 'chat', about: 'Message a Telegram chat or group through a bot.' },
  webhook: { label: 'Webhook (JSON)', name: 'Webhook', icon: 'link', about: 'POST to any URL: RMM and PSA tools, Power Automate, n8n, on-call services.' },
  microsoft: { label: 'Outlook.com / Microsoft 365', name: 'Outlook.com / Microsoft 365', icon: 'mail', about: '' },
}
const PROVIDER_ABOUT: Record<string, string> = {
  yahoo: "Send from a Yahoo account with an app password. Brand-new accounts can't create one for a few days.",
  icloud: 'Send from an @icloud.com address with an app-specific password.',
}
const OTHER_TYPES = ['ntfy', 'teams', 'slack', 'discord', 'telegram', 'webhook']

const DEFAULTS: Record<string, object> = {
  gmail: { authMode: 'AppPassword', account: '', appPassword: '', recipients: '' },
  smtp: { provider: '', host: '', port: 587, security: 'Auto', username: '', password: '', from: '', recipients: '' },
  teams: { webhookUrl: '' }, slack: { webhookUrl: '' }, discord: { webhookUrl: '' },
  ntfy: { serverUrl: 'https://ntfy.sh', topic: '', accessToken: '' },
  telegram: { botToken: '', chatId: '' },
  webhook: { url: '', headerName: '', headerValue: '' },
}

const WEBHOOK_STEPS: Record<string, string> = {
  teams: '1. In Teams, open the channel, click ⋯ (More options) → Workflows.\n'
    + '2. Choose the template "Send webhook alerts to a channel" (or "Post to a channel when a webhook request is received").\n'
    + '3. Finish the wizard and copy the URL it shows. Paste it below.',
  slack: '1. Go to api.slack.com/apps, sign in and click Create an App → Blank app. Name it (e.g. Traceline), pick your workspace and click Continue.\n'
    + '2. Under "What it can do in Slack?" search for incoming-webhook, click it, then click Continue. Click Continue again on the events step without adding any.\n'
    + '3. Click Create and Install, pick the channel for the alerts and click Allow.\n'
    + '4. Click Go to App Settings → Incoming Webhooks. In the Webhook URL table, click Copy on the channel\'s row (not the Copy under the curl example). Paste it below.',
  discord: '1. In Discord, open Server Settings → Integrations → Webhooks → New Webhook.\n'
    + '2. Pick the channel, then click Copy Webhook URL. Paste it below.',
}

const str = (c: Channel, key: string) => String(c[key] ?? '')
/** A secret the server has already encrypted. The page never sees what is inside, and sends it back as it is. */
const isStored = (value: string) => value.startsWith('dpapi:') || value.startsWith('aes:')
const same = (a: Channel, b: Channel) => JSON.stringify(a) === JSON.stringify(b)
const plural = (n: number) => n === 1 ? '1 channel' : `${n} channels`
const presetOf = (c: Channel, setup: Setup) => c.type === 'smtp' ? setup.providers.find(p => p.id === str(c, 'provider').toLowerCase()) : undefined
const kindOf = (c: Channel, setup: Setup) => presetOf(c, setup)?.name ?? (c.type === 'smtp' ? 'SMTP server' : TYPES[c.type]?.label ?? c.type)

/** The line under a channel's name in the list: the account, topic or chat it sends to. */
function summaryOf(c: Channel, setup: Setup) {
  const address = (value: string) => {
    if (value.trim() === '') return 'Not configured'
    if (isStored(value)) return setup.hosts[c.id] ? `${setup.hosts[c.id]}/…` : 'Configured'
    try { return `${new URL(value.trim()).hostname}/…` } catch { return 'Configured' }
  }
  switch (c.type) {
    case 'gmail': return c.authMode === 'OAuth' ? 'Signed in with Google: cannot send' : str(c, 'account') || 'Not configured'
    case 'microsoft': return 'Signed in with Microsoft: cannot send'
    case 'smtp': return presetOf(c, setup) ? str(c, 'username') || 'Not configured' : str(c, 'host') ? `${str(c, 'host')}:${str(c, 'port')}` : 'Not configured'
    case 'ntfy': return str(c, 'topic') ? `Topic ${str(c, 'topic')}` : 'No topic yet'
    case 'telegram': return str(c, 'chatId') ? `Chat ${str(c, 'chatId')}` : 'Not configured'
    case 'webhook': return address(str(c, 'url'))
    default: return address(str(c, 'webhookUrl'))
  }
}

/** On the public ntfy.sh server anyone who knows the topic can read it, so the name has to be hard to guess. */
function newTopic(machine: string) {
  const letters = 'abcdefghijkmnopqrstuvwxyz23456789'
  const tail = Array.from(crypto.getRandomValues(new Uint32Array(12)), n => letters[n % letters.length]).join('')
  const site = machine.toLowerCase().replace(/[^a-z0-9]/g, '').slice(0, 12)
  return `traceline-${site ? `${site}-` : ''}${tail}`
}

function ChannelIcon({ kind }: { kind: IconKind }) {
  const shapes: Record<IconKind, ReactNode> = {
    mail: <><rect x="3" y="5" width="18" height="14" rx="2" /><path d="m3.5 7 8.5 6.5L20.5 7" /></>,
    chat: <path d="M4 4.5h16A1.5 1.5 0 0 1 21.5 6v10a1.5 1.5 0 0 1-1.5 1.5h-9.5L6 21v-3.5H4A1.5 1.5 0 0 1 2.5 16V6A1.5 1.5 0 0 1 4 4.5Z" />,
    phone: <><rect x="7" y="2.5" width="10" height="19" rx="2" /><path d="M11 18.5h2" /></>,
    link: <><path d="M10.5 13.5a4 4 0 0 0 5.7 0l2.8-2.8a4 4 0 0 0-5.7-5.7l-1 1" /><path d="M13.5 10.5a4 4 0 0 0-5.7 0L5 13.3a4 4 0 0 0 5.7 5.7l1-1" /></>,
  }
  return <svg className="channel-icon" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">{shapes[kind]}</svg>
}

function Steps({ title, text, children }: { title: string; text: string; children?: ReactNode }) {
  return <div className="steps"><strong>{title}</strong><div className="lines">{text}</div>{children && <div className="row">{children}</div>}</div>
}

const Outside = ({ href, children }: { href: string; children: ReactNode }) => <a className="button" href={href} target="_blank" rel="noreferrer">{children}</a>

export function ChannelsPanel({ channels, state, reload }: { channels: Channel[]; state: AppState; reload: () => Promise<void> }) {
  const [setup, setSetup] = useState<Setup>({ providers: [], hosts: {} })
  // Changes that are not saved yet, by channel id. A channel that is new exists only here until it is saved.
  const [drafts, setDrafts] = useState<Record<string, Channel>>({})
  const [selected, setSelected] = useState<string | null>(null)
  const [pane, setPane] = useState<'edit' | 'add' | 'export' | 'import'>('edit')
  const [results, setResults] = useState<Record<string, Result>>({})
  const [notice, setNotice] = useState<Result | null>(null)
  const [importing, setImporting] = useState<{ text: string; password: string; about: string; error?: string } | null>(null)
  const [exporting, setExporting] = useState<{ password: string; second: string; error?: string }>({ password: '', second: '' })

  useEffect(() => {
    let alive = true
    api<Setup>('/channels/setup').then(s => { if (alive) setSetup(s) }).catch(() => { /* the list then shows less, nothing else */ })
    return () => { alive = false }
  }, [channels])

  const isSaved = (id: string) => channels.some(c => c.id === id)
  const shown = [...channels.map(c => drafts[c.id] ?? c), ...Object.values(drafts).filter(d => !isSaved(d.id))]
  const current = shown.find(c => c.id === selected) ?? shown[0] ?? null
  const view = pane === 'edit' && !current ? 'add' : pane
  const forget = (id: string) => setDrafts(d => { const rest = { ...d }; delete rest[id]; return rest })
  const report = (id: string, result: Result | null) => setResults(r => { const rest = { ...r }; if (result) rest[id] = result; else delete rest[id]; return rest })
  const failed = (id: string, e: unknown) => report(id, { ok: false, text: (e as Error).message })
  const put = async (next: Channel[]) => { await api('/channels', 'PUT', next); await reload() }

  const edit = (c: Channel, patch: Record<string, unknown>) => {
    const next = { ...c, ...patch } as Channel
    const saved = channels.find(x => x.id === c.id)
    if (saved && same(saved, next)) forget(c.id); else setDrafts(d => ({ ...d, [c.id]: next }))
    if (results[c.id]?.passing) report(c.id, null)
  }
  const add = (type: string, provider?: Provider) => {
    const alike = shown.filter(c => c.type === type && (type !== 'smtp' || (presetOf(c, setup)?.id ?? '') === (provider?.id ?? ''))).length
    const name = provider?.name ?? TYPES[type].name
    const c = {
      type, id: newId(), name: alike > 0 ? `${name} ${alike + 1}` : name, enabled: true, ...DEFAULTS[type],
      ...(provider ? { provider: provider.id, host: provider.host, port: provider.port, security: provider.security } : {}),
    } as Channel
    setDrafts(d => ({ ...d, [c.id]: c }))
    setSelected(c.id); setPane('edit'); setNotice(null)
  }
  const save = async (c: Channel) => {
    if (c.name.trim() === '') { report(c.id, { ok: false, text: 'Give the channel a name first.' }); return }
    try {
      await put(isSaved(c.id) ? channels.map(x => x.id === c.id ? c : x) : [...channels, c])
      forget(c.id)
      report(c.id, { ok: true, text: 'Saved.', passing: true })
    } catch (e) { failed(c.id, e) }
  }
  // The tick takes effect at once, like in the list of alerts; other changes to the channel wait for Save.
  const enable = async (c: Channel, on: boolean) => {
    if (!isSaved(c.id)) { edit(c, { enabled: on }); return }
    try {
      await put(channels.map(x => x.id === c.id ? { ...x, enabled: on } : x))
      setDrafts(d => d[c.id] ? { ...d, [c.id]: { ...d[c.id], enabled: on } } : d)
    } catch (e) { failed(c.id, e) }
  }
  const remove = async (c: Channel) => {
    if (!confirm(`Remove the notification channel "${c.name}"?`)) return
    try {
      if (isSaved(c.id)) await put(channels.filter(x => x.id !== c.id))
      forget(c.id); report(c.id, null); setSelected(null)
    } catch (e) { failed(c.id, e) }
  }
  const test = async (c: Channel) => {
    report(c.id, { ok: true, busy: true, text: 'Sending a test message…' })
    try { report(c.id, await api<Result>('/channels/test', 'POST', c)) } catch (e) { failed(c.id, e) }
  }

  const chooseImport = async () => {
    const file = await pickFile('.smchannels,application/json')
    if (!file) return
    const text = await file.text()
    setNotice(null)
    try {
      const head = JSON.parse(text) as { format?: string; channels?: number; machine?: string; exported?: string }
      if (head.format !== 'ServiceMonitor.Channels') throw new Error()
      setImporting({ text, password: '', about: `${plural(head.channels ?? 0)} exported${head.machine ? ` from ${head.machine}` : ''}${head.exported ? ` on ${fmtTime(Date.parse(head.exported), true)}` : ''}.` })
      setPane('import')
    } catch { setNotice({ ok: false, text: 'Could not import: this is not a channel export file.' }) }
  }
  const runImport = async () => {
    if (!importing) return
    try {
      const r = await api<{ added: number; replaced: number; needSignIn: string[] }>('/channels/import', 'POST', { file: importing.text, password: importing.password })
      // Changes that were waiting on a channel the file may have replaced would undo the import when saved.
      setDrafts(d => Object.fromEntries(Object.entries(d).filter(([id]) => !isSaved(id))))
      setImporting(null); setPane('edit')
      await reload()
      setNotice({
        ok: r.needSignIn.length === 0,
        text: `Imported ${plural(r.added + r.replaced)}: ${r.added} new, ${r.replaced} replaced. Use Send test on each before relying on it.`
          + (r.needSignIn.length ? ` ${r.needSignIn.join(', ')} used a sign-in, which Traceline cannot send through; set ${r.needSignIn.length === 1 ? 'it' : 'them'} up again with an app password or SMTP.` : ''),
      })
    } catch (e) { setImporting({ ...importing, error: (e as Error).message }) }
  }
  const startExport = () => {
    if (channels.length === 0) { setNotice({ ok: false, text: 'There are no saved channels to export yet.' }); return }
    setNotice(null); setExporting({ password: '', second: '' }); setPane('export')
  }
  const runExport = async () => {
    if (exporting.password.length < 8) { setExporting({ ...exporting, error: 'Use at least 8 characters.' }); return }
    if (exporting.password !== exporting.second) { setExporting({ ...exporting, error: 'The two passwords are not the same.' }); return }
    try {
      const r = await api<{ name: string; file: string; count: number }>('/channels/export', 'POST', { password: exporting.password })
      download(r.name, r.file, 'application/json')
      setPane('edit')
      setNotice({ ok: true, text: `Exported ${plural(r.count)} to ${r.name}, in your Downloads folder.` })
    } catch (e) { setExporting({ ...exporting, error: (e as Error).message }) }
  }

  const tile = (type: string, provider?: Provider) => (
    <button key={provider?.id ?? type} type="button" className="type-tile" onClick={() => add(type, provider)}>
      <ChannelIcon kind={TYPES[type].icon} />
      <span><strong>{provider?.name ?? TYPES[type].label}</strong>
        <span className="muted small">{provider ? PROVIDER_ABOUT[provider.id] ?? `Send from a ${provider.name} account with an app password.` : TYPES[type].about}</span></span>
    </button>
  )

  return (
    <>
      <h2>Notification channels</h2>
      <p className="muted">Where alerts are sent. An alert goes to every enabled channel unless it names its own, and a channel that cannot be reached is tried again on its own for up to an hour and a half.</p>
      {notice && <div className={`banner ${notice.ok ? 'ok' : 'bad'}`}>{notice.text}</div>}
      <div className="channels">
        <div className="channel-list">
          <button type="button" className="primary" onClick={() => { setPane('add'); setNotice(null) }}>+ Add channel</button>
          {shown.map(c => {
            const summary = summaryOf(c, setup)
            const open = () => { setSelected(c.id); setPane('edit') }
            return (
              <div key={c.id} role="button" tabIndex={0} className={`channel-item ${view === 'edit' && current?.id === c.id ? 'on' : ''}`}
                onClick={open} onKeyDown={e => { if (e.key === 'Enter' && e.target === e.currentTarget) open() }}>
                <ChannelIcon kind={TYPES[c.type]?.icon ?? 'link'} />
                <div className="text">
                  <div><strong className="name">{c.name || kindOf(c, setup)}</strong>{c.id in drafts && <span className="tag">not saved</span>}</div>
                  <div className="muted small" title={summary}>{summary}</div>
                </div>
                <input type="checkbox" checked={c.enabled} title="Enabled" aria-label={`${c.name} enabled`} onClick={e => e.stopPropagation()} onChange={e => void enable(c, e.target.checked)} />
              </div>
            )
          })}
          {shown.length === 0 && <div className="muted small">No channels yet. Without one, alerts still appear here, on timelines and as browser notifications.</div>}
          <div className="pair">
            {state.role === 'admin' && <button type="button" onClick={startExport} title="Save all channels, with their passwords, to a password-protected file">Export…</button>}
            <button type="button" onClick={() => void chooseImport()} title="Add the channels from an exported file (.smchannels, from Traceline or Service Monitor)">Import…</button>
          </div>
        </div>

        {view === 'add' && <div className="card">
          <div className="title">Add a notification channel</div>
          <div className="group">Email</div>
          <div className="type-tiles">{tile('gmail')}{setup.providers.map(p => tile('smtp', p))}{tile('smtp')}</div>
          <div className="group">Push, chat and integrations</div>
          <div className="type-tiles">{OTHER_TYPES.map(type => tile(type))}</div>
          {current && <div><button type="button" onClick={() => setPane('edit')}>Cancel</button></div>}
        </div>}

        {view === 'export' && <form className="card" onSubmit={e => { e.preventDefault(); void runExport() }}>
          <div className="title">Choose a password for the file</div>
          <div className="muted">The file holds every channel with its passwords, tokens and webhook URLs, encrypted with this password. You need it to import the file, here or in Service Monitor. Sign-ins with Google or Microsoft are not included.</div>
          {Object.keys(drafts).length > 0 && <div className="banner warn">Changes that are not saved yet are not in the file.</div>}
          <div className="field"><span>Password</span>
            <input aria-label="Password" type="password" autoComplete="new-password" autoFocus value={exporting.password} onChange={e => setExporting({ ...exporting, password: e.target.value, error: undefined })} /></div>
          <div className="field"><span>Repeat the password</span>
            <input aria-label="Repeat the password" type="password" autoComplete="new-password" value={exporting.second} onChange={e => setExporting({ ...exporting, second: e.target.value, error: undefined })} /></div>
          {exporting.error && <div className="banner bad">{exporting.error}</div>}
          <div className="row"><button className="primary">Export</button><button type="button" onClick={() => setPane('edit')}>Cancel</button></div>
        </form>}

        {view === 'import' && importing && <form className="card" onSubmit={e => { e.preventDefault(); void runImport() }}>
          <div className="title">Enter the file's password</div>
          <div className="muted">{importing.about} Enter the password chosen when they were exported. A channel with the same name as one here replaces it.</div>
          <div className="field"><span>Password</span>
            <input aria-label="Password" type="password" autoComplete="off" autoFocus required value={importing.password} onChange={e => setImporting({ ...importing, password: e.target.value, error: undefined })} /></div>
          {importing.error && <div className="banner bad">{importing.error}</div>}
          <div className="row"><button className="primary">Import</button><button type="button" onClick={() => { setImporting(null); setPane('edit') }}>Cancel</button></div>
        </form>}

        {view === 'edit' && current && <ChannelForm key={current.id} c={current} setup={setup} machine={state.machine} isNew={!isSaved(current.id)} changed={current.id in drafts}
          result={results[current.id]} onEdit={patch => edit(current, patch)} onEnable={on => void enable(current, on)} onSave={() => void save(current)}
          onTest={() => void test(current)} onRemove={() => void remove(current)}
          onDiscard={() => { forget(current.id); report(current.id, null) }} />}
      </div>
    </>
  )
}

interface FieldOptions { secret?: boolean; address?: boolean; number?: boolean; noSpaces?: boolean; placeholder?: string; help?: ReactNode; after?: ReactNode; also?: Record<string, unknown> }

function ChannelForm({ c, setup, machine, isNew, changed, result, onEdit, onEnable, onSave, onTest, onRemove, onDiscard }: {
  c: Channel; setup: Setup; machine: string; isNew: boolean; changed: boolean; result?: Result
  onEdit: (patch: Record<string, unknown>) => void; onEnable: (on: boolean) => void; onSave: () => void; onTest: () => void; onRemove: () => void; onDiscard: () => void
}) {
  const [chat, setChat] = useState<Result | null>(null)
  // ntfy's less common settings start folded away unless the channel uses them.
  const [more, setMore] = useState(() => c.type === 'ntfy' && (!/^https:\/\/ntfy\.sh\/*$/i.test(str(c, 'serverUrl').trim() || 'https://ntfy.sh') || str(c, 'accessToken') !== ''))
  const kind = kindOf(c, setup)
  const preset = presetOf(c, setup)

  // A secret that is already saved stays on the server: its box is empty and says so, and typing in it replaces the secret.
  // A new one is hidden while it is typed, except an address, which is easier to check when it can be read.
  const field = (label: string, key: string, o: FieldOptions = {}) => {
    const value = str(c, key), stored = isStored(value)
    const input = <input aria-label={label} type={o.number ? 'number' : 'text'} className={o.secret && !o.address ? 'secret' : undefined} autoComplete="off" spellCheck={false}
      value={stored ? '' : value} placeholder={stored ? 'Saved. Type a new one to replace it.' : o.placeholder}
      onChange={e => onEdit({ ...o.also, [key]: o.number ? Number(e.target.value) : o.noSpaces ? e.target.value.replace(/ /g, '') : e.target.value })} />
    return <div className="field"><span>{label}</span>{o.after ? <div className="with-button">{input}{o.after}</div> : input}{o.help && <span className="muted small">{o.help}</span>}</div>
  }
  const recipients = field('Send alerts to', 'recipients', { placeholder: 'tech1@example.com, oncall@example.com', help: 'Separate addresses with commas. Leave empty to send to the sending account itself.' })
  const kept = 'Kept encrypted on the computer Traceline runs on.'

  const findChat = async () => {
    setChat({ ok: true, busy: true, text: 'Asking Telegram…' })
    try {
      const found = await api<{ ok: boolean; chatId: string; text: string }>('/channels/telegram-chat', 'POST', { botToken: str(c, 'botToken') })
      if (found.ok) onEdit({ chatId: found.chatId })
      setChat(found)
    } catch (e) { setChat({ ok: false, text: (e as Error).message }) }
  }

  const details = () => {
    switch (c.type) {
      case 'gmail': return <>
        {c.authMode === 'OAuth' && <div className="banner warn">This channel signed in with Google in Service Monitor, which Traceline cannot do. Enter an app password below and it sends with that instead.</div>}
        <Steps title="Create an app password" text={'1. In a browser, sign in to the Gmail account that will send the alerts.\n2. Turn on 2-Step Verification (Google requires it for app passwords).\n'
          + '3. Open App passwords, type a name such as "Traceline" and click Create.\n4. Copy the 16-letter password Google shows and paste it below.'}>
          <Outside href="https://myaccount.google.com/signinoptions/twosv">Open 2-Step Verification</Outside>
          <Outside href="https://myaccount.google.com/apppasswords">Open App passwords</Outside>
        </Steps>
        {field('Gmail address', 'account', { placeholder: 'site.alerts@gmail.com' })}
        {field('App password', 'appPassword', { secret: true, noSpaces: true, also: { authMode: 'AppPassword' }, help: `${kept} Spaces are removed automatically.` })}
        {recipients}
      </>
      case 'microsoft': return <div className="banner warn">This channel signs in with Microsoft in Service Monitor, which Traceline cannot do, so nothing can be sent through it here. Add Gmail, another mail provider or a chat channel instead.</div>
      case 'smtp': return <>
        <div className="field"><span>Email provider</span>
          <select aria-label="Email provider" value={preset?.id ?? ''} onChange={e => {
            const p = setup.providers.find(x => x.id === e.target.value)
            onEdit(p ? { provider: p.id, host: p.host, port: p.port, security: p.security, from: '' } : { provider: '' })
          }}>
            {setup.providers.map(p => <option key={p.id} value={p.id}>{p.name}</option>)}
            <option value="">Other SMTP server</option>
          </select></div>
        {preset ? <>
          <Steps title="Create an app password" text={preset.instructions}><Outside href={preset.accountPageUrl}>{preset.accountPageButton}</Outside></Steps>
          {field(`${preset.name} address`, 'username', { placeholder: preset.addressExample })}
          {field('App password', 'password', { secret: true, noSpaces: true, help: `Sends through ${preset.host}, port ${preset.port}. ${kept} Spaces are removed automatically.` })}
        </> : <>
          <div className="field-row">
            {field('SMTP server', 'host', { placeholder: 'smtp.example.com' })}
            <div className="short">{field('Port', 'port', { number: true })}</div>
            <div className="field medium"><span>Encryption</span>
              <select aria-label="Encryption" value={str(c, 'security')} onChange={e => onEdit({ security: e.target.value })}>
                <option value="Auto">Auto</option><option value="StartTls">STARTTLS</option><option value="SslOnConnect">SSL</option><option value="None">None</option>
              </select></div>
          </div>
          <div className="muted small">Auto picks the right encryption for the port (587 = STARTTLS, 465 = SSL). Use None only for an internal relay.</div>
          {field("User name (leave empty if the server doesn't need sign-in)", 'username')}
          {field('Password', 'password', { secret: true, help: kept })}
          {field('From address', 'from', { placeholder: 'Same as user name' })}
        </>}
        {recipients}
      </>
      case 'ntfy': {
        const server = (str(c, 'serverUrl').trim() || 'https://ntfy.sh').replace(/\/+$/, '')
        const topic = str(c, 'topic').trim()
        return <>
          <Steps title="Get alerts as push notifications on your phone" text={'1. Install the free ntfy app (Google Play, F-Droid or the App Store).\n2. Click Generate below to create a private topic name.\n'
            + '3. In the app tap +, enter the topic name and subscribe. Everyone who should get the alerts can subscribe to the same topic.'} />
          {field('Topic', 'topic', {
            placeholder: 'traceline-yoursite-…', after: <button type="button" onClick={() => onEdit({ topic: newTopic(machine) })}>Generate</button>,
            help: 'On the public ntfy.sh server anyone who knows the topic can read it, so use the generated, hard-to-guess name.',
          })}
          {topic !== '' && /^https?:\/\//i.test(server) && <div className="small">Or subscribe in a web browser: <a href={`${server}/${encodeURIComponent(topic)}`} target="_blank" rel="noreferrer">{server}/{topic}</a></div>}
          <details open={more} onToggle={e => setMore(e.currentTarget.open)}>
            <summary>Self-hosted server or protected topic</summary>
            <div className="inner">
              {field('Server', 'serverUrl', { placeholder: 'https://ntfy.sh' })}
              {field('Access token (optional)', 'accessToken', { secret: true, help: kept })}
            </div>
          </details>
        </>
      }
      case 'telegram': return <>
        <Steps title="Create a Telegram bot" text={'1. In Telegram, open a chat with @BotFather, send /newbot and follow the prompts.\n2. Paste the bot token it gives you below.\n'
          + '3. Send any message to your new bot (or add it to a group and post there).\n4. Click Find chat ID.'} />
        {field('Bot token', 'botToken', { secret: true, help: kept })}
        {field('Chat ID', 'chatId', {
          after: <button type="button" disabled={chat?.busy} onClick={() => void findChat()}>Find chat ID</button>,
          help: chat && <span className={chat.ok ? undefined : 'lossy'}>{chat.text}</span>,
        })}
      </>
      case 'webhook': return <>
        <div className="muted">Each notification is sent as an HTTP POST with a JSON body (id, kind, severity, title, summary, subject, machine, time, facts and more). Use it to feed RMM and PSA tools, Power Automate, n8n, Zapier or an on-call service.</div>
        {field('URL', 'url', { secret: true, address: true, placeholder: 'https://…', help: kept })}
        <div className="field-row">
          {field('Header name (optional)', 'headerName', { placeholder: 'Authorization' })}
          {field('Header value', 'headerValue', { secret: true })}
        </div>
      </>
      default: return <>
        <Steps title="Get the webhook URL" text={WEBHOOK_STEPS[c.type] ?? ''} />
        {field('Webhook URL', 'webhookUrl', { secret: true, address: true, placeholder: 'https://…', help: 'Anyone with this URL can post to the channel, so it is kept encrypted on the computer Traceline runs on.' })}
      </>
    }
  }

  return (
    <div className="card">
      <div className="head">
        <div className="grow"><div className="muted small">{kind}</div><div className="title">{c.name || kind}</div></div>
        <button type="button" onClick={onRemove} title="Remove this channel">Remove</button>
      </div>
      <div className="field-row">
        <div className="field"><span>Channel name</span><input aria-label="Channel name" value={c.name} autoComplete="off" onChange={e => onEdit({ name: e.target.value })} /></div>
        <label className="check"><input type="checkbox" checked={c.enabled} onChange={e => onEnable(e.target.checked)} /> Enabled</label>
      </div>
      {details()}
      <hr />
      <div className="row">
        <button type="button" className="primary" disabled={!changed} onClick={onSave}>{isNew ? 'Save channel' : changed ? 'Save changes' : 'Saved'}</button>
        <button type="button" disabled={result?.busy} onClick={onTest}>Send test</button>
        {changed && <button type="button" className="link" onClick={onDiscard}>{isNew ? 'Cancel' : 'Undo changes'}</button>}
        <span className="muted small">Send test goes through this channel only. You can test before saving.</span>
      </div>
      {result && <div className={`banner ${result.busy ? 'info' : result.ok ? 'ok' : 'bad'}`}>{result.text}</div>}
    </div>
  )
}
