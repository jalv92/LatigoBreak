"""Break/whipsaw event engine. One forward pass, no lookahead by construction.

Frozen definitions live in docs/specs/2026-07-30-whipsaw-detector-research-design.md
section 4; test_events.py is the executable spec.
"""
import math
from dataclasses import dataclass

import numpy as np

from corpus import NET_S, TICK_SIZE

WATCH_END_S = 930          # 18:15:30, seconds after 18:00:00
CANDLE_S = 30


@dataclass
class Event:
    side: int
    t_break: int
    i_break: int
    break_px: float
    level: float
    label: str | None = None
    t_resolve: int | None = None
    max_ext: int = 0
    ret_s: float | None = None


def first_candle(a, t0):
    m = (a["ts"] >= t0) & (a["ts"] < t0 + CANDLE_S * NET_S)
    if not m.any():
        return None
    px = a["px"][m]
    h, l = float(px.max()), float(px.min())
    return h, l, round((h - l) / TICK_SIZE)


def _ext_ticks(px, side, level):
    d = (px - level) if side > 0 else (level - px)
    return max(0, round(d / TICK_SIZE))


def detect_events(a, t0, k, x_s, e_floor=8, r30_min=4):
    fc = first_candle(a, t0)
    if fc is None:
        return None, "no prints in first candle"
    h, l, r30 = fc
    if r30 < r30_min:
        return None, f"degenerate candle R30={r30}"
    e_ticks = max(math.ceil(k * r30), e_floor)
    watch_end = t0 + WATCH_END_S * NET_S
    x_net = int(x_s * NET_S)

    evs, active = [], None
    inside_prev = True
    prev_ts = -1
    for i in range(len(a)):
        ts, px = int(a["ts"][i]), float(a["px"][i])
        assert ts >= prev_ts, "tape not time-ordered"
        prev_ts = ts
        if ts < t0 + CANDLE_S * NET_S:
            continue
        up = px >= h + TICK_SIZE - 1e-9
        dn = px <= l - TICK_SIZE + 1e-9
        inside = not (up or dn)

        if active is not None:
            if ts > active.t_break + x_net:                      # (a) timeout
                active.label = "whipsaw"
                active.t_resolve = active.t_break + x_net
                active = None
            else:
                ext = _ext_ticks(px, active.side, active.level)  # (b) extension
                if ext > active.max_ext:
                    active.max_ext = ext
                if active.max_ext >= e_ticks:
                    active.label, active.t_resolve = "real", ts
                    active = None
                else:                                            # (c) re-entry
                    re_in = (px <= h - TICK_SIZE + 1e-9) if active.side > 0 \
                        else (px >= l + TICK_SIZE - 1e-9)
                    if re_in:
                        active.label, active.t_resolve = "whipsaw", ts
                        active.ret_s = (ts - active.t_break) / NET_S
                        opp = -active.side
                        active = None
                        beyond_opp = dn if opp < 0 else up
                        if beyond_opp and ts <= watch_end:       # gap-through
                            level = l if opp < 0 else h
                            active = Event(opp, ts, i, px, level)
                            active.max_ext = _ext_ticks(px, opp, level)
                            evs.append(active)

        if active is None and inside_prev and (up or dn) and ts <= watch_end:
            side = 1 if up else -1
            level = h if up else l
            active = Event(side, ts, i, px, level)
            active.max_ext = _ext_ticks(px, side, level)
            if active.max_ext >= e_ticks:                        # giant first print
                active.label, active.t_resolve = "real", ts
                evs.append(active)
                active = None
            else:
                evs.append(active)
        inside_prev = inside

    if active is not None:                                       # tape ended
        active.label, active.t_resolve = "whipsaw", prev_ts
    return evs, None
