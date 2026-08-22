"""Desktop launcher for STYLY NetSync - a terminal-free setup and control GUI.

Run it with ``styly-netsync-launcher`` (or the double-clickable launchers in
``tools/``). It covers the whole local setup:

* **Server** - start/stop the NetSync server with live logs, no console window.
* **Unity project** - add or update the ``com.styly.styly-netsync`` package in a
  Unity project by patching ``Packages/manifest.json`` directly, so neither
  Node.js nor the OpenUPM CLI is needed.
* **Simulator** - spawn simulated clients to verify a room end to end.

The GUI is built on Tkinter (Python standard library) so it adds no dependency
to the server package.
"""

from __future__ import annotations

import argparse
import json
import os
import queue
import re
import signal
import socket
import subprocess
import sys
import threading
import tkinter as tk
from collections.abc import Callable, Sequence
from dataclasses import asdict, dataclass
from pathlib import Path
from tkinter import filedialog, font, messagebox, ttk
from typing import Any, TypeVar

from .network_utils import get_local_ip_addresses
from .unity_setup import UnitySetupError, inspect_project, install_package

APP_TITLE = "STYLY NetSync Launcher"

WINDOWS = sys.platform == "win32"
_CREATE_NO_WINDOW = 0x08000000
_CREATE_NEW_PROCESS_GROUP = 0x00000200

_LOG_LEVELS = ("TRACE", "DEBUG", "INFO", "WARNING", "ERROR", "CRITICAL")
# The simulator's argparse accepts a narrower set than the server's.
_SIMULATOR_LOG_LEVELS = ("DEBUG", "INFO", "WARNING", "ERROR")
_MAX_LOG_LINES = 4000

# Loopback port used only as a single-instance lock, never for traffic.
_SINGLE_INSTANCE_PORT = 58472

# The server colourises its console sink; a Tk text widget would render those
# escape sequences literally.
_ANSI_RE = re.compile(r"\x1b\[[0-9;?]*[ -/]*[@-~]")

# Colors for the console panes. Kept explicit so the log stays readable
# regardless of the OS theme the rest of the window inherits.
_CONSOLE_BG = "#12151a"
_CONSOLE_FG = "#d5dbe2"
_CONSOLE_WARN = "#e3b341"
_CONSOLE_ERROR = "#f4776b"
_CONSOLE_NOTE = "#7fb3ff"

_STATUS_COLORS = {
    "stopped": "#8b949e",
    "starting": "#e3b341",
    "running": "#3fb950",
    "error": "#f4776b",
}


# --------------------------------------------------------------------------
# Settings
# --------------------------------------------------------------------------


@dataclass
class ServerSettings:
    """Server options exposed by the launcher.

    Defaults mirror ``default.toml``; only values that differ from the defaults
    are passed on the command line, so a ``--config`` file keeps its meaning.
    """

    control_port: int = 5555
    transform_port: int = 5557
    pub_port: int = 5556
    server_discovery_port: int = 9999
    rest_api_port: int = 8800
    disable_server_discovery: bool = False
    log_level_console: str = "INFO"
    config_file: str = ""


@dataclass
class SimulatorSettings:
    """Client simulator options exposed by the launcher.

    Defaults mirror ``client_simulator.main``; only values that differ from the
    defaults are passed on the command line. The ports are not here on purpose:
    they are taken from the Server tab so both halves cannot disagree.
    """

    clients: int = 10
    server: str = "localhost"
    room: str = "default_room"
    transform_send_rate: float = 10.0
    spawn_batch_size: int = 0
    spawn_batch_interval: float = 0.0
    sync_battery: bool = True
    log_level: str = "INFO"


def settings_path() -> Path:
    """Return the per-user file the launcher persists its settings to."""
    # Read through a local so mypy does not prune the other platforms' branches.
    platform_name = sys.platform
    if WINDOWS:
        base = Path(os.environ.get("APPDATA") or Path.home() / "AppData" / "Roaming")
    elif platform_name == "darwin":
        base = Path.home() / "Library" / "Application Support"
    else:
        base = Path(os.environ.get("XDG_CONFIG_HOME") or Path.home() / ".config")
    return base / "STYLY-NetSync" / "launcher.json"


def server_state_path() -> Path:
    """Return the file recording the server this launcher started."""
    return settings_path().with_name("running-server.json")


def record_running_server(pid: int, control_port: int) -> None:
    """Note the running server so a later session can find it again.

    If the launcher is killed rather than closed - a crash, or Task Manager -
    the server it started keeps running and keeps holding its ports. This
    record is what makes that recoverable instead of a mystery.
    """
    path = server_state_path()
    try:
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(
            json.dumps({"pid": pid, "control_port": control_port}, indent=2),
            encoding="utf-8",
        )
    except OSError:
        pass


def clear_running_server() -> None:
    """Drop the record after a server has been stopped."""
    try:
        server_state_path().unlink()
    except OSError:
        pass


def find_stale_server() -> dict[str, int] | None:
    """Return a still-running server left behind by an earlier launcher.

    Returns ``None`` unless the recorded process is alive *and* still looks
    like a NetSync server, so a recycled PID is never mistaken for one.
    """
    try:
        raw: Any = json.loads(server_state_path().read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return None
    if not isinstance(raw, dict):
        return None

    pid = raw.get("pid")
    control_port = raw.get("control_port")
    if not isinstance(pid, int) or not isinstance(control_port, int):
        return None

    if not _is_netsync_server(pid):
        clear_running_server()
        return None
    return {"pid": pid, "control_port": control_port}


def _is_netsync_server(pid: int) -> bool:
    """True when *pid* is alive and its command line runs our server module."""
    try:
        import psutil

        process = psutil.Process(pid)
        argv = process.cmdline()
    except Exception:  # noqa: BLE001 - psutil raises a family of errors
        return False
    return any(
        argument in {"styly_netsync", "styly-netsync-server"} for argument in argv
    )


def stop_stale_server(pid: int) -> bool:
    """Kill a server left behind by an earlier launcher. True when it is gone."""
    if not _is_netsync_server(pid):
        clear_running_server()
        return True
    _kill_process_tree(pid)
    stopped = not _is_netsync_server(pid)
    if stopped:
        clear_running_server()
    return stopped


def load_settings() -> dict[str, Any]:
    """Load persisted settings, returning ``{}`` when unavailable."""
    try:
        data: Any = json.loads(settings_path().read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError):
        return {}
    return data if isinstance(data, dict) else {}


def save_settings(data: dict[str, Any]) -> None:
    """Persist settings, ignoring failures (the GUI must never die on this)."""
    path = settings_path()
    try:
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(data, indent=2) + "\n", encoding="utf-8")
    except OSError:
        pass


# --------------------------------------------------------------------------
# Command building (pure - covered by tests)
# --------------------------------------------------------------------------


def build_server_command(
    settings: ServerSettings, python_executable: str | None = None
) -> list[str]:
    """Build the command that runs the server in a child process.

    The server is invoked through the *current* interpreter, so the launcher and
    the server it starts are always the same version and no network round-trip
    is needed to resolve the package.
    """
    defaults = ServerSettings()
    command = [python_executable or sys.executable, "-m", "styly_netsync"]

    if settings.config_file:
        command += ["--config", settings.config_file]
    if settings.control_port != defaults.control_port:
        command += ["--control-port", str(settings.control_port)]
    if settings.transform_port != defaults.transform_port:
        command += ["--transform-port", str(settings.transform_port)]
    if settings.pub_port != defaults.pub_port:
        command += ["--pub-port", str(settings.pub_port)]
    if settings.rest_api_port != defaults.rest_api_port:
        command += ["--rest-api-port", str(settings.rest_api_port)]

    if settings.disable_server_discovery:
        command.append("--no-server-discovery")
    elif settings.server_discovery_port != defaults.server_discovery_port:
        command += ["--server-discovery-port", str(settings.server_discovery_port)]

    if settings.log_level_console != defaults.log_level_console:
        command += ["--log-level-console", settings.log_level_console]

    return command


def build_simulator_command(
    settings: SimulatorSettings,
    server_settings: ServerSettings,
    python_executable: str | None = None,
) -> list[str]:
    """Build the command that runs the client simulator in a child process.

    Ports come from *server_settings* rather than from the simulator's own
    fields, so pointing the simulator somewhere else cannot silently leave it
    talking to the wrong ports.
    """
    defaults = SimulatorSettings()
    command = [
        python_executable or sys.executable,
        "-m",
        "styly_netsync.client_simulator",
        "--clients",
        str(settings.clients),
        "--server",
        settings.server,
        "--room",
        settings.room,
        "--control-port",
        str(server_settings.control_port),
        "--transform-port",
        str(server_settings.transform_port),
        "--sub-port",
        str(server_settings.pub_port),
    ]

    if settings.transform_send_rate != defaults.transform_send_rate:
        command += ["--transform-send-rate", str(settings.transform_send_rate)]
    if settings.spawn_batch_size != defaults.spawn_batch_size:
        command += ["--spawn-batch-size", str(settings.spawn_batch_size)]
        # The simulator only honours an interval when batching is on.
        if settings.spawn_batch_interval != defaults.spawn_batch_interval:
            command += ["--spawn-batch-interval", str(settings.spawn_batch_interval)]
    if not settings.sync_battery:
        command.append("--no-sync-battery")
    if settings.log_level != defaults.log_level:
        command += ["--log-level", settings.log_level]

    return command


# --------------------------------------------------------------------------
# Child process supervision
# --------------------------------------------------------------------------


class ManagedProcess:
    """A supervised child process whose output is streamed line by line.

    Output is read on a background thread into a queue; the Tk main loop drains
    it, so no Tk call ever happens off the main thread.
    """

    def __init__(self) -> None:
        self._process: subprocess.Popen[str] | None = None
        self._lines: queue.Queue[str | None] = queue.Queue()
        self._lock = threading.Lock()
        self._stop_requested = threading.Event()

    @property
    def is_running(self) -> bool:
        with self._lock:
            process = self._process
        return process is not None and process.poll() is None

    @property
    def pid(self) -> int | None:
        with self._lock:
            process = self._process
        return process.pid if process is not None else None

    def start(self, command: Sequence[str], cwd: Path | None = None) -> None:
        """Start *command*. Raises OSError if the process cannot be spawned."""
        if self.is_running:
            raise RuntimeError("Process is already running.")

        self._stop_requested.clear()
        env = dict(os.environ)
        # Stream logs promptly instead of in 8 KB blocks.
        env["PYTHONUNBUFFERED"] = "1"

        creationflags = 0
        start_new_session = False
        if WINDOWS:
            # No console window, and its own process group so a Ctrl-Break can
            # be delivered without touching the launcher itself.
            creationflags = _CREATE_NO_WINDOW | _CREATE_NEW_PROCESS_GROUP
        else:
            start_new_session = True

        process = subprocess.Popen(  # noqa: S603 - fixed argv, no shell
            list(command),
            cwd=str(cwd) if cwd else None,
            env=env,
            stdin=subprocess.DEVNULL,
            stdout=subprocess.PIPE,
            stderr=subprocess.STDOUT,
            text=True,
            encoding="utf-8",
            errors="replace",
            bufsize=1,
            creationflags=creationflags,
            start_new_session=start_new_session,
        )

        with self._lock:
            self._process = process

        thread = threading.Thread(
            target=self._pump_output, args=(process,), daemon=True
        )
        thread.start()

    def _pump_output(self, process: subprocess.Popen[str]) -> None:
        stream = process.stdout
        if stream is not None:
            try:
                for line in stream:
                    self._lines.put(_ANSI_RE.sub("", line).rstrip("\r\n"))
            except (OSError, ValueError):
                pass
        code = process.wait()
        if self._stop_requested.is_set():
            # A forced stop reports a non-zero code on Windows; that is the
            # Stop button working, not a crash.
            self._lines.put("[stopped]")
        else:
            self._lines.put(f"[process exited with code {code}]")
        self._lines.put(None)

    def drain(self, limit: int = 400) -> tuple[list[str], bool]:
        """Return up to *limit* buffered lines and whether the process ended."""
        lines: list[str] = []
        ended = False
        for _ in range(limit):
            try:
                item = self._lines.get_nowait()
            except queue.Empty:
                break
            if item is None:
                ended = True
            else:
                lines.append(item)
        return lines, ended

    def stop(self, timeout: float = 8.0) -> None:
        """Ask the process to stop, escalating to a forced kill if needed."""
        with self._lock:
            process = self._process
        if process is None or process.poll() is not None:
            return

        self._stop_requested.set()
        grace = self._request_shutdown(process, timeout)
        if _wait_for(process, grace):
            return

        try:
            process.terminate()
        except OSError:
            pass
        if _wait_for(process, 3.0):
            return

        _kill_process_tree(process.pid)
        _wait_for(process, 2.0)

    def _request_shutdown(
        self, process: subprocess.Popen[str], timeout: float
    ) -> float:
        """Send the platform's stop signal; return how long to wait for it.

        Platform-specific symbols are reached through ``getattr`` and the branch
        is chosen on ``os.name``: a ``sys.platform`` comparison would make one
        of these branches statically dead on whichever OS runs mypy.
        """
        if os.name == "nt":
            # Ctrl-Break is only delivered when a console is attached, which a
            # windowless GUI does not have. Try anyway - it works when the
            # launcher was started from a terminal - but fall through quickly
            # to the kill Windows does guarantee. The server keeps no state
            # that needs flushing and the OS reclaims its sockets.
            ctrl_break = getattr(signal, "CTRL_BREAK_EVENT", None)
            if ctrl_break is not None:
                try:
                    os.kill(process.pid, ctrl_break)
                except OSError:
                    pass
            return 1.5

        # cli_main turns SIGTERM into its normal KeyboardInterrupt shutdown.
        killpg = getattr(os, "killpg", None)
        getpgid = getattr(os, "getpgid", None)
        if killpg is not None and getpgid is not None:
            try:
                killpg(getpgid(process.pid), signal.SIGTERM)
            except (OSError, ProcessLookupError):
                pass
        return max(timeout - 3.0, 1.0)

    def clear_if_finished(self) -> None:
        with self._lock:
            if self._process is not None and self._process.poll() is not None:
                self._process = None


def _wait_for(process: subprocess.Popen[str], timeout: float) -> bool:
    """Wait up to *timeout* seconds; return True once the process is gone."""
    try:
        process.wait(timeout=timeout)
    except subprocess.TimeoutExpired:
        return False
    return True


def _kill_process_tree(pid: int) -> None:
    """Last-resort kill of a process and everything it spawned."""
    try:
        import psutil
    except ImportError:  # pragma: no cover - psutil is a hard dependency
        return
    try:
        parent = psutil.Process(pid)
    except psutil.Error:
        return
    children = []
    try:
        children = parent.children(recursive=True)
    except psutil.Error:
        pass
    for proc in [*children, parent]:
        try:
            proc.kill()
        except psutil.Error:
            pass


# --------------------------------------------------------------------------
# Widgets
# --------------------------------------------------------------------------


class LogView(ttk.Frame):
    """Read-only console pane with severity highlighting."""

    def __init__(self, master: tk.Misc, height: int = 14) -> None:
        super().__init__(master)
        self.columnconfigure(0, weight=1)
        self.rowconfigure(0, weight=1)

        self._text = tk.Text(
            self,
            height=height,
            wrap="none",
            state="disabled",
            background=_CONSOLE_BG,
            foreground=_CONSOLE_FG,
            insertbackground=_CONSOLE_FG,
            borderwidth=0,
            highlightthickness=1,
            highlightbackground="#2b313a",
            highlightcolor="#2b313a",
            padx=8,
            pady=6,
            font=font.nametofont("TkFixedFont"),
        )
        self._text.grid(row=0, column=0, sticky="nsew")

        y_scroll = ttk.Scrollbar(self, orient="vertical", command=self._text.yview)
        y_scroll.grid(row=0, column=1, sticky="ns")
        x_scroll = ttk.Scrollbar(self, orient="horizontal", command=self._text.xview)
        x_scroll.grid(row=1, column=0, sticky="ew")
        self._text.configure(yscrollcommand=y_scroll.set, xscrollcommand=x_scroll.set)

        self._text.tag_configure("warning", foreground=_CONSOLE_WARN)
        self._text.tag_configure("error", foreground=_CONSOLE_ERROR)
        self._text.tag_configure("note", foreground=_CONSOLE_NOTE)
        self._line_count = 0

    def append(self, line: str, tag: str | None = None) -> None:
        resolved = tag if tag is not None else _severity_tag(line)
        at_bottom = self._text.yview()[1] > 0.999

        self._text.configure(state="normal")
        self._text.insert("end", line + "\n", resolved or ())
        self._line_count += 1
        if self._line_count > _MAX_LOG_LINES:
            trim = self._line_count - _MAX_LOG_LINES
            self._text.delete("1.0", f"{trim + 1}.0")
            self._line_count = _MAX_LOG_LINES
        self._text.configure(state="disabled")

        if at_bottom:
            self._text.see("end")

    def clear(self) -> None:
        self._text.configure(state="normal")
        self._text.delete("1.0", "end")
        self._text.configure(state="disabled")
        self._line_count = 0

    def contents(self) -> str:
        return self._text.get("1.0", "end-1c")


def _severity_tag(line: str) -> str | None:
    upper = line.upper()
    if "| ERROR" in upper or "| CRITICAL" in upper or "TRACEBACK" in upper:
        return "error"
    if "| WARNING" in upper:
        return "warning"
    return None


class StatusChip(ttk.Frame):
    """Colored dot plus a short status label."""

    def __init__(self, master: tk.Misc) -> None:
        super().__init__(master)
        self._canvas = tk.Canvas(
            self, width=12, height=12, highlightthickness=0, borderwidth=0
        )
        # Match the themed frame so the dot does not sit on a white square.
        background = str(ttk.Style().lookup("TFrame", "background") or "")
        if background:
            self._canvas.configure(background=background)
        self._dot = self._canvas.create_oval(
            2, 2, 11, 11, fill=_STATUS_COLORS["stopped"], outline=""
        )
        self._canvas.grid(row=0, column=0, padx=(0, 6))
        self._label = ttk.Label(self, text="Stopped")
        self._label.grid(row=0, column=1)

    def set(self, state: str, text: str) -> None:
        self._canvas.itemconfigure(
            self._dot, fill=_STATUS_COLORS.get(state, _STATUS_COLORS["stopped"])
        )
        self._label.configure(text=text)


# --------------------------------------------------------------------------
# Application
# --------------------------------------------------------------------------


class LauncherApp:
    """The launcher window."""

    def __init__(
        self,
        root: tk.Tk,
        *,
        version: str,
        autostart: bool = False,
        unity_project: str | None = None,
        overrides: ServerSettings | None = None,
    ) -> None:
        self.root = root
        self.version = version
        self._stored = load_settings()

        self.server_settings = _apply_overrides(
            _apply_stored(ServerSettings(), self._stored.get("server")), overrides
        )
        self.simulator_settings = _apply_stored(
            SimulatorSettings(), self._stored.get("simulator")
        )

        self.server = ManagedProcess()
        self.simulator = ManagedProcess()
        self._ui_actions: queue.Queue[Callable[[], None]] = queue.Queue()
        self._server_state = "stopped"

        root.title(f"{APP_TITLE}  -  v{version}")
        root.geometry("820x660")
        root.minsize(720, 560)
        root.protocol("WM_DELETE_WINDOW", self._on_close)

        self._build_ui()

        project = unity_project or str(self._stored.get("unity_project") or "")
        if project:
            self.project_var.set(project)
            self._refresh_project(quiet=True)

        self.root.after(120, self._pump)
        self.root.after(200, self._offer_to_stop_stale_server)
        if autostart:
            self.root.after(600, self.start_server)

    # -- UI construction ---------------------------------------------------

    def _build_ui(self) -> None:
        self.root.columnconfigure(0, weight=1)
        self.root.rowconfigure(1, weight=1)

        header = ttk.Frame(self.root, padding=(14, 12, 14, 6))
        header.grid(row=0, column=0, sticky="ew")
        header.columnconfigure(1, weight=1)

        title_font = font.nametofont("TkDefaultFont").copy()
        title_font.configure(size=14, weight="bold")
        ttk.Label(header, text="STYLY NetSync", font=title_font).grid(
            row=0, column=0, sticky="w"
        )
        ttk.Label(
            header,
            text=f"server + Unity package v{self.version}",
            foreground="#6b7280",
        ).grid(row=0, column=1, sticky="w", padx=(10, 0))

        self.status_chip = StatusChip(header)
        self.status_chip.grid(row=0, column=2, sticky="e")

        notebook = ttk.Notebook(self.root, padding=(10, 4, 10, 10))
        notebook.grid(row=1, column=0, sticky="nsew")
        notebook.add(self._build_server_tab(notebook), text="  Server  ")
        notebook.add(self._build_unity_tab(notebook), text="  Unity Project  ")
        notebook.add(self._build_simulator_tab(notebook), text="  Simulator  ")

    def _build_server_tab(self, master: tk.Misc) -> ttk.Frame:
        tab = ttk.Frame(master, padding=12)
        tab.columnconfigure(0, weight=1)
        tab.rowconfigure(3, weight=1)

        controls = ttk.Frame(tab)
        controls.grid(row=0, column=0, sticky="ew")
        controls.columnconfigure(2, weight=1)

        self.start_button = ttk.Button(
            controls, text="Start Server", command=self.start_server, width=16
        )
        self.start_button.grid(row=0, column=0)
        self.stop_button = ttk.Button(
            controls,
            text="Stop Server",
            command=self.stop_server,
            width=16,
            state="disabled",
        )
        self.stop_button.grid(row=0, column=1, padx=(8, 0))

        addresses = ttk.LabelFrame(tab, text="Clients connect to", padding=10)
        addresses.grid(row=1, column=0, sticky="ew", pady=(12, 0))
        addresses.columnconfigure(0, weight=1)

        self.address_var = tk.StringVar(value=_format_addresses())
        ttk.Label(
            addresses,
            textvariable=self.address_var,
            font=font.nametofont("TkFixedFont"),
        ).grid(row=0, column=0, sticky="w")
        ttk.Button(addresses, text="Copy", command=self._copy_address, width=8).grid(
            row=0, column=1, sticky="e"
        )
        ttk.Label(
            addresses,
            text=(
                "Leave 'Server Address' empty on NetSyncManager to auto-discover "
                "on the LAN; otherwise paste an address above."
            ),
            foreground="#6b7280",
            wraplength=720,
            justify="left",
        ).grid(row=1, column=0, columnspan=2, sticky="w", pady=(6, 0))

        options = ttk.LabelFrame(tab, text="Ports and logging", padding=10)
        options.grid(row=2, column=0, sticky="ew", pady=(12, 0))
        for column in (1, 3, 5):
            options.columnconfigure(column, weight=1)

        self.port_vars: dict[str, tk.StringVar] = {}
        port_fields = (
            ("Control", "control_port"),
            ("Transform", "transform_port"),
            ("PUB", "pub_port"),
            ("Discovery (UDP)", "server_discovery_port"),
            ("REST API", "rest_api_port"),
        )
        for index, (label, key) in enumerate(port_fields):
            row, column = divmod(index, 3)
            var = tk.StringVar(value=str(getattr(self.server_settings, key)))
            self.port_vars[key] = var
            ttk.Label(options, text=label).grid(
                row=row, column=column * 2, sticky="w", padx=(0, 6), pady=3
            )
            ttk.Spinbox(options, from_=1, to=65535, textvariable=var, width=8).grid(
                row=row, column=column * 2 + 1, sticky="w", padx=(0, 18), pady=3
            )

        self.discovery_var = tk.BooleanVar(
            value=not self.server_settings.disable_server_discovery
        )
        ttk.Checkbutton(
            options, text="Enable LAN auto-discovery", variable=self.discovery_var
        ).grid(row=2, column=0, columnspan=3, sticky="w", pady=(8, 0))

        ttk.Label(options, text="Log level").grid(
            row=2, column=3, sticky="e", padx=(0, 6), pady=(8, 0)
        )
        self.log_level_var = tk.StringVar(value=self.server_settings.log_level_console)
        ttk.Combobox(
            options,
            textvariable=self.log_level_var,
            values=list(_LOG_LEVELS),
            state="readonly",
            width=10,
        ).grid(row=2, column=4, sticky="w", pady=(8, 0))

        log_frame = ttk.LabelFrame(tab, text="Server log", padding=(6, 6))
        log_frame.grid(row=3, column=0, sticky="nsew", pady=(12, 0))
        log_frame.columnconfigure(0, weight=1)
        log_frame.rowconfigure(0, weight=1)

        self.server_log = LogView(log_frame, height=12)
        self.server_log.grid(row=0, column=0, sticky="nsew")

        log_buttons = ttk.Frame(log_frame)
        log_buttons.grid(row=1, column=0, sticky="e", pady=(6, 0))
        ttk.Button(log_buttons, text="Copy log", command=self._copy_log).grid(
            row=0, column=0
        )
        ttk.Button(log_buttons, text="Clear", command=self.server_log.clear).grid(
            row=0, column=1, padx=(6, 0)
        )
        return tab

    def _build_unity_tab(self, master: tk.Misc) -> ttk.Frame:
        tab = ttk.Frame(master, padding=12)
        tab.columnconfigure(0, weight=1)
        tab.rowconfigure(3, weight=1)

        ttk.Label(
            tab,
            text=(
                "Point this at your Unity project. The launcher writes the "
                "OpenUPM registry and the NetSync package straight into "
                "Packages/manifest.json - no Node.js and no OpenUPM CLI."
            ),
            wraplength=740,
            justify="left",
        ).grid(row=0, column=0, sticky="w")

        picker = ttk.Frame(tab)
        picker.grid(row=1, column=0, sticky="ew", pady=(12, 0))
        picker.columnconfigure(0, weight=1)

        self.project_var = tk.StringVar()
        entry = ttk.Entry(picker, textvariable=self.project_var)
        entry.grid(row=0, column=0, sticky="ew")
        entry.bind("<Return>", lambda _event: self._refresh_project())
        ttk.Button(picker, text="Browse...", command=self._browse_project).grid(
            row=0, column=1, padx=(8, 0)
        )

        info = ttk.LabelFrame(tab, text="Detected project", padding=10)
        info.grid(row=2, column=0, sticky="ew", pady=(12, 0))
        info.columnconfigure(0, weight=1)

        self.project_info_var = tk.StringVar(value="No project selected.")
        ttk.Label(
            info,
            textvariable=self.project_info_var,
            justify="left",
            font=font.nametofont("TkFixedFont"),
        ).grid(row=0, column=0, sticky="w")

        actions = ttk.Frame(info)
        actions.grid(row=1, column=0, sticky="w", pady=(10, 0))
        self.install_button = ttk.Button(
            actions,
            text=f"Install / Update NetSync {self.version}",
            command=self._install_into_project,
            state="disabled",
        )
        self.install_button.grid(row=0, column=0)
        ttk.Button(actions, text="Re-check", command=self._refresh_project).grid(
            row=0, column=1, padx=(8, 0)
        )

        result_frame = ttk.LabelFrame(tab, text="Result", padding=(6, 6))
        result_frame.grid(row=3, column=0, sticky="nsew", pady=(12, 0))
        result_frame.columnconfigure(0, weight=1)
        result_frame.rowconfigure(0, weight=1)
        self.unity_log = LogView(result_frame, height=8)
        self.unity_log.grid(row=0, column=0, sticky="nsew")
        return tab

    def _build_simulator_tab(self, master: tk.Misc) -> ttk.Frame:
        tab = ttk.Frame(master, padding=12)
        tab.columnconfigure(0, weight=1)
        tab.rowconfigure(3, weight=1)

        ttk.Label(
            tab,
            text=(
                "Spawn simulated clients to confirm a room works before you put "
                "headsets on people."
            ),
            wraplength=740,
            justify="left",
        ).grid(row=0, column=0, sticky="w")

        options = ttk.Frame(tab)
        options.grid(row=1, column=0, sticky="ew", pady=(12, 0))
        options.columnconfigure(5, weight=1)

        ttk.Label(options, text="Clients").grid(row=0, column=0, sticky="w")
        self.sim_clients_var = tk.StringVar(value=str(self.simulator_settings.clients))
        ttk.Spinbox(
            options, from_=1, to=1000, textvariable=self.sim_clients_var, width=8
        ).grid(row=0, column=1, sticky="w", padx=(6, 18))

        ttk.Label(options, text="Server").grid(row=0, column=2, sticky="w")
        self.sim_server_var = tk.StringVar(value=self.simulator_settings.server)
        ttk.Entry(options, textvariable=self.sim_server_var, width=18).grid(
            row=0, column=3, sticky="w", padx=(6, 18)
        )

        ttk.Label(options, text="Room").grid(row=0, column=4, sticky="w")
        self.sim_room_var = tk.StringVar(value=self.simulator_settings.room)
        ttk.Entry(options, textvariable=self.sim_room_var, width=18).grid(
            row=0, column=5, sticky="w", padx=(6, 18)
        )

        advanced = ttk.LabelFrame(tab, text="Advanced", padding=10)
        advanced.grid(row=1, column=0, sticky="ew", pady=(12, 0))
        advanced.columnconfigure(5, weight=1)

        ttk.Label(advanced, text="Send rate (Hz)").grid(row=0, column=0, sticky="w")
        self.sim_rate_var = tk.StringVar(
            value=str(self.simulator_settings.transform_send_rate)
        )
        ttk.Spinbox(
            advanced,
            from_=0.5,
            to=60,
            increment=0.5,
            textvariable=self.sim_rate_var,
            width=8,
        ).grid(row=0, column=1, sticky="w", padx=(6, 18))

        ttk.Label(advanced, text="Spawn batch").grid(row=0, column=2, sticky="w")
        self.sim_batch_size_var = tk.StringVar(
            value=str(self.simulator_settings.spawn_batch_size)
        )
        ttk.Spinbox(
            advanced, from_=0, to=1000, textvariable=self.sim_batch_size_var, width=8
        ).grid(row=0, column=3, sticky="w", padx=(6, 18))

        ttk.Label(advanced, text="Batch interval (s)").grid(row=0, column=4, sticky="w")
        self.sim_batch_interval_var = tk.StringVar(
            value=str(self.simulator_settings.spawn_batch_interval)
        )
        ttk.Spinbox(
            advanced,
            from_=0,
            to=60,
            increment=0.1,
            textvariable=self.sim_batch_interval_var,
            width=8,
        ).grid(row=0, column=5, sticky="w", padx=(6, 18))

        self.sim_battery_var = tk.BooleanVar(value=self.simulator_settings.sync_battery)
        ttk.Checkbutton(
            advanced, text="Sync battery level", variable=self.sim_battery_var
        ).grid(row=1, column=0, columnspan=2, sticky="w", pady=(8, 0))

        ttk.Label(advanced, text="Log level").grid(
            row=1, column=2, sticky="w", pady=(8, 0)
        )
        self.sim_log_level_var = tk.StringVar(value=self.simulator_settings.log_level)
        ttk.Combobox(
            advanced,
            textvariable=self.sim_log_level_var,
            values=list(_SIMULATOR_LOG_LEVELS),
            state="readonly",
            width=10,
        ).grid(row=1, column=3, sticky="w", padx=(6, 18), pady=(8, 0))

        ttk.Label(
            advanced,
            text="Ports come from the Server tab, so both halves cannot disagree.",
            foreground="#6b7280",
        ).grid(row=2, column=0, columnspan=6, sticky="w", pady=(8, 0))

        buttons = ttk.Frame(tab)
        buttons.grid(row=2, column=0, sticky="w", pady=(12, 0))
        self.sim_start_button = ttk.Button(
            buttons, text="Start Simulator", command=self.start_simulator, width=16
        )
        self.sim_start_button.grid(row=0, column=0)
        self.sim_stop_button = ttk.Button(
            buttons,
            text="Stop Simulator",
            command=self.stop_simulator,
            width=16,
            state="disabled",
        )
        self.sim_stop_button.grid(row=0, column=1, padx=(8, 0))

        log_frame = ttk.LabelFrame(tab, text="Simulator log", padding=(6, 6))
        log_frame.grid(row=3, column=0, sticky="nsew", pady=(12, 0))
        log_frame.columnconfigure(0, weight=1)
        log_frame.rowconfigure(0, weight=1)
        self.simulator_log = LogView(log_frame, height=12)
        self.simulator_log.grid(row=0, column=0, sticky="nsew")
        return tab

    # -- Server ------------------------------------------------------------

    def _collect_server_settings(self) -> ServerSettings:
        settings = ServerSettings(
            disable_server_discovery=not self.discovery_var.get(),
            log_level_console=self.log_level_var.get(),
            config_file=self.server_settings.config_file,
        )
        for key, var in self.port_vars.items():
            setattr(settings, key, _parse_port(var.get(), getattr(settings, key)))
            var.set(str(getattr(settings, key)))
        return settings

    def start_server(self) -> None:
        if self.server.is_running:
            return
        self.server_settings = self._collect_server_settings()
        command = build_server_command(self.server_settings)

        self._set_server_state("starting", "Starting...")
        self.server_log.append(f"$ {' '.join(command)}", tag="note")
        try:
            self.server.start(command)
        except (OSError, RuntimeError) as exc:
            self._set_server_state("error", "Failed to start")
            self.server_log.append(f"Failed to start server: {exc}", tag="error")
            messagebox.showerror(APP_TITLE, f"Failed to start the server:\n\n{exc}")
            return

        self.address_var.set(_format_addresses())
        pid = self.server.pid
        if pid is not None:
            record_running_server(pid, self.server_settings.control_port)
        self._set_server_state("running", f"Running (pid {pid})")

    def _offer_to_stop_stale_server(self) -> None:
        """Deal with a server an earlier launcher left running.

        Without this its ports stay taken and the next Start fails with nothing
        the user can act on, because the window that owned it is gone.
        """
        stale = find_stale_server()
        if stale is None:
            return

        pid, port = stale["pid"], stale["control_port"]
        self.server_log.append(
            f"A NetSync server from an earlier session is still running "
            f"(pid {pid}, control port {port}).",
            tag="warning",
        )
        if not messagebox.askyesno(
            APP_TITLE,
            f"A NetSync server from an earlier session is still running "
            f"(pid {pid}, control port {port}).\n\n"
            "It is holding its ports, so a new server cannot start.\n\n"
            "Stop it now?",
        ):
            self.server_log.append("Left the earlier server running.", tag="note")
            return

        if stop_stale_server(pid):
            self.server_log.append(f"Stopped the earlier server (pid {pid}).", "note")
        else:
            self.server_log.append(
                f"Could not stop pid {pid}; stop it manually.", tag="error"
            )

    def stop_server(self) -> None:
        if not self.server.is_running:
            return
        self._set_server_state("starting", "Stopping...")
        self.server_log.append("Stopping server...", tag="note")
        self._run_async(self.server.stop)

    # -- Simulator ---------------------------------------------------------

    def start_simulator(self) -> None:
        if self.simulator.is_running:
            return
        self.simulator_settings = SimulatorSettings(
            clients=_parse_int(self.sim_clients_var.get(), 10, 1, 1000),
            server=self.sim_server_var.get().strip() or "localhost",
            room=self.sim_room_var.get().strip() or "default_room",
            transform_send_rate=_parse_float(self.sim_rate_var.get(), 10.0, 0.5, 60.0),
            spawn_batch_size=_parse_int(self.sim_batch_size_var.get(), 0, 0, 1000),
            spawn_batch_interval=_parse_float(
                self.sim_batch_interval_var.get(), 0.0, 0.0, 60.0
            ),
            sync_battery=self.sim_battery_var.get(),
            log_level=self.sim_log_level_var.get(),
        )
        self.sim_clients_var.set(str(self.simulator_settings.clients))
        self.sim_rate_var.set(str(self.simulator_settings.transform_send_rate))
        self.sim_batch_size_var.set(str(self.simulator_settings.spawn_batch_size))
        self.sim_batch_interval_var.set(
            str(self.simulator_settings.spawn_batch_interval)
        )
        command = build_simulator_command(
            self.simulator_settings, self._collect_server_settings()
        )
        self.simulator_log.append(f"$ {' '.join(command)}", tag="note")
        try:
            self.simulator.start(command)
        except (OSError, RuntimeError) as exc:
            self.simulator_log.append(f"Failed to start simulator: {exc}", tag="error")
            messagebox.showerror(APP_TITLE, f"Failed to start the simulator:\n\n{exc}")
            return
        self.sim_start_button.configure(state="disabled")
        self.sim_stop_button.configure(state="normal")

    def stop_simulator(self) -> None:
        if not self.simulator.is_running:
            return
        self.simulator_log.append("Stopping simulator...", tag="note")
        self._run_async(self.simulator.stop)

    # -- Unity project -----------------------------------------------------

    def _browse_project(self) -> None:
        initial = self.project_var.get() or str(Path.home())
        chosen = filedialog.askdirectory(
            title="Select your Unity project folder", initialdir=initial
        )
        if chosen:
            self.project_var.set(chosen)
            self._refresh_project()

    def _refresh_project(self, quiet: bool = False) -> None:
        path = self.project_var.get().strip()
        if not path:
            self.project_info_var.set("No project selected.")
            self.install_button.configure(state="disabled")
            return
        try:
            project = inspect_project(path)
        except UnitySetupError as exc:
            self.project_info_var.set(str(exc))
            self.install_button.configure(state="disabled")
            if not quiet:
                self.unity_log.append(str(exc), tag="error")
            return

        self.project_info_var.set(project.summary())
        self.install_button.configure(
            state="disabled" if project.is_embedded else "normal"
        )
        if project.is_embedded and not quiet:
            self.unity_log.append(
                "This project embeds the package directly; nothing to install.",
                tag="note",
            )

    def _install_into_project(self) -> None:
        path = self.project_var.get().strip()
        if not path:
            return
        try:
            project, changes = install_package(path, self.version)
        except UnitySetupError as exc:
            self.unity_log.append(str(exc), tag="error")
            messagebox.showerror(APP_TITLE, str(exc))
            return

        if not changes:
            self.unity_log.append(
                f"Already up to date: NetSync {self.version} in {project.name}.",
                tag="note",
            )
        else:
            self.unity_log.append(f"Updated {project.manifest_path}", tag="note")
            for change in changes:
                self.unity_log.append(f"  - {change}")
            self.unity_log.append(
                "Switch to the Unity Editor - it imports the package on focus.",
                tag="note",
            )
        self._refresh_project(quiet=True)

    # -- Plumbing ----------------------------------------------------------

    def _run_async(self, action: Callable[[], None]) -> None:
        def worker() -> None:
            try:
                action()
            except Exception as exc:  # noqa: BLE001 - surfaced in the UI
                message = f"Error: {exc}"
                self._ui_actions.put(
                    lambda: self.server_log.append(message, tag="error")
                )

        threading.Thread(target=worker, daemon=True).start()

    def _pump(self) -> None:
        while True:
            try:
                self._ui_actions.get_nowait()()
            except queue.Empty:
                break

        lines, _ = self.server.drain()
        for line in lines:
            self.server_log.append(line)

        sim_lines, _ = self.simulator.drain()
        for line in sim_lines:
            self.simulator_log.append(line)

        self._sync_buttons()
        self.root.after(120, self._pump)

    def _sync_buttons(self) -> None:
        running = self.server.is_running
        if running and self._server_state != "running":
            self._set_server_state("running", f"Running (pid {self.server.pid})")
        elif not running and self._server_state in {"running", "starting"}:
            self.server.clear_if_finished()
            clear_running_server()
            self._set_server_state("stopped", "Stopped")

        sim_running = self.simulator.is_running
        self.sim_start_button.configure(state="disabled" if sim_running else "normal")
        self.sim_stop_button.configure(state="normal" if sim_running else "disabled")
        if not sim_running:
            self.simulator.clear_if_finished()

    def _set_server_state(self, state: str, text: str) -> None:
        self._server_state = state
        self.status_chip.set(state, text)
        busy = state in {"running", "starting"}
        self.start_button.configure(state="disabled" if busy else "normal")
        self.stop_button.configure(state="normal" if state == "running" else "disabled")

    def _copy_address(self) -> None:
        self.root.clipboard_clear()
        self.root.clipboard_append(_primary_address())

    def _copy_log(self) -> None:
        self.root.clipboard_clear()
        self.root.clipboard_append(self.server_log.contents())

    def _persist(self) -> None:
        save_settings(
            {
                "server": asdict(self.server_settings),
                "simulator": asdict(self.simulator_settings),
                "unity_project": self.project_var.get().strip(),
            }
        )

    def _on_close(self) -> None:
        if self.server.is_running or self.simulator.is_running:
            if not messagebox.askokcancel(
                APP_TITLE, "Stop the running server and quit?"
            ):
                return
        self._persist()
        self.server.stop(timeout=4.0)
        self.simulator.stop(timeout=4.0)
        clear_running_server()
        self.root.destroy()


_SettingsT = TypeVar("_SettingsT", ServerSettings, SimulatorSettings)


def _apply_stored(base: _SettingsT, stored: object) -> _SettingsT:
    """Overlay persisted values onto *base*, ignoring anything mistyped."""
    if not isinstance(stored, dict):
        return base
    for key, value in stored.items():
        if not isinstance(key, str) or not hasattr(base, key):
            continue
        if type(value) is type(getattr(base, key)):
            setattr(base, key, value)
    return base


def _apply_overrides(
    base: ServerSettings, overrides: ServerSettings | None
) -> ServerSettings:
    """Overlay explicitly requested (non-default) command-line values."""
    if overrides is None:
        return base
    defaults = ServerSettings()
    for key, value in asdict(overrides).items():
        if value != getattr(defaults, key):
            setattr(base, key, value)
    return base


def _parse_port(text: str, fallback: int) -> int:
    return _parse_int(text, fallback, 1, 65535)


def _parse_int(text: str, fallback: int, low: int, high: int) -> int:
    try:
        value = int(text.strip())
    except ValueError:
        return fallback
    return max(low, min(high, value))


def _parse_float(text: str, fallback: float, low: float, high: float) -> float:
    try:
        value = float(text.strip())
    except ValueError:
        return fallback
    return max(low, min(high, value))


def _format_addresses() -> str:
    addresses = get_local_ip_addresses()
    if not addresses:
        return "localhost  (no LAN interface detected)"
    return "  ".join(addresses)


def _primary_address() -> str:
    addresses = get_local_ip_addresses()
    return addresses[0] if addresses else "localhost"


def acquire_single_instance() -> socket.socket | None:
    """Claim the launcher's single-instance lock.

    Returns the socket holding the claim, or ``None`` when another launcher
    already holds it. A double-click is easy to repeat by accident, and two
    launchers would fight over the same server ports.

    The caller must keep the returned socket alive for as long as it runs.
    """
    guard = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    try:
        # Deliberately no SO_REUSEADDR: the bind failing is the whole point.
        guard.bind(("127.0.0.1", _SINGLE_INSTANCE_PORT))
        guard.listen(1)
    except OSError:
        guard.close()
        return None
    return guard


def _resolve_version() -> str:
    try:
        from .server import get_version

        version = get_version()
    except Exception:  # noqa: BLE001 - version is cosmetic, never fatal
        return "unknown"
    return version


def _parse_args(argv: Sequence[str] | None) -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        prog="styly-netsync-launcher",
        description="Terminal-free setup and control GUI for STYLY NetSync.",
    )
    parser.add_argument(
        "--autostart", action="store_true", help="Start the server on launch"
    )
    parser.add_argument(
        "--unity-project", metavar="DIR", help="Preselect a Unity project folder"
    )
    parser.add_argument("--control-port", type=int, metavar="PORT")
    parser.add_argument("--transform-port", type=int, metavar="PORT")
    parser.add_argument("--pub-port", type=int, metavar="PORT")
    parser.add_argument("--server-discovery-port", type=int, metavar="PORT")
    parser.add_argument("--rest-api-port", type=int, metavar="PORT")
    parser.add_argument("--no-server-discovery", action="store_true")
    parser.add_argument("--config", metavar="FILE", help="Server TOML config file")
    parser.add_argument(
        "--allow-multiple",
        action="store_true",
        help="Permit a second launcher window (for running two servers at once)",
    )
    return parser.parse_args(argv)


def _overrides_from_args(args: argparse.Namespace) -> ServerSettings:
    overrides = ServerSettings()
    for key in (
        "control_port",
        "transform_port",
        "pub_port",
        "server_discovery_port",
        "rest_api_port",
    ):
        value = getattr(args, key, None)
        if isinstance(value, int) and 1 <= value <= 65535:
            setattr(overrides, key, value)
    if args.no_server_discovery:
        overrides.disable_server_discovery = True
    if args.config:
        overrides.config_file = args.config
    return overrides


def main(argv: Sequence[str] | None = None) -> None:
    """Entry point for the ``styly-netsync-launcher`` command."""
    args = _parse_args(argv)

    try:
        root = tk.Tk()
    except tk.TclError as exc:
        print(
            f"Cannot open a window ({exc}).\n"
            "Run 'styly-netsync-server' instead on a machine without a display.",
            file=sys.stderr,
        )
        raise SystemExit(1) from exc

    guard: socket.socket | None = None
    if not args.allow_multiple:
        guard = acquire_single_instance()
        if guard is None:
            root.withdraw()
            messagebox.showinfo(
                APP_TITLE,
                "The STYLY NetSync Launcher is already running.\n\n"
                "Look for its window, or pass --allow-multiple to open a second "
                "one on different ports.",
            )
            root.destroy()
            return

    if WINDOWS:
        try:
            ttk.Style().theme_use("vista")
        except tk.TclError:
            pass

    LauncherApp(
        root,
        version=_resolve_version(),
        autostart=args.autostart,
        unity_project=args.unity_project,
        overrides=_overrides_from_args(args),
    )
    try:
        root.mainloop()
    finally:
        if guard is not None:
            guard.close()


if __name__ == "__main__":
    main()
