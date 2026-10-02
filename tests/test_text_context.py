"""The text context outside the terminal: single line text boxes of other screens
have the same undo, each box its own history, and the keys never reach the build
history while a box has the cursor.
"""

from __future__ import annotations

import time

import pytest

import rig
from harness import wait_until

# Longer than the coalescing window of 500 ms
PAUSE = 0.8
SCREENS = [("G", "MyGuiScreenCubeBuilder"), ("Enter", "MyGuiScreenChat")]


class Box:
    """The text box a screen opens with the cursor in"""

    def __init__(self, game, key: str, kind: str):
        api = game.api
        self.game = game
        rig.focus_gameplay(api)
        api.key(key)
        wait_until(lambda: (game.screen(kind) or {}).get("hasFocus"), kind)
        time.sleep(1)  # the opening transition takes no input
        self.screen = next(
            i for i, s in enumerate(api.list_screens()) if s.get("type") == kind
        )
        focused = api.get_focus()["control"]
        assert focused["type"] == "MyGuiControlTextbox", focused
        self.ident = focused["id"]

    def text(self) -> str:
        control = self.game.control(self.screen, ident=self.ident)
        return control["properties"]["text"]

    def type(self, text: str, expect: str) -> None:
        self.game.api.type_text(text)
        wait_until(lambda: self.text() == expect, f"{expect!r} typed")

    def press(self, binding, expect: str) -> None:
        self.game.api.key(*binding)
        wait_until(lambda: self.text() == expect, f"{expect!r} after {binding}")


@pytest.fixture(scope="module", autouse=True)
def a_step_in_the_build_history(game):
    """Something Ctrl-Z would undo if it got through to gameplay"""
    game.leave_seat()
    game.build_block(rig.BUILD_CELL)


@pytest.mark.parametrize("key, kind", SCREENS)
def test_text_box_of_another_screen(game, key, kind):
    box = Box(game, key, kind)
    status = game.status()
    try:
        assert box.text() == ""
        box.type("light", "light")
        time.sleep(PAUSE)
        box.type(" armor", "light armor")

        box.press(game.undo_key, "light")
        box.press(game.undo_key, "")
        box.press(game.redo_key, "light")
        box.press(game.redo_key, "light armor")

        # More presses than steps, both ways: the text stays at the ends
        for _ in range(3):
            game.api.key(*game.redo_key)
        time.sleep(0.5)
        assert box.text() == "light armor"
        box.press(game.undo_key, "light")
        box.press(game.undo_key, "")
        for _ in range(3):
            game.api.key(*game.undo_key)
        time.sleep(0.5)
        assert box.text() == ""

        # None of it reached the build history
        assert game.status() == status
        assert game.exists(rig.BUILD_CELL)
    finally:
        rig.focus_gameplay(game.api)


def test_typing_without_a_pause_is_one_step(game):
    box = Box(game, *SCREENS[0])
    try:
        box.type("ab", "ab")
        box.type("cd", "abcd")
        time.sleep(PAUSE)
        box.type("ef", "abcdef")
        box.press(game.undo_key, "abcd")
        box.press(game.undo_key, "")
    finally:
        rig.focus_gameplay(game.api)


def test_typing_after_an_undo_drops_the_redo(game):
    box = Box(game, *SCREENS[0])
    try:
        box.type("a", "a")
        time.sleep(PAUSE)
        box.type("b", "ab")
        box.press(game.undo_key, "a")
        time.sleep(PAUSE)
        box.type("c", "ac")
        game.api.key(*game.redo_key)
        time.sleep(0.5)
        assert box.text() == "ac"
        box.press(game.undo_key, "a")
        box.press(game.undo_key, "")
    finally:
        rig.focus_gameplay(game.api)


def test_a_new_box_starts_with_an_empty_history(game):
    """The history dies with its text box. The screen opened again has a new one."""
    first = Box(game, *SCREENS[1])
    first.type("hello", "hello")
    rig.focus_gameplay(game.api)

    second = Box(game, *SCREENS[1])
    try:
        before = second.text()
        game.api.key(*game.undo_key)
        game.api.key(*game.redo_key)
        time.sleep(0.5)
        assert second.text() == before
    finally:
        rig.focus_gameplay(game.api)


def test_gameplay_has_the_keys_back_after_the_screen_closed(game):
    Box(game, *SCREENS[0])
    rig.focus_gameplay(game.api)
    assert game.undo() == "Undo: placed 1 block"
    wait_until(lambda: not game.exists(rig.BUILD_CELL), "the block to go")
