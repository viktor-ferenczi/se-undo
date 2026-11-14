"""The checks before an edit of tests/test_edit_guards.py on a client of a
dedicated server with the companion. The client decides from the grid as it has
it, and sends the requests on only after the check or the player's Yes.
"""

from __future__ import annotations

from test_edit_guards import *  # noqa: F401,F403 -- the tests, their fixtures, EXTRA_GRIDS and UNDO_CONFIG

CLIENT_UNDO_CONFIG = UNDO_CONFIG  # noqa: F405
