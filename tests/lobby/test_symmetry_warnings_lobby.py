"""The symmetry warnings of tests/test_symmetry_warnings.py for the player who
joined a friends game. The host's own edits run the offline code paths.
"""

from __future__ import annotations

import pytest

from test_symmetry_warnings import *  # noqa: F401,F403 -- the tests, their fixtures and EXTRA_GRIDS


@pytest.fixture(scope="module")
def game(joiner):
    return joiner
