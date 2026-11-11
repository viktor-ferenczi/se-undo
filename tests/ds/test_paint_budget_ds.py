"""tests/test_paint_budget.py on a client of a dedicated server with the Undo
companion: the server replays the undo, and the client, which gets every paint
request the server sends, survives it.
"""

from __future__ import annotations

from test_paint_budget import *  # noqa: F401,F403 -- the test and EXTRA_GRIDS
