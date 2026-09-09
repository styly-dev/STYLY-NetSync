#!/bin/bash
# STYLY NetSync Launcher - double-click on macOS, or run on Linux.
#
# Installs uv on first use (user-level, no sudo), then opens the setup GUI.
# macOS shows a Terminal window while this runs; the GUI itself is a normal
# window and the Terminal can be closed once it appears.
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repo_root="$(cd "${script_dir}/../.." && pwd)"

find_uv() {
    if command -v uv >/dev/null 2>&1; then command -v uv; return 0; fi
    for candidate in "$HOME/.local/bin/uv" /opt/homebrew/bin/uv /usr/local/bin/uv; do
        if [ -x "$candidate" ]; then echo "$candidate"; return 0; fi
    done
    return 1
}

uv_bin="$(find_uv || true)"
if [ -z "${uv_bin}" ]; then
    echo "STYLY NetSync needs the 'uv' runtime (user-level install, no sudo)."
    read -r -p "Install it now? [y/N] " reply
    case "${reply}" in
        [yY]*) curl -LsSf https://astral.sh/uv/install.sh | sh ;;
        *) echo "Cancelled."; exit 1 ;;
    esac
    uv_bin="$(find_uv || true)"
    if [ -z "${uv_bin}" ]; then
        echo "uv is still not on PATH. See https://docs.astral.sh/uv/getting-started/installation/"
        exit 1
    fi
fi

# Prefer this checkout when the script sits inside the repository.
server_dir="${repo_root}/STYLY-NetSync-Server"
if [ -f "${server_dir}/pyproject.toml" ]; then
    # uv caches the build of a local directory and does not notice edits to it,
    # so without --reinstall a checkout keeps running whatever it built first.
    from_args=(--reinstall --from "${server_dir}")
else
    version="latest"
    package_json="${repo_root}/STYLY-NetSync-Unity/Packages/com.styly.styly-netsync/package.json"
    if [ -f "${package_json}" ]; then
        version="$(sed -n 's/.*"version"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "${package_json}" | head -n 1)"
        version="${version:-latest}"
    fi
    from_args=(--exclude-newer "5 days"
               --exclude-newer-package "styly-netsync-server=2999-12-31"
               --from "styly-netsync-server@${version}")
fi

echo "Starting the STYLY NetSync Launcher..."
exec "${uv_bin}" tool run "${from_args[@]}" styly-netsync-launcher "$@"
