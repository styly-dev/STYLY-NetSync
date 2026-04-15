"""State relay stub.

The state relay validates incoming STATE_BATCH frames against the
current authority epoch, coalesces pose updates, and fans them back
out to subscribers on the room's state-plane topic.

Populated in a later phase.
"""
