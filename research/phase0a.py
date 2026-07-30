"""Phase 0a: label-free descriptives of the 18:00 ET reopen corpus.

Writes out/phase0a_stats.json (per-session rows, consumed by freeze_labels.py)
and ../docs/research/phase0a-report.md (versioned).
"""
import json
from datetime import datetime
from pathlib import Path

import numpy as np

import corpus
import events

DOCS = Path(__file__).parent.parent / "docs" / "research"
MIN_PRINTS_WATCH = 200          # thin-holiday exclusion, frozen (spec section 4)


def session_row(inst, d):
    a = corpus.load_session(inst, d)
    if a is None or not len(a):
        return None
    t0 = corpus.dt_to_net(datetime(d.year, d.month, d.day, 18, 0))
    watch = a[(a["ts"] >= t0 + 30 * corpus.NET_S) &
              (a["ts"] <= t0 + 930 * corpus.NET_S)]
    fc = events.first_candle(a, t0)
    if fc is None:
        return None
    h, l, r30 = fc
    pc = corpus.prev_close(inst, d)
    first_px = float(a["px"][0])
    evs, _ = events.detect_events(a, t0, k=1e9, x_s=120.0,
                                  e_floor=10**6, r30_min=0)
    evs = evs or []
    # Roll effective-spread estimator on watch-window prints
    dp = np.diff(watch["px"])
    roll = np.nan
    if len(dp) > 10:
        cov = float(np.cov(dp[1:], dp[:-1])[0, 1])
        if cov < 0:
            roll = 2 * np.sqrt(-cov) / corpus.TICK_SIZE
    m1 = a[(a["ts"] >= t0) & (a["ts"] < t0 + 60 * corpus.NET_S)]
    return {
        "date": d.isoformat(),
        "n_prints_watch": int(len(watch)),
        "n_prints_1st_min": int(len(m1)),
        "r30_ticks": int(r30),
        "gap_ticks": None if pc is None else round((first_px - pc) / corpus.TICK_SIZE),
        "n_breaks": len(evs),
        "roll_spread_ticks": None if np.isnan(roll) else round(roll, 2),
        "breaks": [{"side": e.side, "t_s": round((e.t_break -
                    (t0 + 30 * corpus.NET_S)) / corpus.NET_S, 1),
                    "max_ext_120": e.max_ext, "ret_s": e.ret_s} for e in evs],
    }


def q(vals, ps=(10, 25, 50, 75, 90)):
    v = [x for x in vals if x is not None and not (isinstance(x, float) and np.isnan(x))]
    if not v:
        return {}
    return {f"p{p}": round(float(np.percentile(v, p)), 2) for p in ps}


def main():
    stats, lines = {}, ["# Phase 0a — Descriptive Report (label-free)", ""]
    for inst in ("NQ", "MNQ"):
        rows = [r for d in sorted(corpus.sessions(inst))
                if (r := session_row(inst, d))]
        thin = [r for r in rows if r["n_prints_watch"] < MIN_PRINTS_WATCH]
        degen = [r for r in rows if r["r30_ticks"] < 4]
        usable = [r for r in rows if r["n_prints_watch"] >= MIN_PRINTS_WATCH
                  and r["r30_ticks"] >= 4]
        allbr = [b for r in usable for b in r["breaks"]]
        rets = [b["ret_s"] for b in allbr if b["ret_s"] is not None]
        stats[inst] = rows
        lines += [f"## {inst}", "",
                  f"- Sessions with a 1900-file front-month: **{len(rows)}**",
                  f"- Thin (<{MIN_PRINTS_WATCH} watch prints): {len(thin)}"
                  f" | degenerate (R30<4t): {len(degen)} | **usable: {len(usable)}**",
                  f"- R30 ticks: {q([r['r30_ticks'] for r in usable])}",
                  f"- Watch prints: {q([r['n_prints_watch'] for r in usable])}",
                  f"- First-minute prints: {q([r['n_prints_1st_min'] for r in usable])}",
                  f"- |gap| ticks: {q([abs(r['gap_ticks']) for r in usable if r['gap_ticks'] is not None])}",
                  f"- Roll spread ticks: {q([r['roll_spread_ticks'] for r in usable])}",
                  f"- Breaks/session: {q([r['n_breaks'] for r in usable])}"
                  f" (total {len(allbr)})",
                  f"- Break max-ext@120s ticks: {q([b['max_ext_120'] for b in allbr])}",
                  f"- Re-entry time s (n={len(rets)}): {q(rets)}",
                  f"- Breaks re-entering <=120s: "
                  f"{(100 * len(rets) / max(1, len(allbr))):.0f}%", ""]
    corpus.OUT.mkdir(exist_ok=True)
    (corpus.OUT / "phase0a_stats.json").write_text(json.dumps(stats))
    DOCS.mkdir(parents=True, exist_ok=True)
    (DOCS / "phase0a-report.md").write_text("\n".join(lines))
    print("\n".join(lines))


if __name__ == "__main__":
    main()
