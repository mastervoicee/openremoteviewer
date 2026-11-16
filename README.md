# OpenRemoteViewer

A small, single-file remote screen viewer for Windows. One exe, no installer, no account, no cloud relay: the admin connects straight to the employee's computer and sees its screen live.

[Русская версия](README.ru.md) · [Download the latest release](../../releases/latest)

## What it does

The same exe works in two roles:

- **Client** runs on the computer being watched. It captures the screen and streams it.
- **Admin** connects to a client and shows its screen in a window.

The admin only watches. There is no remote mouse or keyboard control. To point at something, the admin can click (a red marker appears on the client's screen) or draw with the right mouse button (the stroke fades out after 3 seconds).

The person at the client computer always knows they are being watched: a notification appears and the tray icon turns red while an admin is connected.

## Features

- **Encrypted traffic.** AES-256-GCM, key derived from the shared password (PBKDF2-SHA256, 100 000 iterations), challenge-response authentication.
- **Multi-monitor.** Switch between the client's displays and see which one the cursor is on.
- **Click marker and drawing** on the client's screen.
- **Reconnects by itself.** If the connection drops or goes silent for 10 seconds, the admin reconnects automatically.
- **Survives UAC prompts and a locked screen.** The session stays alive and the picture comes back.
- **Updates itself** from this repository's releases (see [Updates](#updates)).
- **Tray and auto-start.** The client lives in the tray and can start with Windows.
- **10 interface languages:** English, Russian, German, Spanish, French, Polish, Czech, Lithuanian, Latvian, Estonian.

## What you need

- Windows 10 or 11, 64-bit, on both computers. Nothing else to install: .NET is inside the exe.
- A network path from the admin to the client's port (default `9876`). On one office network this already exists. Between different networks, see [Different networks](#different-networks).
- The same password on both sides. Choose a long one: the password is the only thing protecting the connection.
- A folder the program can write to (Desktop, Documents, `C:\Tools`). Self-update replaces the exe in place, so do not put it in `Program Files`.

On first start Windows may show two prompts:

- **SmartScreen** ("Windows protected your PC"), because the exe is not code-signed. Click *More info* → *Run anyway*.
- **Windows Firewall** on the client. Allow access, otherwise the admin cannot connect.

## Quick start

### On the employee's computer (client)

1. Download `OpenRemoteViewer.exe` and put it in a permanent folder.
2. Run it, choose **Client**, set a port and a password.
3. Tick **Remember settings and password** and **Auto-start with Windows** if the computer should be reachable after every reboot.
4. Click **Start**. The window shows the local and external IP address. You can close it; the program stays in the tray.

### On your computer (admin)

1. Run the same exe, choose **Admin**.
2. Enter the client's IP address, the same port and password.
3. Click **Start**. The client's screen appears.

In the viewer window:

| Action | Result |
| --- | --- |
| Left click on the picture | Red marker on the client's screen |
| Hold right button and draw | A stroke on the client's screen, fades after 3 s |
| Click a display button at the top | Switch to another monitor of the client |

To watch several computers, start one admin window per client (run the exe again for each).

## Different networks

The client's port must be reachable from the admin's computer.

**Tailscale (recommended).** Install [Tailscale](https://tailscale.com) on both computers and sign in to the same account. Each computer gets a permanent `100.x.x.x` address. Enter the client's Tailscale address in the admin window. No router setup, nothing exposed to the internet.

**Port forwarding.** On the client's router, forward the port (e.g. `9876`) to the client computer and connect to the external IP shown in the client window. The port becomes visible to the whole internet, so use a strong password.

**A TCP tunnel** such as `ngrok tcp 9876` also works: connect to the address the tunnel gives you.

## Updates

The program checks this repository's [latest release](../../releases/latest) shortly after start and then once an hour.

- **Client** updates silently: it downloads the new exe, replaces itself and restarts with the same port and password. If an admin was watching, the picture returns in a few seconds.
- **Admin** shows an **Update to vX.Y.Z** button in the top bar and updates when you click it.

A download is installed only if its SHA-256 matches the checksum GitHub publishes for the release file. The current version is shown in the window title.

If an update does not install, the usual reason is that the exe is in a folder the program cannot write to.

## Troubleshooting

| Symptom | What to check |
| --- | --- |
| Admin: connection error on first start | IP and port; is the client running; did the client's firewall prompt get allowed |
| "Wrong password" | Passwords differ, including spaces at the ends |
| "Port is taken by another program" on the client | Another program uses the port. Choose a different one on both sides |
| "Screen temporarily unavailable" | A UAC prompt or lock screen is showing on the client. The picture returns by itself |
| Keeps reconnecting | The client is off, asleep or offline. With Tailscale, check that both computers are online in its menu |

## Building from source

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```bash
cd OpenRemoteViewer
dotnet publish -c Release
```

The result is `OpenRemoteViewer/bin/Release/net8.0-windows/win-x64/publish/OpenRemoteViewer.exe`.

### Publishing a new version

1. Raise `<Version>` in `OpenRemoteViewer/OpenRemoteViewer.csproj`.
2. Build with `dotnet publish -c Release`.
3. Create a release whose tag is `v` + that version and attach the exe under the name `OpenRemoteViewer.exe`:

```bash
gh release create v1.2.0 OpenRemoteViewer/bin/Release/net8.0-windows/win-x64/publish/OpenRemoteViewer.exe --title "v1.2.0" --notes "What changed"
```

Every running copy picks it up within an hour. If you fork the project, change `Repo` in `Updater.cs` so your builds update from your own releases.

## How it works

1. The client listens on a TCP port.
2. The admin connects; both sides prove they know the password with a challenge-response handshake and derive an AES-256-GCM key from the password and a random salt.
3. The client captures the selected monitor about 30 times per second as JPEG and sends encrypted frames.
4. The admin decrypts and shows them, and sends back clicks, stroke points and monitor-switch commands.
5. When the admin leaves, the client goes back to waiting. It serves one admin at a time.

```
OpenRemoteViewer/
├── App.xaml.cs                  start-up, auto-connect, resume after update
├── MainWindow.xaml(.cs)         role, address, password, language
├── ClientWindow.xaml(.cs)       client: capture, streaming, tray, silent update
├── AdminWindow.xaml(.cs)        admin: viewer, monitor switch, update button
├── AdminNotificationWindow      "admin connected" notice on the client
├── NetworkProtocol.cs           encrypted TCP protocol, timeouts, keepalive
├── ScreenCapture.cs             screen capture and monitor list
├── ClickOverlay.cs              transparent overlay for markers and strokes
├── StrokePainter.cs             freehand strokes with fade-out
├── Updater.cs                   self-update from GitHub Releases
├── Lang.cs                      interface translations
├── IconHelper.cs                generated icons
└── AppSettings.cs               saved settings (password protected with DPAPI)
```

## Roadmap

- One admin window showing several clients at once
- A saved list of clients instead of typing the address each time

## Responsible use

Use it only on computers you own or administer, and tell the people who work on them. Monitoring someone without their knowledge is illegal in many countries.

## License

[MIT](LICENSE)
