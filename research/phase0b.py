"""Phase 0b: frozen labels -> base rates, MFE/MAE, naive vs ORACLE expectancy.

G0 (kill gate): if even perfect whipsaw foresight cannot beat costs, archive.
"""
import json
from datetime import datetime
from pathlib import Path

import numpy as np

import corpus
import events

REG = json.loads((Path(__file__).parent / "preregistration.json").read_text())
DOCS = Path(__file__).parent.parent / "docs" / "research"


def sim_bracket(a, ev, h, l, rr, inst, t0):
    """Enter at break print +3 ticks adverse; bracket rr*R; stop wins ties."""
    tick = corpus.TICK_SIZE
    entry = ev.break_px + ev.side * 3 * tick
    stop = l if ev.side > 0 else h
    r_pts = abs(entry - stop)
    if r_pts < tick:
        return None
    target = entry + ev.side * rr * r_pts
    t_end = t0 + 1800 * corpus.NET_S                     # 18:30:00 time stop
    exit_px = float(a["px"][min(len(a) - 1, ev.i_break)])
    for j in range(ev.i_break + 1, len(a)):
        px = float(a["px"][j])
        if int(a["ts"][j]) > t_end:
            break
        exit_px = px
        if (ev.side > 0 and px <= stop) or (ev.side < 0 and px >= stop):
            exit_px = stop
            break
        if (ev.side > 0 and px >= target) or (ev.side < 0 and px <= target):
            exit_px = target
            break
    gross = (exit_px - entry) * ev.side * corpus.POINT_VALUE[inst]
    net = gross - corpus.COMMISSION_RT[inst]
    return {"net": net, "r": net / (r_pts * corpus.POINT_VALUE[inst])}


def mfe_mae(a, ev, minutes):
    out = {}
    t1 = ev.t_break
    for m in minutes:
        w = a[(a["ts"] > t1) & (a["ts"] <= t1 + m * 60 * corpus.NET_S)]["px"]
        if not len(w):
            out[m] = (0, 0)
            continue
        fav = (w - ev.break_px) * ev.side / corpus.TICK_SIZE
        out[m] = (round(float(fav.max()), 1), round(float(fav.min()), 1))
    return out


def stat_block(trades):
    if not trades:
        return "n=0"
    net = [t["net"] for t in trades]
    r = [t["r"] for t in trades]
    wins = sum(1 for x in net if x > 0)
    return (f"n={len(net)} | win%={100 * wins / len(net):.0f} | "
            f"E[$/trade]={np.mean(net):+.2f} (t={np.mean(net) / (np.std(net) / np.sqrt(len(net)) + 1e-9):.2f}) | "
            f"E[R]={np.mean(r):+.3f} | total=${np.sum(net):+.0f}")


def main():
    lines = [f"# Phase 0b — Base Rates & Oracle Gate", "",
             f"Frozen params: X={REG['x_timeout_s']}s, k={REG['k']}, "
             f"E_floor={REG['e_floor_ticks']}t (preregistration.json "
             f"{REG['frozen_at']})", ""]
    verdicts = {}
    for inst in ("NQ", "MNQ"):
        n_sess = n_events = 0
        whips, first_naive, first_oracle = [], {1: [], 2: []}, {1: [], 2: []}
        mfe_rows = []
        for d in sorted(corpus.sessions(inst)):
            a = corpus.load_session(inst, d)
            if a is None or len(a) < REG["min_prints_watch"]:
                continue
            t0 = corpus.dt_to_net(datetime(d.year, d.month, d.day, 18, 0))
            evs, reason = events.detect_events(
                a, t0, k=REG["k"], x_s=REG["x_timeout_s"],
                e_floor=REG["e_floor_ticks"], r30_min=REG["r30_min_ticks"])
            if evs is None or not evs:
                continue
            watch_n = int(((a["ts"] >= t0 + 30 * corpus.NET_S) &
                           (a["ts"] <= t0 + 930 * corpus.NET_S)).sum())
            if watch_n < REG["min_prints_watch"]:
                continue
            n_sess += 1
            n_events += len(evs)
            whips += [e.label == "whipsaw" for e in evs]
            fc = events.first_candle(a, t0)
            h, l, _ = fc
            for e in evs:
                mfe_rows.append(mfe_mae(a, e, REG["horizons_min"]))
            reals = [e for e in evs if e.label == "real"]
            for rr in (1, 2):
                t = sim_bracket(a, evs[0], h, l, rr, inst, t0)
                if t:
                    first_naive[rr].append(t)
                if reals:
                    t = sim_bracket(a, reals[0], h, l, rr, inst, t0)
                    if t:
                        first_oracle[rr].append(t)
        wr = 100 * np.mean(whips) if whips else float("nan")
        lines += [f"## {inst}", "",
                  f"- Usable sessions: {n_sess} | events: {n_events} "
                  f"({n_events / max(1, n_sess):.2f}/session) | "
                  f"**whipsaw rate: {wr:.0f}%**", ""]
        for m in REG["horizons_min"]:
            fav = [r[m][0] for r in mfe_rows]
            adv = [r[m][1] for r in mfe_rows]
            lines.append(f"- MFE/MAE @{m}min (ticks, all events): "
                         f"+{np.median(fav):.0f}/{np.median(adv):.0f} (median)")
        lines.append("")
        for rr in (1, 2):
            lines.append(f"- NAIVE  {rr}R bracket: {stat_block(first_naive[rr])}")
            lines.append(f"- ORACLE {rr}R bracket: {stat_block(first_oracle[rr])}")
        best = max(np.mean([t['net'] for t in first_oracle[rr]])
                   if first_oracle[rr] else -1e9 for rr in (1, 2))
        verdicts[inst] = best > 0
        lines += ["", f"**Best oracle E[$/trade]: {best:+.2f} -> "
                  f"{'PASS' if best > 0 else 'FAIL'}**", ""]
    g0 = verdicts.get("NQ", False)
    lines += ["## G0 verdict", "",
              f"**G0 {'PASS' if g0 else 'FAIL'}** (NQ primary; MNQ replication "
              f"{'PASS' if verdicts.get('MNQ') else 'FAIL'}). " +
              ("Proceed to Phase 1." if g0 else
               "Per spec section 7: archive the project honestly.")]
    DOCS.mkdir(parents=True, exist_ok=True)
    (DOCS / "phase0b-report.md").write_text("\n".join(lines))
    print("\n".join(lines))


if __name__ == "__main__":
    main()
