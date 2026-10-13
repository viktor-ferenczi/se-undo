"""The symmetry tests of tests/test_symmetry.py on a client of a dedicated server
with the companion. The client mirrors the cells and sends the copies in its build
and removal requests; the companion records them for the player and replays them.
"""

from __future__ import annotations

from test_symmetry import *  # noqa: F401,F403 -- the tests, their fixture and EXTRA_GRIDS
