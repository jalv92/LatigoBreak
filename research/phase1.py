"""Phase 1: causal price/time confirmation detector + 24-cell grid runner.

Detector is label-blind: it sees the tape after a break and confirms when
hold AND extension conditions are met before any re-entry (>=1 tick inside).
"""
import math

import corpus
import events

ENTRY_DEADLINE_S = 1200            # 18:20:00 ET, pre-registered in the plan
EPS = 1e-9


def confirm_index(a, ev, h, l, r30, hold_s, ext_r30, deadline_net):
    """Tape index of the confirmation print, or None (veto/deadline)."""
    need_ext = math.ceil(ext_r30 * r30)
    hold_net = int(hold_s * corpus.NET_S)
    max_ext = 0
    for j in range(ev.i_break, len(a)):
        ts, px = int(a["ts"][j]), float(a["px"][j])
        if ts > deadline_net:
            return None
        ext = events._ext_ticks(px, ev.side, ev.level)
        if ext > max_ext:
            max_ext = ext
        re_in = (px <= h - corpus.TICK_SIZE + EPS) if ev.side > 0 \
            else (px >= l + corpus.TICK_SIZE - EPS)
        if re_in:
            return None
        if ts - ev.t_break >= hold_net and max_ext >= need_ext:
            return j
    return None
