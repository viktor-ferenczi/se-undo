"""tests/test_paint_budget.py for the player who joined a friends game: the host
replays the undo, both games survive it.
"""

from __future__ import annotations

import pytest

from test_paint_budget import *  # noqa: F401,F403 -- the test and EXTRA_GRIDS


@pytest.fixture(scope="module")
def game(joiner):
    return joiner
