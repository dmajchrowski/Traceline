import { useEffect, useState, type ReactNode } from 'react'
import type { AppState } from './api'
import { SHORTCUTS } from './ui'

// The Help page: what this copy is, the keyboard shortcuts, and the user guide that was built into it.

/** `code` and **bold** inside a line of the guide. */
function inline(text: string): ReactNode[] {
  return text.split(/(`[^`]+`|\*\*[^*]+\*\*)/).map((part, i) =>
    part.length > 2 && part.startsWith('`') ? <code key={i}>{part.slice(1, -1)}</code>
      : part.length > 4 && part.startsWith('**') ? <strong key={i}>{inline(part.slice(2, -2))}</strong>
        : part)
}

const cells = (row: string) => row.trim().replace(/^\||\|$/g, '').split('|').map(c => c.trim())
const idOf = (title: string) => `guide-${title.toLowerCase().replace(/[^a-z0-9]+/g, '-')}`

/**
 * The guide is written in Markdown, and uses little of it: headings, paragraphs, tables, lists with "- ", blocks of
 * code between ``` lines, `code` and **bold**. This turns just that into a page; anything else shows as it is written.
 */
function renderGuide(markdown: string) {
  const blocks: ReactNode[] = [], sections: string[] = []
  const lines = markdown.replace(/\r/g, '').split('\n')
  const startsBlock = (line: string) => /^(#{1,3} |\||- |```)/.test(line)
  for (let i = 0; i < lines.length;) {
    const line = lines[i], key = blocks.length
    const heading = /^(#{1,3}) (.*)$/.exec(line)
    if (line.trim() === '') i++
    else if (heading) {
      const title = heading[2].trim()
      // The first heading is the guide's own title; the page has one already.
      if (heading[1].length === 2) { sections.push(title); blocks.push(<h3 key={key} id={idOf(title)}>{inline(title)}</h3>) }
      else if (heading[1].length === 3) blocks.push(<h4 key={key}>{inline(title)}</h4>)
      i++
    } else if (line.startsWith('```')) {
      const code: string[] = []
      for (i++; i < lines.length && !lines[i].startsWith('```'); i++) code.push(lines[i])
      i++
      blocks.push(<pre key={key}><code>{code.join('\n')}</code></pre>)
    } else if (line.startsWith('|')) {
      const rows: string[][] = []
      for (; i < lines.length && lines[i].startsWith('|'); i++) rows.push(cells(lines[i]))
      const ruled = rows.length > 1 && rows[1].every(c => /^:?-+:?$/.test(c))
      const body = ruled ? rows.slice(2) : rows
      blocks.push(<div key={key} className="scroll-x"><table>
        {ruled && <thead><tr>{rows[0].map((c, n) => <th key={n}>{inline(c)}</th>)}</tr></thead>}
        <tbody>{body.map((row, r) => <tr key={r}>{row.map((c, n) => <td key={n}>{inline(c)}</td>)}</tr>)}</tbody>
      </table></div>)
    } else if (line.startsWith('- ')) {
      const items: string[] = []
      for (; i < lines.length && lines[i].startsWith('- '); i++) items.push(lines[i].slice(2))
      blocks.push(<ul key={key}>{items.map((item, n) => <li key={n}>{inline(item)}</li>)}</ul>)
    } else {
      const text: string[] = []
      for (; i < lines.length && lines[i].trim() !== '' && !startsBlock(lines[i]); i++) text.push(lines[i].trim())
      blocks.push(<p key={key}>{inline(text.join(' '))}</p>)
    }
  }
  return { blocks, sections }
}

export function HelpPage({ state }: { state: AppState }) {
  const [guide, setGuide] = useState<string | null>(null)
  const [error, setError] = useState('')
  useEffect(() => {
    let live = true
    fetch('/api/guide').then(async r => {
      if (!r.ok) throw new Error(r.status === 404 ? 'This build has no guide in it.' : `The guide could not be loaded (${r.status}).`)
      const text = await r.text()
      if (live) setGuide(text)
    }).catch(e => { if (live) setError((e as Error).message) })
    return () => { live = false }
  }, [])
  const { blocks, sections } = guide ? renderGuide(guide) : { blocks: [], sections: [] }

  return (
    <div className="page narrow guide">
      <h2>Help</h2>
      <div className="card">
        <div><strong>Traceline {state.version}</strong></div>
        <div className="muted">Continuous route tracing with latency and packet loss graphs for every hop. Free and open source under the MIT License.</div>
        {state.dataFolder && <div className="muted small">What it records and its settings are kept in <code>{state.dataFolder}</code> on {state.machine}.</div>}
      </div>

      <h3>Keyboard shortcuts</h3>
      <table><tbody>{SHORTCUTS.map(([key, what]) => <tr key={key}><td className="nowrap"><code>{key}</code></td><td>{what}</td></tr>)}</tbody></table>
      <p className="muted small">They all work in Traceline's own window. A web browser keeps some of these keys for itself: Ctrl+N there opens a browser window.</p>

      <h2>User guide</h2>
      {error && <div className="banner bad">{error}</div>}
      {!guide && !error && <div className="muted">Loading the guide…</div>}
      {sections.length > 0 && <ul className="contents">{sections.map(title =>
        <li key={title}><button className="link" onClick={() => document.getElementById(idOf(title))?.scrollIntoView({ behavior: 'smooth' })}>{title}</button></li>)}</ul>}
      {blocks}
    </div>
  )
}
