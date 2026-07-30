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


# --- grid runner -----------------------------------------------------------
import json
from datetime import datetime
from pathlib import Path

import numpy as np

import phase0b

DOCS = Path(__file__).parent.parent / "docs" / "research"
GRID_HOLD = phase0b.REG["phase1_grid"]["hold_s"]
GRID_EXT = phase0b.REG["phase1_grid"]["ext_r30"]
MIN_CAL_ENTRIES = 15


def usable_dates(inst):
    out = []
    for d in sorted(corpus.sessions(inst)):
        a = corpus.load_session(inst, d)
        if a is None or len(a) < phase0b.REG["min_prints_watch"]:
            continue
        t0 = corpus.dt_to_net(datetime(d.year, d.month, d.day, 18, 0))
        watch_n = int(((a["ts"] >= t0 + 30 * corpus.NET_S) &
                       (a["ts"] <= t0 + 930 * corpus.NET_S)).sum())
        if watch_n >= phase0b.REG["min_prints_watch"]:
            out.append(d)
    return out


def run_cell(inst, dates, hold_s, ext_r30):
    """One trade max per session; returns list of per-trade dicts."""
    trades = []
    for d in dates:
        a = corpus.load_session(inst, d)
        t0 = corpus.dt_to_net(datetime(d.year, d.month, d.day, 18, 0))
        evs, _ = events.detect_events(
            a, t0, k=phase0b.REG["k"], x_s=phase0b.REG["x_timeout_s"],
            e_floor=phase0b.REG["e_floor_ticks"],
            r30_min=phase0b.REG["r30_min_ticks"])
        if not evs:
            continue
        fc = events.first_candle(a, t0)
        h, l, r30 = fc
        deadline = t0 + ENTRY_DEADLINE_S * corpus.NET_S
        for ev in evs:
            j = confirm_index(a, ev, h, l, r30, hold_s, ext_r30, deadline)
            if j is None:
                continue
            shim = events.Event(ev.side, int(a["ts"][j]), j,
                                float(a["px"][j]), ev.level)
            res = {rr: phase0b.sim_bracket(a, shim, h, l, rr, inst, t0)
                   for rr in (1, 2)}
            if res[1] is None:
                break                          # stop already crossed: no trade
            res["chase_ticks"] = round((float(a["px"][j]) - ev.level)
                                       * ev.side / corpus.TICK_SIZE)
            res["date"] = d.isoformat()
            trades.append(res)
            break                              # one entry per session
    return trades


def cell_stats(trades, n_sessions):
    r1 = [t[1]["r"] for t in trades]
    return {"n": len(trades),
            "win": round(100 * np.mean([t[1]["net"] > 0 for t in trades]), 0) if trades else 0,
            "er_trade": round(float(np.mean(r1)), 3) if trades else 0.0,
            "er_sess": round(float(np.sum(r1)) / max(1, n_sessions), 4),
            "usd_trade": round(float(np.mean([t[1]["net"] for t in trades])), 2) if trades else 0.0,
            "er2_sess": round(float(np.sum([t[2]["r"] for t in trades if t[2]]))
                              / max(1, n_sessions), 4),
            "chase_med": float(np.median([t["chase_ticks"] for t in trades])) if trades else 0.0}


def main():
    dates = usable_dates("NQ")
    n_cal = int(len(dates) * 0.6)
    cal, val = dates[:n_cal], dates[n_cal:]
    rows = {}
    for hs in GRID_HOLD:
        for ex in GRID_EXT:
            key = f"h{hs}_x{ex}"
            rows[key] = {"hold": hs, "ext": ex,
                         "cal": cell_stats(run_cell("NQ", cal, hs, ex), len(cal)),
                         "val": cell_stats(run_cell("NQ", val, hs, ex), len(val))}
            print(key, "cal", rows[key]["cal"], "val", rows[key]["val"], flush=True)
    eligible = {k: v for k, v in rows.items() if v["cal"]["n"] >= MIN_CAL_ENTRIES}
    best_key = max(eligible, key=lambda k: eligible[k]["cal"]["er_sess"])
    best = rows[best_key]
    naive = rows["h0_x0"]
    g1 = (best["val"]["er_sess"] > 0
          and best["val"]["er_sess"] > naive["val"]["er_sess"])
    mnq_dates = usable_dates("MNQ")
    mnq = cell_stats(run_cell("MNQ", mnq_dates, best["hold"], best["ext"]),
                     len(mnq_dates))

    corpus.OUT.mkdir(exist_ok=True)
    (corpus.OUT / "phase1_cells.json").write_text(json.dumps(rows))
    hdr = ("| cell | cal n | cal E[R]/sess | val n | val E[R]/sess | "
           "val E[R]/trade | val win% | val $/trade | chase med (t) |")
    sep = "|" + "---|" * 9

    def fmt(k, v):
        return (f"| {k} | {v['cal']['n']} | {v['cal']['er_sess']:+.4f} | "
                f"{v['val']['n']} | {v['val']['er_sess']:+.4f} | "
                f"{v['val']['er_trade']:+.3f} | {v['val']['win']:.0f} | "
                f"{v['val']['usd_trade']:+.2f} | {v['val']['chase_med']:.0f} |")

    lines = ["# Phase 1 — Detector Grid & G1 Verdict", "",
             f"NQ usable sessions: {len(dates)} (cal {len(cal)} / val {len(val)}, "
             f"chronological; split date {val[0].isoformat()}). "
             f"Eligibility: >={MIN_CAL_ENTRIES} cal entries. "
             f"Entry deadline 18:20:00 ET (pre-registered).", "",
             hdr, sep] + [fmt(k, v) for k, v in rows.items()] + [
             "",
             f"**Selected on calibration: {best_key}** (hold={best['hold']}s, "
             f"ext={best['ext']}xR30) -> validation E[R]/session = "
             f"{best['val']['er_sess']:+.4f} vs naive {naive['val']['er_sess']:+.4f}.",
             "",
             f"MNQ replication ({len(mnq_dates)} sessions, no split): "
             f"n={mnq['n']}, E[R]/session={mnq['er_sess']:+.4f}, "
             f"E[$/trade]={mnq['usd_trade']:+.2f}.",
             "",
             "## G1 verdict", "",
             f"**G1 {'PASS' if g1 else 'FAIL'}** — chosen cell validation "
             f"E[R]/session = {best['val']['er_sess']:+.4f} "
             f"({'>' if best['val']['er_sess'] > 0 else '<='} 0 [required]; "
             f"{'beats' if best['val']['er_sess'] > naive['val']['er_sess'] else 'does not beat'}"
             f" naive {naive['val']['er_sess']:+.4f} [required]). " +
             ("Proceed to Phase 2 (order-flow overlay)." if g1 else
              "Per spec section 7: archive or redesign the signal; no re-tuning "
              "past the split.")]
    DOCS.mkdir(parents=True, exist_ok=True)
    (DOCS / "phase1-report.md").write_text("\n".join(lines))
    print("\n".join(lines[-12:]))


if __name__ == "__main__":
    main()
