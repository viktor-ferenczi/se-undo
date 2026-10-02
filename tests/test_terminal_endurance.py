"""Terminal recording over many changes: a combobox, and one slider changed more
often than the 30 calls after which the runtime compiles a method again. That is
where the recording used to stop while it hung on a patch of the generic SetValue
(SE1-0079).
"""

from __future__ import annotations

import time

import pytest

import rig
from harness import wait_until

STAND = (2, 1.0, 14)
AIM = (2, 1.3, 15)
CONTROLLER_NAME = "Undo Turret Controller"
BURSTS = 12
BURST = 4


@pytest.fixture(scope="module", autouse=True)
def at_the_turret_controller(game):
    game.stand_at(STAND)


def test_combobox_through_the_terminal(game):
    label = f"changed Content of {CONTROLLER_NAME}"
    with game.open_terminal(AIM) as screen:

        def selected():
            return game.control(screen, "Content")["properties"]["selectedKey"]

        before = selected()
        for key in (1, before, 1):
            last = game.last_node_id("terminal")
            game.api.control_set("Content", key, screen=screen)
            wait_until(lambda: selected() == key, "the combobox")
            game.wait_recorded(last, label, history="terminal")

        assert game.undo() == f"Undo: {label}"
        wait_until(lambda: selected() == before, "the combobox to follow the undo")
        assert game.redo() == f"Redo: {label}"
        wait_until(lambda: selected() == 1, "the combobox to follow the redo")


def test_slider_changes_keep_being_recorded(game):
    """Twelve short drags of one slider, a node each"""
    api = game.api
    label = f"changed Radius of {rig.TARGET_NAME}"
    value = 1.0
    for burst in range(BURSTS):
        last = game.last_node_id("terminal")
        for _ in range(BURST):
            value = 1.0 + (value + 0.37) % 15
            api.set_property(game.station, rig.TARGET, "Radius", value)
        try:
            game.wait_recorded(last, label, history="terminal")
        except AssertionError:
            raise AssertionError(
                f"not recorded after {burst * BURST} changes of the slider"
            ) from None
        time.sleep(0.6)
