"""Causal detector tests on synthetic tape (reuses test_events fixtures)."""
import corpus
import events
import phase1
from test_events import CANDLE, T0, tape

S = corpus.NET_S
DEADLINE = T0 + 1200 * S


def _first_event(a, **kw):
    evs, reason = events.detect_events(a, T0, k=1.0, x_s=30.0, **kw)
    assert reason is None and evs
    return evs[0]


def test_naive_cell_confirms_at_break_print():
    a = tape(*CANDLE, (40, 20002.25), (45, 20003.0))
    ev = _first_event(a)
    j = phase1.confirm_index(a, ev, 20002.0, 20000.0, 8, 0, 0.0, DEADLINE)
    assert j == ev.i_break


def test_hold_waits_and_confirms():
    a = tape(*CANDLE, (40, 20002.25), (43, 20002.5), (46, 20002.75))
    ev = _first_event(a)
    j = phase1.confirm_index(a, ev, 20002.0, 20000.0, 8, 5, 0.0, DEADLINE)
    assert j == ev.i_break + 2          # first print with ts-t_break >= 5s


def test_reentry_vetoes_before_hold():
    a = tape(*CANDLE, (40, 20002.25), (43, 20001.75), (50, 20002.5))
    ev = _first_event(a)
    assert phase1.confirm_index(a, ev, 20002.0, 20000.0, 8, 5, 0.0, DEADLINE) is None


def test_extension_requirement_uses_cumulative_max():
    # ext_r30=0.5 -> ceil(0.5*8)=4 ticks; reached at 44s, confirm there (hold=0)
    a = tape(*CANDLE, (40, 20002.25), (44, 20003.0), (48, 20002.25))
    ev = _first_event(a)
    j = phase1.confirm_index(a, ev, 20002.0, 20000.0, 8, 0, 0.5, DEADLINE)
    assert j == ev.i_break + 1


def test_deadline_forfeits():
    a = tape(*CANDLE, (40, 20002.25), (1250, 20002.5))
    ev = _first_event(a)
    assert phase1.confirm_index(a, ev, 20002.0, 20000.0, 8, 20, 0.0, DEADLINE) is None
