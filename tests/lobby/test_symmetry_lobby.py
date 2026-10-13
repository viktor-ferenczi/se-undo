"""The symmetry tests of tests/test_symmetry.py for the player who joined a
friends game; the host records and replays for that player. The host's own
symmetry steps run the offline code paths, tests/test_symmetry.py covers them.
"""

from __future__ import annotations

import pytest

from test_symmetry import *  # noqa: F401,F403 -- the tests, their fixture and EXTRA_GRIDS


@pytest.fixture(scope="module")
def game(joiner):
    return joiner
