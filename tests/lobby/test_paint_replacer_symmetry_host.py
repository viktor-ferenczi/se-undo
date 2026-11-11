"""The Paint Replacer symmetry tests of tests/test_paint_replacer_symmetry.py for the host of a
friends game.
"""

from __future__ import annotations

import pytest

import lobby_rig
from test_paint_replacer_symmetry import *  # noqa: F401,F403 -- the tests, fixtures and PLUGINS


@pytest.fixture(scope="module")
def game(host):
    return host


def STORE_FOLDERS():
    return list((lobby_rig.HOST.appdata / "Undo" / "Worlds").glob("*/grids"))
