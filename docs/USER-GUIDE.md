# Traceline user guide

## Targets

Enter a host name or an IP address on the **Targets** page and press **Start**. Several can be added at once, separated by commas.

| You type | What is measured |
|---|---|
| `example.com` or `203.0.113.7` | The route to the host, by ping, hop by hop |
| `ipv4:example.com`, `ipv6:example.com` | The same, forcing one protocol |
| `tcp://example.com:443` | Time to open a TCP connection to that port |
| `https://example.com/health` | Time until the web server starts answering |
| `dns://example.com` | Time for your DNS server to look the name up |
| `dns://example.com@1.1.1.1` | The same, asking a specific DNS server |

The last four are single measurements with no route. They are useful beside a ping trace: if ping is fine and HTTP is slow, the network is not the problem.

A target entered by name is traced at the address the name has, and the name is looked up again every five minutes. While the name cannot be looked up, for instance because the computer is switched off, Traceline keeps tracing the last address it had, so the outage shows as loss. That also holds after Traceline or the computer restarts; a note on the target's timeline then says so. Only a name that has never been found shows "Could not find the address".

The **interval** is how often a target is probed. 2.5 seconds is a good default. One second shows more detail; 15 or 60 seconds is kinder for long-term monitoring of many targets.

On a target's page, **Start over…** clears everything recorded for it and starts tracing afresh, and **Close** stops tracing it.

The **+** tab, next to "All targets", is the quick way to start again with something you traced before. It lists the targets you have closed, the most recent first, with when each was last traced; click one and tracing starts again, with everything it recorded before still there. While that list is short it also offers three public DNS servers that answer from anywhere, as a first thing to trace.

## Reading a trace

Each row is one hop: a router between you and the destination. The last row is the destination itself.

| Column | Meaning |
|---|---|
| Count | Probes sent in the period shown |
| Avg, Min, Max | Round-trip time in milliseconds; lost packets are not counted |
| Cur | The most recent sample |
| Jitter | How much consecutive samples differ, on average |
| Loss % | Share of probes that got no answer |
| MOS | Estimated voice quality, 1 (unusable) to about 4.4 (excellent) |
| Network | Who owns the address |

The bar on the right shows minimum to maximum as a thin line, the average as a block and the current sample as a cross, on a green / yellow / red background. A red bar from the left edge shows the share of samples a hop lost, and a red line joins the averages from hop to hop, so a jump in latency stands out.

**Columns** chooses what the table shows; right-clicking the column headings opens the same list. Two columns are off until you turn them on: **Lost** (how many samples got no answer) and **Notes**. **Reset columns to default** puts the usual set back.

Right-click a hop for more: copy its address or name, give the address a name of your own, keep **notes** about it (they show on every target that passes through that address), **look its name up again**, add it to a summary, make an alert for it, or start a trace to it.

**The one rule that matters:** a hop is only a problem if every hop after it, including the destination, shows the same problem. Routers often answer pings slowly or not at all while passing your traffic perfectly well. Traceline applies this rule for you and writes the result in the box above the table.

## Timelines

The destination always has a timeline. Click any hop to add one for it.

- The line is the average, the grey band is minimum to maximum, and red bars from the top are packet loss.
- **Drag** to move back in time. **Scroll** to zoom. **Back to now** returns to live.
- **Double-click** to focus: the table then shows the route and statistics for that moment. The **Focus** setting controls how long a period the table covers, from 5 seconds to an hour; "Auto" means the same period as the timeline, and "All that is recorded" covers the target's whole history.
- **Right-click** to add a note at that time ("rebooted the router"). Notes, route changes and alerts are marked along the bottom edge and listed under the timelines.

Ranges longer than six hours are drawn from one-minute summaries, so "Cur" is empty there.

## Alerts

An alert watches the destination of one, several or all targets, or a single hop (by number or by address). Right-click a hop on a target's page to start one for it.

| Condition | Fires when |
|---|---|
| Latency or loss over a period | At least a given share of samples in the last N minutes were lost or too slow |
| Latency or loss over a number of samples | At least X of the last Y samples were lost or too slow. "10 of 10 lost" is the classic "target is down" |
| Voice quality below a value | The MOS over the last N minutes is under your limit |
| The route changed | A hop was replaced or the route got longer or shorter |
| An address appears in the route | For example, your backup link's router |
| On a timer | Sends the current state on a schedule |

Set the latency to 0 to count only lost samples.

Each alert can act when the problem starts, when it ends, repeatedly while it lasts, or on every sample. Besides notifying, it can mark the timeline, append a line to a log file, save a picture of the trace or the recent samples to a folder, attach them to emails, start a program, put the target on a summary screen, and switch it between full-route and destination-only tracing. Starting programs has to be allowed in Settings first.

An alert can have its own **subject line** and message text; names in double braces, such as `{{Target}}` and `{{State}}`, are filled in (the list is shown in the alert editor). In an email the picture of the trace is shown inside the message. **Send at most one notification every N minutes** keeps a flapping connection from filling your inbox: notifications that come sooner are dropped, while the other actions still happen.

**Channels** are where notifications go: Gmail, Yahoo Mail or iCloud Mail (each with an app password), any other SMTP server, Microsoft Teams, Slack, Discord, ntfy (phone push), Telegram, or a webhook that receives JSON. Under **Notification channels** they are listed on the left, each with the account, topic or chat it sends to; the tick turns a channel on or off at once.

**Add channel** shows the kinds to choose from. Choosing one opens its form on the right, with the steps to set it up: buttons that open the page where the app password is made, **Generate** for an ntfy topic that is hard to guess, and **Find chat ID** for Telegram once you have written to your bot. Click a channel in the list to change it. Changes wait for **Save**: until then the channel is marked "not saved", and **Undo changes** puts it back. **Send test** sends a test message through that channel alone and shows the answer under the form; it works before saving too, so use it before relying on a channel.

Passwords, tokens and webhook addresses are stored encrypted on the computer Traceline runs on and are not shown again. A saved one is an empty box that says so; typing in it replaces it.

**Export…** and **Import…**, under the list of channels, move channels between computers or keep them safe across a reinstall. Export asks for a password of at least 8 characters and saves all channels, with their passwords, tokens and webhook addresses, to a file ending in `.smchannels` in your Downloads folder. The file is encrypted with that password, and you need it to import the file. Import asks for the file and its password; a channel with the same name as one you already have replaces it, and alerts that send to that channel keep doing so. Only administrators can export, because the file hands out every channel's password to whoever knows the file's password.

Service Monitor reads and writes the same files, so channels set up in one program do not have to be typed into the other. Channels that sign in with Google or Microsoft come across but cannot send from Traceline; set those up with an app password or SMTP instead.

The **Alert log**, lower on the Alerts page, lists every time an alert started, ended or repeated. It shows the last 7 days at first; the box above it widens that to 30 days, a year, or everything that is kept. **Save as a file** writes the lines shown as a file a spreadsheet opens.

Each alert has a sound: one of three built in, or a .wav, .mp3 or .ogg file you add on the Alerts page. It plays in every browser that has Traceline open and, for .wav files, from the tray icon. The page also has a switch for sound and for desktop notifications in the browser you are using.

## Sharing and exporting

**Export** on a target's page offers:

- **Report**: one self-contained page with the diagnosis, the route table, timelines and your notes for the period on screen. Print it, save it as PDF, or send the file to your provider.
- **Picture** of the trace as a PNG, GIF or BMP file, or copied.
- **Route as a text file**, or copied as text.
- **Every sample as CSV**, and **statistics per 5 minutes, per hour or per day as CSV**.
- **Session file**: everything the target recorded, or only the period on screen, as a `.traceline` file that any Traceline imports (see "Traceline's own files" below).

A **share link** (Settings) shows live results to someone else without letting them change anything. You choose which targets it shows and when it expires. The other person must be able to reach your computer, so this needs the network access described next.

## Access from other computers

By default the UI answers only on the computer it runs on. To open it to your network: set a password in Settings, tick "Let other computers on the network open the UI", restart Traceline, and allow Traceline through Windows Firewall. The setup program does the last part if you tick "Allow Traceline through Windows Firewall"; run the setup again to add it later.

### HTTPS

Tick "Also serve the UI over HTTPS" in Settings and restart Traceline. The UI is then also at `https://<computer>:7879`, encrypted. "Send browsers on other computers from the plain port to HTTPS" makes that the only way in from elsewhere; this computer itself and remote agents keep using the plain port.

Without a certificate file, Traceline makes a certificate for itself. The connection is encrypted, but the browser warns that it cannot tell who it is talking to, because nobody it trusts has vouched for that certificate. To get rid of the warning, point "Certificate file" at a certificate for your own domain: a `.pfx` or `.p12` file, or a `.pem` file with the key inside it or next to it as a `.key` file. Traceline does not fetch certificates from Let's Encrypt by itself; use a tool such as win-acme or certbot to get one and point at the file, or put a reverse proxy in front.

## Running as a service

The setup program sets this up, and tells you if the service could not be started and why. By hand, from an administrator prompt:

```
Traceline.exe --install-service
Traceline.exe --uninstall-service
```

`--install-service` can be run again at any time: it points the existing service at the program it is run from and starts it.

As a service, data is kept in `C:\ProgramData\Traceline`. When run by hand it is kept in `%LOCALAPPDATA%\Traceline`. To back up, stop Traceline and copy that folder.

**Updating:** run the new setup over the old one. It stops the service, closes Traceline's windows, replaces the program and starts the service again; your data stays.

**Uninstalling** (Windows Settings, Installed apps) removes the program, the service, the shortcuts and the firewall rule. It asks "Also delete the recorded data and settings?"; unless you answer Yes, `C:\ProgramData\Traceline` is kept for a later reinstall.

"Keep this computer awake while something is being traced" in Settings stops Windows from going to sleep for lack of keyboard and mouse activity.

## Stored data

Every sample is kept for 30 days and one-minute summaries for a year; both are adjustable in Settings. The **History** page shows what is stored per target and lets you clear or delete it. At a 2.5 second interval a target uses a few megabytes per day.

## Configurations

A configuration is a named set of settings; each target uses one. Make them under **Settings**, and pick one in the "Settings" box next to a target (it appears once there is more than the default).

| Setting | What it does |
|---|---|
| Packet type | ICMP (ping) works everywhere. UDP and TCP get through where ping is blocked and show what your applications see; they need administrator rights. "Remote agent" has another Traceline do the tracing |
| TCP port | 443 passes almost every firewall. Use another port to follow the path of a specific service |
| Wait for replies | How long before a probe counts as lost (3 seconds by default) |
| Pause between packets | Spreads out the probes of one round |
| Packet size, longest route | As named |
| Allow fragmentation | On: large packets may be split up on the way, and sizes up to 9000 bytes are sent. Off: packets carry the "don't fragment" flag and are kept to 1500 bytes, which shows whether a route carries full-size packets in one piece. Applies to ICMP and UDP over IPv4 |
| Forget a hop that has stopped answering | A hop that has been silent for the given minutes and samples loses its address in the route, until it answers again |
| Network interface | Which of this computer's addresses to send from |
| Colour thresholds, timeline scale, loss scale | How graphs are drawn |
| Jitter graph | A strip under each timeline, with a line at your target value |

UDP and TCP tracing send every packet of a target from the same local port, so routers that balance load keep them on one path and the route you see is one real route. TCP probes the hops of a round one after the other; a router that does not answer is given less time on later rounds so that a round stays short.

## Summary screens and workspaces

**All targets** is always there. Make your own summary screens with **New summary…** on the **+** tab of the Targets page. To fill one, tick targets and use "Add to summary", or right-click a hop on a target's page to put that hop on a summary. Right-click a tab to rename or delete it. The **Period** box sets how much history the figures cover.

**Right-click a target** in the list for a menu: show its time graph larger, close it, pause or resume it, clear its data and start over, put it on a summary or take it off ("Show on summary"), see the alerts that watch it, open it in a window of its own, or trace the destination only. If the row is ticked, the menu acts on all ticked targets and says how many. A web browser may open only the first of several windows unless you allow pop-ups for the page; Traceline then says how many it opened.

**Tile** shows the ticked targets (or all of them) side by side, each in its own pane, with a choice of columns. Drag a pane by its title onto another to rearrange, widen it with the arrows button, or take it off with ×; **Pop out** puts that view, or any single target (the ⧉ button), in a window of its own for a second screen.

Tick several targets to pause, resume or close them, or to change their interval or configuration, in one go. **Load a list** reads a text file with one target per line, and **Save list** writes one from the targets shown. **Find a target** narrows the list to names and addresses that contain what you type. Click a column heading to sort by it; click again for the other way round, and once more for the original order. **Columns**, or a right-click on the column headings, chooses the columns of the list: Lost, Min, Max, Settings and Notes are off until you turn them on, and **Reset columns to default** puts the usual set back.

The figure at the top right (for example "40.4 pps") is how many packets Traceline is sending per second.

A **workspace** is a separate set of targets and summaries, for example "home" and "office". Only the active workspace is traced. Create, rename, switch, export and import them under Settings; with more than one, a box at the top of the page switches between them. **Clear…** empties the workspace in use: its targets are closed and its summary screens removed. **Export** saves a workspace's targets and summary screens as a `.traceline-workspace` file; what was recorded is not in it.

## Sessions

**Close** on a target stops tracing it. What was recorded stays under **Sessions**, where it can be opened to look at, reopened to continue, exported as a `.traceline` file, or deleted. A session file can be imported on any Traceline.

**Import a text export** reads a comma-separated file in the "one column per hop, one row per sample" layout with sample times, as other trace tools can write.

Settings decides what closing keeps: always, only sessions that ran for a minimum time, or only sessions marked **Keep**.

## Local network discovery

**Discovery** scans a network this computer is connected to and lists each device's address, hardware address, name and what it says about itself. Tick devices and add them to a summary to keep an eye on them.

Vendor names come from the IEEE's public list, which Traceline does not include. An administrator can download it from the Discovery page.

## Accounts

Without accounts, nobody signs in and the UI only answers on the computer it runs on. Create an administrator under Settings and signing in becomes required.

| Role | Can |
|---|---|
| Administrator | Everything |
| Editor | Targets, notes, alerts, discovery, sessions |
| Viewer | Look only |

## Remote agents

To trace from another computer: install Traceline there, give it an **agent key** and network access in its Settings. Then, on your own Traceline, make a configuration with packet type "Remote agent", that computer's address and the key. Targets using that configuration are probed from the other computer and recorded on yours.

## Desktop mode

Starting Traceline, from its Start menu shortcut or by double-clicking `Traceline.exe`, opens it in its own window and shows an icon in the notification area. No browser and no console window are involved: the window has its own place on the taskbar, and "Pop out" on a target or a wall opens further Traceline windows, for a second screen for example.

- **Closing the window** leaves Traceline running behind its icon. Double-click the icon, or start Traceline again, to get the window back.
- **The icon** turns red and shows a notice while an alert is active. Its menu has "Open Traceline", "Open in browser" and "Exit".
- **Exit** closes the window and the icon. If the Traceline service is running, the window only attaches to it and tracing goes on; otherwise this copy does the tracing itself, and Exit stops it.
- `--minimized` starts with only the icon. The installer's "show the tray icon when I sign in" option uses it.
- `--headless` runs Traceline without a window or icon, for use from a terminal or a script: it serves the UI at `http://localhost:7878` until Ctrl+C. `--open` does the same and opens your default browser. The builds for Linux and macOS always run this way.

The window uses the Microsoft Edge WebView2 Runtime, which is part of Windows 11 and comes with Edge on Windows 10 and Windows Server. Where it is missing, Traceline opens in Edge, Chrome or your default browser instead. If accounts are set up, you sign in inside the window as you would in a browser.

## Traceline's own files

Traceline has two kinds of file of its own:

| File | What is in it | Made by | Read by |
|---|---|---|---|
| `.traceline` | A session: what one target recorded, for all time or for a period | **Export** on the Sessions page, or **Export** on a target's page | **Import a session file** on the Sessions page |
| `.traceline-workspace` | A workspace: its targets with their settings, and its summary screens, without what was recorded | **Export** beside a workspace in Settings | **Import a workspace file** in Settings |

On a computer where Traceline was installed with its setup program, both kinds show as Traceline's in Explorer. Double-clicking one opens Traceline's window and imports it: a session opens as a closed session you can look at or reopen, a workspace is added to the list of workspaces. Workspace files written by earlier versions, ending in `.traceline-workspace.json`, are still read.

Lists of targets are plain text files with one target per line, and notification channels travel as `.smchannels` files, which Service Monitor reads too.

## Keyboard shortcuts and help

| Keys | Does |
|---|---|
| Ctrl+N | New trace: the **+** tab on Targets, with the cursor in the target box |
| Ctrl+L | Load a list of targets from a text file |
| Ctrl+F | Find a target in the list |
| Ctrl+O | Import a session file |
| Ctrl+S | On a target's page: save everything it recorded as a session file |
| Ctrl+Shift+C | On a target's page: copy the picture of the trace |
| Ctrl+Alt+C | On a target's page: copy the route as text |

They all work in Traceline's own window. A web browser keeps some of these keys for itself: Ctrl+N there opens a browser window.

**Help**, at the top of the page, shows the version, these shortcuts and this guide.

## Limits worth knowing

- Many home routers answer only one "hop 1" probe per second. With several targets, hop 1 will then show loss that is not real. The diagnosis accounts for this.
- A destination that ignores ping shows 100% loss even when it is up. Add a `tcp://` or `https://` probe for it.
