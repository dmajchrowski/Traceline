import { useEffect, useState } from 'react'
import { api, download, fmt, newId, type AppState, type Device, type SummaryDef } from './api'
import { Banner } from './ui'

/** Local network discovery: find the devices on this network and, if wanted, start tracing them. */
export function DiscoveryPage({ state, onChanged }: { state: AppState; onChanged: () => void }) {
  const [subnets, setSubnets] = useState<{ cidr: string; address: string; interface: string }[]>([])
  const [subnet, setSubnet] = useState('')
  const [methods, setMethods] = useState({ ping: true, arp: true, mdns: true, ssdp: true })
  const [devices, setDevices] = useState<Device[] | null>(null)
  const [picked, setPicked] = useState<string[]>([])
  const [scanning, setScanning] = useState(false)
  const [message, setMessage] = useState<{ ok: boolean; text: string } | null>(null)

  useEffect(() => {
    api<typeof subnets>('/discovery/subnets').then(list => { setSubnets(list); setSubnet(s => s || (list[0]?.cidr ?? '')) }).catch(() => { })
  }, [])

  const scan = async () => {
    setScanning(true); setMessage(null); setPicked([])
    try { setDevices(await api<Device[]>('/discovery/scan', 'POST', { subnet, ...methods })) }
    catch (e) { setMessage({ ok: false, text: (e as Error).message }) }
    setScanning(false)
  }
  const exportCsv = () => {
    const q = (v: string | number | null) => `"${String(v ?? '').replace(/"/g, '""')}"`
    const lines = ['IP address,MAC address,Vendor,Name,Ping ms,Found by,Description',
      ...(devices ?? []).map(d => [d.ip, d.mac, d.vendor, d.name, d.pingMs, d.methods.join(' '), d.description].map(q).join(','))]
    download('local-network.csv', '﻿' + lines.join('\r\n'), 'text/csv')
  }
  const addToSummary = async (ips: string[]) => {
    if (ips.length === 0) return
    try {
      // Devices on the local network are one hop away, so tracing the route adds nothing: watch the device itself.
      const added: { id: number }[] = []
      for (const ip of ips) {
        try {
          const [target] = await api<{ id: number }[]>('/targets', 'POST', { input: ip, intervalMs: 5000 })
          await api(`/targets/${target.id}`, 'PATCH', { finalHopOnly: true, name: devices?.find(d => d.ip === ip)?.name || ip })
          added.push(target)
        } catch { /* already traced */ }
      }
      const defs = await api<SummaryDef[]>('/summaries')
      const name = 'Local network discovery'
      const existing = defs.find(d => d.name === name)
      const items = added.map(a => ({ targetId: a.id, hop: 0 }))
      await api('/summaries', 'PUT', existing
        ? defs.map(d => d === existing ? { ...d, items: [...d.items, ...items] } : d)
        : [...defs, { id: newId(), name, workspaceId: state.activeWorkspace, items }])
      setMessage({ ok: true, text: `${added.length} device(s) added to the "${name}" summary on the Targets page${added.length < ips.length ? `; ${ips.length - added.length} were already being traced` : ''}.` })
      onChanged()
    } catch (e) { setMessage({ ok: false, text: (e as Error).message }) }
  }
  const getVendors = async () => {
    setMessage({ ok: true, text: 'Downloading the vendor list…' })
    try {
      const r = await api<{ vendors: number }>('/discovery/vendors', 'POST')
      setMessage({ ok: true, text: `Vendor list saved (${r.vendors.toLocaleString()} entries). Scan again to see vendors.` }); onChanged()
    } catch (e) { setMessage({ ok: false, text: (e as Error).message }) }
  }
  const toggle = (key: keyof typeof methods) => setMethods(m => ({ ...m, [key]: !m[key] }))

  return (
    <div className="page">
      <Banner message={message} />
      <h2>Local network discovery</h2>
      <p className="muted">Finds the devices on a network this computer is connected to. A scan takes about ten seconds.</p>
      <div className="toolbar">
        <label>Network
          <input list="subnets" value={subnet} onChange={e => setSubnet(e.target.value)} placeholder="192.168.1.0/24" />
          <datalist id="subnets">{subnets.map(s => <option key={s.cidr} value={s.cidr}>{s.interface}</option>)}</datalist>
        </label>
        <label className="check"><input type="checkbox" checked={methods.ping} onChange={() => toggle('ping')} /> Ping sweep</label>
        <label className="check"><input type="checkbox" checked={methods.arp} onChange={() => toggle('arp')} /> ARP</label>
        <label className="check"><input type="checkbox" checked={methods.mdns} onChange={() => toggle('mdns')} /> mDNS / Bonjour</label>
        <label className="check"><input type="checkbox" checked={methods.ssdp} onChange={() => toggle('ssdp')} /> UPnP / SSDP</label>
        <button className="primary" disabled={scanning} onClick={scan}>{scanning ? 'Scanning…' : 'Start scan'}</button>
      </div>
      {!state.vendorList && state.role === 'admin' && <div className="muted small">
        Vendor names need the IEEE's public list of hardware-address prefixes (about 6 MB), which is not included. <button className="link" onClick={getVendors}>Download it from standards-oui.ieee.org now</button>
      </div>}

      {devices && <>
        <div className="toolbar" style={{ marginTop: 12 }}>
          <strong>{devices.length} device{devices.length === 1 ? '' : 's'}</strong>
          <button onClick={exportCsv} disabled={devices.length === 0}>Export to CSV</button>
          <button onClick={() => addToSummary(picked)} disabled={picked.length === 0}>Add selected to summary</button>
          <button onClick={() => addToSummary(devices.map(d => d.ip))} disabled={devices.length === 0}>Add all to summary</button>
        </div>
        <div className="scroll-x"><table className="plain">
          <thead><tr>
            <th><input type="checkbox" checked={picked.length > 0 && picked.length === devices.length} onChange={e => setPicked(e.target.checked ? devices.map(d => d.ip) : [])} /></th>
            <th className="left">IP address</th><th className="left">MAC address</th><th className="left">Vendor</th><th className="left">Name</th><th>Ping</th><th className="left">Found by</th><th className="left wide">Description</th></tr></thead>
          <tbody>
            {devices.map(d => (
              <tr key={d.ip}>
                <td><input type="checkbox" checked={picked.includes(d.ip)} onChange={e => setPicked(p => e.target.checked ? [...p, d.ip] : p.filter(ip => ip !== d.ip))} /></td>
                <td className="left mono">{d.ip}</td><td className="left mono">{d.mac}</td><td className="left">{d.vendor}</td><td className="left">{d.name}</td>
                <td>{d.pingMs == null ? '' : `${fmt(d.pingMs)} ms`}</td><td className="left">{d.methods.join(', ')}</td><td className="left wide">{d.description}</td>
              </tr>
            ))}
            {devices.length === 0 && <tr><td className="muted left" colSpan={8}>Nothing answered on that network.</td></tr>}
          </tbody>
        </table></div>
      </>}
    </div>
  )
}
