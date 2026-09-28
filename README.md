# OpenRemoteViewer

A lightweight, single-file remote screen viewer for Windows. Built with WPF (.NET 8).

## What it does

OpenRemoteViewer lets you view a remote computer's screen in real-time over a network. It works in two roles:

- **Client** — runs on the employee's machine, captures the screen and streams it to the admin
- **Admin** — connects to a client and views their screen live with FPS counter, multi-monitor switching, and click markers

All traffic is encrypted end-to-end using AES-256-GCM with password-based key derivation (PBKDF2-SHA256, 100k iterations). No data is sent unencrypted.

## Features

- **End-to-end encryption** — challenge-response authentication + AES-GCM encrypted communication
- **Multi-monitor support** — switch between displays, see where the employee's cursor is
- **Click overlay** — admin can click on the screen image to place a visible red marker on the client's display
- **Draw on the client's screen** — hold the **right mouse button** and draw on the image to highlight an element; the stroke appears on the employee's screen and fades out 3 seconds after you stop drawing
- **Survives UAC / firewall prompts** — when the screen is temporarily not capturable (secure desktop, locked workstation, display change) the session stays alive, the admin sees *"screen temporarily unavailable"*, and the picture comes back by itself
- **Port protection** — the client's port cannot be hijacked by another program that binds with `SO_REUSEADDR` (Node, Python, Docker and friends); if the port is busy at startup the client waits for it instead of dying
- **Auto-reconnect** — if the connection drops (client reboot, network blip, tunnel restart) the admin reconnects automatically every 2 seconds
- **Connection notifications** — when admin connects, the client sees a prominent toast notification and the tray icon turns red
- **System tray** — client minimizes to tray, stays running in the background
- **Auto-start** — optional Windows startup with auto-connect (no UI, goes straight to tray)
- **Remember settings** — saves IP, port, role, and password (DPAPI-encrypted) between sessions
- **Single exe** — self-contained ~68 MB portable executable, no installation required
- **Reconnect support** — admin can disconnect and reconnect any number of times without the client needing to restart

## Download

Grab the latest `OpenRemoteViewer.exe` from the [Releases](../../releases/latest) page — it's a single portable file, no installation needed.

## Requirements

- Windows 10/11 (x64)
- .NET 8 runtime (included in the single-file build)

## Usage

### Same network (LAN / office)

1. Run `OpenRemoteViewer.exe` on both machines
2. On the employee's machine: select **Client**, set a port and password, click Start
3. On the admin machine: select **Admin**, enter the client's local IP, same port and password, click Start
4. The admin window shows the client's screen in real-time

### Different networks (remote access)

If the admin and client are on different networks, the client's port must be reachable from the internet. Options:

**Option A — Port forwarding**
On the client's router, forward the chosen port (e.g. `9876`) to the client machine's local IP. The admin then connects using the client's public IP.

**Option B — Tunnel (no router access needed)**
Use a free tunneling service to expose the client's port:

- [Tailscale](https://tailscale.com) — creates a private mesh VPN between your devices, no port forwarding needed (free for personal use)
- [ngrok](https://ngrok.com) — `ngrok tcp 9876` (requires free registration)
- [Cloudflare Tunnel](https://developers.cloudflare.com/cloudflare-one/connections/connect-networks/) — requires free Cloudflare account
- [localhost.run](https://localhost.run) — `ssh -R 80:localhost:9876 nokey@localhost.run`
- [Serveo](https://serveo.net) — `ssh -R 9876:localhost:9876 serveo.net`

The tunnel gives you a public address that the admin uses to connect.

## Building from source

```bash
cd OpenRemoteViewer
dotnet publish -c Release
```

The output is a single self-contained exe at:
```
OpenRemoteViewer/bin/Release/net8.0-windows/win-x64/publish/OpenRemoteViewer.exe
```

## How it works

1. Client starts a TCP listener on the specified port
2. Admin connects and performs a challenge-response handshake using the shared password
3. A symmetric AES-256-GCM key is derived from the password + random salt
4. Client captures the screen ~30 times per second (JPEG) and sends encrypted frames
5. Admin decrypts and displays frames, can send click coordinates, freehand stroke points and monitor switch commands
6. When admin disconnects, client automatically returns to listening for a new connection
7. If a frame cannot be captured (UAC prompt, locked screen), the client keeps the session and sends a "capture blocked" notice until capture works again

## Project structure

```
OpenRemoteViewer/
├── App.xaml / App.xaml.cs          — Application entry, auto-connect logic
├── MainWindow.xaml / .cs           — Role selection, settings UI
├── ClientWindow.xaml / .cs         — Client: screen capture, streaming, tray icon
├── AdminWindow.xaml / .cs          — Admin: screen viewer, multi-monitor UI
├── AdminNotificationWindow.xaml/.cs — Toast notification shown on client when admin connects
├── NetworkProtocol.cs              — Encrypted TCP protocol (SecureConnection)
├── ScreenCapture.cs                — Screen capture and monitor enumeration
├── ClickOverlay.cs                 — Transparent overlay for click markers and admin drawings
├── StrokePainter.cs                — Freehand strokes on a canvas, auto-fade 3 s after the last point
├── IconHelper.cs                   — Programmatic icon generation (blue/red)
└── AppSettings.cs                  — Settings persistence with encrypted password
```

## License

MIT
