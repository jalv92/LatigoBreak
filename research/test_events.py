"""Synthetic-tape tests: the executable spec of the event engine."""
from datetime import datetime

import numpy as np
import pytest

import corpus
import events

T0 = corpus.dt_to_net(datetime(2026, 3, 2, 18, 0, 0))
S = corpus.NET_S


def tape(*rows):
    """rows = (seconds_after_1800, px). Candle rows included by caller."""
    return np.array([(T0 + int(s * S), px, 0, 1) for s, px in rows],
                    dtype=corpus.DTYPE)

# Base candle: H=20002.0, L=20000.0 -> R30 = 8 ticks, E = max(8,8) = 8 ticks (2.0 pts)
CANDLE = [(0, 20000.0), (5, 20002.0), (12, 20000.5), (29, 20001.0)]


def run(*watch, k=1.0, x=60.0):
    evs, reason = events.detect_events(tape(*CANDLE, *watch), T0, k=k, x_s=x)
    assert reason is None
    return evs


def test_first_candle():
    h, l, r30 = events.first_candle(tape(*CANDLE, (40, 21000.0)), T0)
    assert (h, l, r30) == (20002.0, 20000.0, 8)


def test_real_break_up():
    evs = run((40, 20002.25), (45, 20003.0), (50, 20004.0))
    assert len(evs) == 1
    e = evs[0]
    assert e.side == 1 and e.level == 20002.0 and e.label == "real"
    assert e.max_ext == 8 and e.break_px == 20002.25


def test_whipsaw_reentry():
    evs = run((40, 20002.25), (43, 20001.75))
    assert evs[0].label == "whipsaw" and evs[0].ret_s == pytest.approx(3.0)


def test_whipsaw_timeout_without_extension():
    # stays 1-2 ticks outside, never reaches E=8, first print after X=60 resolves
    evs = run((40, 20002.25), (70, 20002.5), (105, 20003.0))
    e = evs[0]
    assert e.label == "whipsaw" and e.ret_s is None
    assert e.t_resolve == e.t_break + 60 * S


def test_rearm_after_whipsaw_and_down_break():
    evs = run((40, 20002.25), (43, 20001.75),        # whipsaw up
              (50, 19999.75), (55, 19997.75))        # real down (ext 9 >= 8)
    assert [e.side for e in evs] == [1, -1]
    assert [e.label for e in evs] == ["whipsaw", "real"]
    assert evs[1].level == 20000.0


def test_no_new_breaks_after_watch_end_but_active_resolves():
    evs = run((925, 20002.25), (945, 20004.25))      # opens at 925, real at 945
    assert len(evs) == 1 and evs[0].label == "real"
    evs2 = run((935, 20002.25), (940, 20004.25))     # break attempt after 930s
    assert evs2 == []


def test_gap_through_resolves_and_opens_opposite():
    evs = run((40, 20002.25), (42, 19999.5), (44, 19997.5))
    assert [e.side for e in evs] == [1, -1]
    assert evs[0].label == "whipsaw"
    assert evs[1].t_break == T0 + 42 * S             # same print opens the down event
    assert evs[1].label == "real"


def test_degenerate_candle_excluded():
    a = tape((0, 20000.0), (10, 20000.25), (29, 20000.0), (40, 20001.0))
    evs, reason = events.detect_events(a, T0, k=1.0, x_s=60.0)
    assert evs is None and "degenerate" in reason


def test_non_monotonic_tape_raises():
    a = tape(*CANDLE, (50, 20002.25), (45, 20003.0))
    with pytest.raises(AssertionError):
        events.detect_events(a, T0, k=1.0, x_s=60.0)


def test_tape_end_with_active_event_is_whipsaw():
    evs = run((40, 20002.25), (41, 20002.5))
    assert evs[0].label == "whipsaw"
