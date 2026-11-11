"""tests/test_paint_budget.py for the host of a friends game; the joined client
gets every paint request of the replay too.
"""

from __future__ import annotations

import pytest

from test_paint_budget import *  # noqa: F401,F403 -- the test and EXTRA_GRIDS


@pytest.fixture(scope="module")
def game(host):
    return host
