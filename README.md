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
- The same password on both sides, at least 10 characters. Choose a long random one: the password is the only thing protecting the connection.
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
- **Admin** and the start window always show a version button (**v1.1.1 · Check for updates**). A click checks right away. When a new version is out, the button turns green (**Update to vX.Y.Z**), a notification pops up, and a click installs it and reopens the same connection.

A download is installed only if it carries a valid signature made with the publisher's private key (ECDSA P-256). The matching public key is built into the program, so neither a tampered download nor a hijacked GitHub account can push code to your computers. The current version is shown in the window title.

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

Releases are signed. Once, create a signing key and keep it outside the repository (and keep a backup: without it you cannot publish updates that existing copies will accept):

```bash
mkdir -p ~/.orv && openssl ecparam -genkey -name prime256v1 -noout -out ~/.orv/update-signing.pem
```

Print its public half with `openssl ec -in ~/.orv/update-signing.pem -pubout` and put it into `PublisherPublicKey` in `Updater.cs`. If you fork the project, also change `Repo` there, so your builds update from your own releases.

Then, for every version:

1. Raise `<Version>` in `OpenRemoteViewer/OpenRemoteViewer.csproj` and commit.
2. Run the release script from Git Bash:

```bash
./release.sh "What changed"
```

It builds the exe, signs it, pushes the tag `v<Version>` and creates the GitHub release with `OpenRemoteViewer.exe` and `OpenRemoteViewer.exe.sig`. Every running copy picks it up within an hour.

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
├── AdminWindow.xaml(.cs)        admin: viewer, monitor switch, version / update button
├── AdminNotificationWindow      "admin connected" notice on the client
├── NetworkProtocol.cs           encrypted TCP protocol, timeouts, keepalive
├── ScreenCapture.cs             screen capture and monitor list
├── ClickOverlay.cs              transparent overlay for markers and strokes
├── StrokePainter.cs             freehand strokes with fade-out
├── Updater.cs                   self-update from GitHub Releases
├── Lang.cs                      interface translations
├── IconHelper.cs                generated icons
├── UpdatePrompt.cs               version / update button and notices
└── AppSettings.cs               saved settings (password protected with DPAPI)
```

## Security

- **Password.** Everything rests on the shared password: use a long random one. The settings window refuses passwords shorter than 10 characters.
- **Guessing over the network.** After 5 wrong passwords from one address the client refuses that address for 1 minute, then 2, 4 and so on up to an hour.
- **Tampering in transit.** Every message is encrypted and bound to its type, direction and sequence number, so it cannot be replayed, reordered, relabelled or reflected.
- **Updates** are accepted only with the publisher's signature.
- **Not covered yet.** Someone who records the traffic and later learns the password can decrypt the recording, and can try passwords against a recorded handshake offline. A long password makes the second attack impractical. Prefer a private network such as Tailscale over an open port.

Found a problem? Please open an issue.

## Roadmap

- One admin window showing several clients at once
- A saved list of clients instead of typing the address each time

## Responsible use

Use it only on computers you own or administer, and tell the people who work on them. Monitoring someone without their knowledge is illegal in many countries.

## License

[MIT](LICENSE)
