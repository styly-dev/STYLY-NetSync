# STYLY NetSync Launcher

Double-clickable entry points for the STYLY NetSync Launcher: a small desktop
window that does the whole local setup without a terminal.

| What you want | What to do |
|---|---|
| Windows | Double-click **`Windows/STYLY NetSync Launcher.vbs`** |
| Windows, if `.vbs` is blocked by policy | Double-click **`Windows/STYLY NetSync Launcher.cmd`** (a console flashes briefly) |
| macOS / Linux | Double-click **`macOS-Linux/STYLY NetSync Launcher.command`** |
| Already inside Unity | **STYLY → STYLY NetSync → Start NetSync Server → Open Launcher** |

## What the launcher does

- **Server** — start and stop the NetSync server, watch its log live, and read
  off the IP addresses clients should connect to. No console window.
- **Unity Project** — pick a Unity project folder and install or update the
  `com.styly.styly-netsync` package. It writes the OpenUPM scoped registry and
  the package version straight into `Packages/manifest.json`, so **Node.js and
  the OpenUPM CLI are not needed**. Unity imports the package the next time it
  gets focus. The previous manifest is kept as `manifest.json.netsync-backup`.
- **Simulator** — spawn simulated clients to check a room end to end: client
  count, server address and room, plus transform send rate, spawn batching,
  battery sync and log level. Ports are taken from the Server tab so the two
  halves cannot disagree.

## First run

The launcher needs [uv](https://docs.astral.sh/uv/), which manages the Python
runtime. If it is missing, the Windows and macOS/Linux entry points offer to
install it for you: user-level, no administrator rights, nothing added to PATH
by us. Then they download the server and its dependencies behind a progress
window. Later runs start immediately from uv's cache, and work offline.

## Which version gets run

- Run from a checkout of this repository, the launcher uses **this checkout's
  server sources**, so it always matches the code you are working on.
- Copied somewhere else (a venue PC, for example), it resolves
  `styly-netsync-server` from PyPI, pinned to the version in
  `STYLY-NetSync-Unity/Packages/com.styly.styly-netsync/package.json` when that
  file is reachable, and `latest` otherwise. Drop a `netsync-version.txt` next
  to `netsync-launcher.ps1` to pin a specific version instead.

Keep the Unity package and the server on the same version — the wire protocol
is not backward compatible.

## Copying the launcher to another machine

Copy the whole platform folder (the `.vbs` and the `.ps1` must stay together)
or the whole `tools/` folder. Nothing else from the repository is required.

## Troubleshooting

Every failure is reported in a dialog box, never on a console.

| Symptom | Cause |
|---|---|
| "uv was not found" | Decline of the install prompt, or the installer failed. Install uv manually and start the launcher again. |
| "Could not prepare STYLY NetSync" | The dialog contains uv's own output — usually no network on a first run, before anything is cached. |
| The Unity tab says "not a Unity project" | Pick the folder containing `Assets` and `ProjectSettings`. |
| The Unity tab refuses to install | The project embeds NetSync under `Packages/com.styly.styly-netsync`. An embedded package always wins over the registry, so there is nothing to install. |
| Server will not start, log mentions a port | Another process holds one of the ports. Change it on the Server tab. |
| "A NetSync server from an earlier session is still running" | The launcher was killed rather than closed, and the server it started outlived it. Answer **Yes** to stop it and free its ports. |

For headless machines, `styly-netsync-server` is still the right tool — the
launcher needs a desktop session.
