import { useEffect, useRef, useState } from 'react'

/** One line of a menu. With `items` it opens a second menu beside it instead of doing something itself. */
export interface MenuItem { label?: string; onClick?: () => void; disabled?: boolean; checked?: boolean; separator?: boolean; items?: MenuItem[] }

// What a line and a dividing rule take up in a menu, in pixels, to keep menus inside the window.
const heightOf = (items: MenuItem[]) => items.reduce((h, i) => h + (i.separator ? 11 : 33), 0)

/** A small pop-up menu at a screen position (right-click menus and drop-downs). */
export function PopupMenu({ at, items, onClose }: { at: { x: number; y: number }; items: MenuItem[]; onClose: () => void }) {
  const [open, setOpen] = useState<number | null>(null)
  // The page behind the menu redraws all the time and hands over a new onClose each time. The listeners are set up
  // once all the same: setting them up again on every redraw left moments in which a click or Escape went unheard.
  const closer = useRef(onClose)
  closer.current = onClose
  const shown = useRef(items)
  shown.current = items
  useEffect(() => {
    let alive = true, unheard: MouseEvent | null = null
    const close = () => closer.current()
    const key = (e: KeyboardEvent) => { if (e.key === 'Escape') close() }
    // Any click closes the menu, except on a line marked "stay" (one that opens a second menu, or is switched off).
    const stays = (e: MouseEvent) => e.target instanceof Element && e.target.closest('.menu-list .stay') !== null
    const click = (e: MouseEvent) => { unheard = null; if (!stays(e)) close() }
    // Some things on a page keep their clicks to themselves (a tick box in a table row), so such a click never gets
    // as far as the listener above. It is noticed here on its way in, and if it was not heard on its way out, the
    // menu closes a moment later, unless another menu has taken this one's place by then.
    const clickStarts = (e: MouseEvent) => {
      if (stays(e)) return
      const menu = shown.current
      unheard = e
      setTimeout(() => { if (alive && unheard === e && shown.current === menu) close() })
    }
    // Registered after the opening click has finished, so that click does not close the menu again.
    // A right-click elsewhere closes this menu first (capture phase), so that the menu it opens there survives.
    const timer = setTimeout(() => {
      window.addEventListener('click', click); window.addEventListener('click', clickStarts, true)
      window.addEventListener('contextmenu', close, true); window.addEventListener('keydown', key)
    })
    return () => {
      alive = false
      clearTimeout(timer)
      window.removeEventListener('click', click); window.removeEventListener('click', clickStarts, true)
      window.removeEventListener('contextmenu', close, true); window.removeEventListener('keydown', key)
    }
  }, [])
  const left = Math.max(8, Math.min(at.x, window.innerWidth - 290))
  const top = Math.max(8, Math.min(at.y, window.innerHeight - 22 - heightOf(items)))
  // The second menu opens to the right of its line, or to the left when there is no room; it moves up as far as it must to fit.
  const beside = left + 550 > window.innerWidth ? 'left' : ''
  const lift = (i: number, sub: MenuItem[]) => Math.max(0, top + heightOf(items.slice(0, i)) + heightOf(sub) + 22 - window.innerHeight)

  const lines = (list: MenuItem[], nested: boolean) => {
    const ticks = list.some(i => i.checked !== undefined)
    return list.map((item, i) => item.separator
      ? <hr key={i} />
      : item.items && !nested
        ? <div key={i} className="sub" onMouseEnter={() => setOpen(i)}>
          <a className={`stay ${open === i ? 'on' : ''}`} onClick={() => setOpen(i)}>
            {ticks && <span className="tick" />}{item.label}<span className="more">›</span>
          </a>
          {open === i && <div className={`menu-list ${beside}`} style={{ top: -7 - lift(i, item.items) }}>{lines(item.items, true)}</div>}
        </div>
        : <a key={i} className={item.disabled ? 'disabled stay' : ''} onClick={() => { if (!item.disabled) item.onClick?.() }}>
          {ticks && <span className="tick">{item.checked ? '✓' : ''}</span>}{item.label}
        </a>)
  }
  return <div className="menu-list popup" style={{ left, top }}>{lines(items, false)}</div>
}

/** Shows what went wrong, or that something worked. */
export function Banner({ message }: { message: { ok: boolean; text: string } | null }) {
  return message ? <div className={`banner ${message.ok ? 'ok' : 'bad'}`}>{message.text}</div> : null
}

// Keyboard shortcuts. A page says what a key does for as long as it is shown; one listener (in App) looks the key up.
// Traceline's own window passes every one of them on. A web browser keeps some for itself (Ctrl+N opens a browser window).
const shortcuts = new Map<string, () => void>()

/** The keys this page listens for, for the Help page: what to press and what it does. */
export const SHORTCUTS: [string, string][] = [
  ['Ctrl+N', 'New trace: the "+" tab on Targets, with the cursor in the target box'],
  ['Ctrl+L', 'Load a list of targets from a text file'],
  ['Ctrl+F', 'Find a target in the list'],
  ['Ctrl+O', 'Import a session file'],
  ['Ctrl+S', "On a target's page: save everything it recorded as a session file"],
  ['Ctrl+Shift+C', "On a target's page: copy the picture of the trace"],
  ['Ctrl+Alt+C', "On a target's page: copy the route as text"],
]

/** "ctrl+shift+c" for a key press with Ctrl held, or null for any other. Goes by the key's place, so it is the same on every keyboard layout. */
export function shortcutOf(e: KeyboardEvent) {
  if (!(e.ctrlKey || e.metaKey) || !e.code.startsWith('Key')) return null
  return `ctrl+${e.shiftKey ? 'shift+' : ''}${e.altKey ? 'alt+' : ''}${e.code.slice(3).toLowerCase()}`
}

/** Does what the page shown has put on this key. False when nothing has. */
export function runShortcut(key: string) {
  const run = shortcuts.get(key)
  run?.()
  return run !== undefined
}

/** While this component is shown (and `on` holds), `key` (as in "ctrl+shift+c") does `run`. */
export function useShortcut(key: string, run: () => void, on = true) {
  const latest = useRef(run)
  latest.current = run
  useEffect(() => {
    if (!on) return
    const call = () => latest.current()
    shortcuts.set(key, call)
    return () => { if (shortcuts.get(key) === call) shortcuts.delete(key) }
  }, [key, on])
}
