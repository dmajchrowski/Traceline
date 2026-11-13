// Sample times come from the server's clock. When the browser runs on another computer whose clock
// is a little off, "now" must still mean the server's now, or the live edge of the graphs would be wrong.

let skew = 0

export function setServerTime(serverMs: number) { skew = serverMs - Date.now() }

export function serverNow() { return Date.now() + skew }
