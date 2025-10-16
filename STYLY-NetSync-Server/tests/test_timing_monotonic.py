#!/usr/bin/env python3
"""
Test monotonic timing behavior to verify WSL timing fix
"""

import time


def test_monotonic_time_always_increases():
    """Test that monotonic time always increases and never goes backwards"""
    times = []

    # Collect multiple time measurements
    for _ in range(100):
        times.append(time.monotonic())
        time.sleep(0.001)  # Small delay

    # Verify all times are strictly increasing
    for i in range(1, len(times)):
        assert (
            times[i] > times[i - 1]
        ), f"Monotonic time went backwards: {times[i-1]} -> {times[i]}"

    print(f"✓ Collected {len(times)} monotonic timestamps, all strictly increasing")


def test_timing_interval_calculation():
    """Test timing interval calculations using monotonic time"""
    start = time.monotonic()

    # Simulate work with sleep
    time.sleep(0.1)

    end = time.monotonic()
    interval = end - start

    # Should be approximately 0.1 seconds (with some tolerance)
    assert 0.05 <= interval <= 0.2, f"Expected ~0.1s interval, got {interval:.3f}s"
    print(f"✓ Timing interval calculation: {interval:.3f}s")


def test_monotonic_vs_system_time():
    """Compare monotonic time vs system time behavior"""
    mono_start = time.monotonic()
    sys_start = time.time()

    time.sleep(0.05)

    mono_end = time.monotonic()
    sys_end = time.time()

    mono_interval = mono_end - mono_start
    sys_interval = sys_end - sys_start

    # Both should be similar for this short interval
    assert (
        abs(mono_interval - sys_interval) < 0.01
    ), f"Large difference between monotonic ({mono_interval:.3f}s) and system ({sys_interval:.3f}s) intervals"

    print(
        f"✓ Monotonic interval: {mono_interval:.3f}s, System interval: {sys_interval:.3f}s"
    )


if __name__ == "__main__":
    test_monotonic_time_always_increases()
    test_timing_interval_calculation()
    test_monotonic_vs_system_time()
    print("All timing tests passed!")
