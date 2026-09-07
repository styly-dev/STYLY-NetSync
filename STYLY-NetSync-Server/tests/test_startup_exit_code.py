"""Regression test for issue #497: main() must exit non-zero on startup failure.

Previously, ``main()`` caught the ``SystemExit(1)`` raised by
``NetSyncServer.start()`` on a fatal startup abort (REST bridge failure, port
already in use) and swallowed it with a bare ``return``. That made the
process exit with code 0 even though startup failed, so supervisors
(systemd ``Restart=on-failure``), ``$?``-based deploy gates, and container
healthchecks all read the failed startup as success.
"""

from __future__ import annotations

from unittest.mock import patch

import pytest

from styly_netsync.server import NetSyncServer, main


def test_main_exits_nonzero_when_start_fails() -> None:
    """main() must propagate SystemExit(1) when NetSyncServer.start() aborts."""
    with (
        patch(
            "sys.argv",
            ["styly-netsync-server", "--no-server-discovery"],
        ),
        patch.object(NetSyncServer, "start", side_effect=SystemExit(1)) as mock_start,
        patch.object(NetSyncServer, "stop") as mock_stop,
    ):
        with pytest.raises(SystemExit) as excinfo:
            main()

    assert excinfo.value.code == 1
    mock_start.assert_called_once()
    # The finally block's teardown must still run even though we re-raise.
    mock_stop.assert_called_once()
