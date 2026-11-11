"""The Paint Replacer symmetry tests of tests/test_paint_replacer_symmetry.py for the player who
joined a friends game; the host records the repaint for that player.
"""

from __future__ import annotations

import pytest

import lobby_rig
from test_paint_replacer_symmetry import *  # noqa: F401,F403 -- the tests, fixtures and PLUGINS


@pytest.fixture(scope="module")
def game(joiner):
    return joiner


def STORE_FOLDERS():
    return list(
        (lobby_rig.HOST.appdata / "Undo" / "Worlds").glob(
            f"*/players/{lobby_rig.JOIN_ID}/grids"
        )
    )
