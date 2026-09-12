#!/usr/bin/env python3
"""LatigoBreak on the NT8 tick tape: 18:00 ET window, 1 NQ, $500 target, ATR stop.

    python3 tape_run.py                 # ALL (in-sample) + NQ 09-26 after ALL's end (OOS)
    python3 tape_run.py --stop 1.5 2.0  # stop multiples to run (default both)

Uses `engine.LatigoBreak` (the closed mirror of LatigoBreakStrategy.cs v3) for
the ENTRIES, then swaps the ATR target for a fixed $500 (25 NQ points) before
the fills. Governor off, one trade per window, no time stop (flat at the 17:00
halt by the tape's own gap). 30-second bars, as the chart the ATR reads.

Rows per stop multiple:
  signal   whipsaw filter ON (hold 30 s + extension 0.25 R30), $500 target
  fade     opposite side at the same tick, mirrored bracket
  naive    filter OFF: chase the break print, same bracket
  atr_tgt  signal with the strategy's own 2xATR target instead of $500
Then the feature breakdown of the `signal` rows: which breaks paid.
"""
import argparse, sys, time
import numpy as np
sys.path.insert(0, "/home/javlo/Code Projects/main-project/projects/Trading/PropSim")
import engine, tape as tp  # noqa: E402
COSTS = engine.Costs(commission=5.76, slippage_ticks=2)
TF = 30
TARGET_USD = 500.0
W0 = 18 * 3600          # window open, seconds of day ET
CANDLE = 30


def stats(trades):
    if not len(trades):
        return "n=  0 (no trades)"
    p = np.array([t.pnl for t in trades]); gp, gl = p[p > 0].sum(), -p[p < 0].sum()
    pf = gp / gl if gl else float("inf")
    why = {}
    for t in trades: why[t.reason] = why.get(t.reason, 0) + 1
    gate = "PASS" if len(p) >= 100 and p.mean() >= 30 and pf >= 1.3 else ("n<100" if len(p) < 100 else "FAIL")
    return (f"n={len(p):4d} win={np.mean(p > 0):4.0%} net=${p.sum():8,.0f} avg=${p.mean():6,.0f} "
            f"PF={pf:5.2f} worstMAE=${min(t.mae for t in trades):7,.0f} gate={gate:5} {why}")


def base_params(S, stop_mult):
    p = {k: v.default for k, v in S.params.items()}
    p.update(trade_globex_reopen=1, trade_evening=0, trade_us_open=0, contracts=1,
             daily_profit_target=0, daily_loss_limit=0, atr_stop_mult=stop_mult,
             max_trades_per_window=1, use_breakeven=0)
    return p


def run(ctx, S, stop_mult):
    t = ctx["tape"]; px = t["px"]
    s = S(); s.tick, s.point_value = engine.instrument(ctx["contract"])[::-1]
    pts = TARGET_USD / s.point_value
    kw = dict(timeout_min=23 * 60)
    out = {}
    p = base_params(S, stop_mult)
    ei, dr, st, tg_atr, _, _ = s.entries(ctx["bars"], t, p)
    e = px[ei].astype(float); tg = e + dr * pts
    out["signal"] = engine.resolve(t, ei, dr, st, tg, COSTS, **kw)
    out["fade"] = engine.resolve(t, ei, -dr, e + (e - st), e - dr * pts, COSTS, **kw)
    q = dict(p, use_whipsaw_filter=0)
    ei2, dr2, st2, _, _, _ = s.entries(ctx["bars"], t, q)
    out["naive"] = engine.resolve(t, ei2, dr2, st2, px[ei2].astype(float) + dr2 * pts, COSTS, **kw)
    out["atr_tgt"] = engine.resolve(t, ei, dr, st, tg_atr, COSTS, **kw)
    return out, (ei, dr, st, s)


def features(ctx, ei, dr, st, s):
    """Per-trade facts known AT the entry tick. Nothing after it is read."""
    t = ctx["tape"]; ts, px, vol, side = t["ts"], t["px"], t["vol"], t["side"]
    day = tp.day_index(ts); tick = s.tick
    rows = []
    for k, e in enumerate(ei):
        dk = int(dr[k]); d = int(day[e]); w0 = (int(d) * 86400 + tp.NET_EPOCH_S + W0) * tp.TPS
        a = int(np.searchsorted(ts, w0)); c = int(np.searchsorted(ts, w0 + CANDLE * tp.TPS))
        h, l = float(px[a:c].max()), float(px[a:c].min())
        level = h if dk > 0 else l
        # first print through the level after the candle: the break tick
        seg = px[c:e + 1]
        brk = c + int(np.flatnonzero(seg >= h + tick / 2)[0] if dk > 0 else np.flatnonzero(seg <= l - tick / 2)[0])
        prev_close = float(px[a - 1]) if a > 0 else np.nan
        cd = int((vol[a:c] * side[a:c]).sum()); bd = int((vol[brk:e + 1] * side[brk:e + 1]).sum())
        rows.append(dict(
            r30=(h - l) / tick, atr_t=abs(px[e] - st[k]) / tick / 1.0,   # stop distance in ticks
            delay_s=(ts[e] - ts[c]) / tp.TPS, chase=dk * (float(px[e]) - level) / tick,
            side=dk, cvol=int(vol[a:c].sum()), cdelta=cd * dk, bdelta=bd * dk,
            gap=dk * (float(px[a]) - prev_close) / tick, wday=(d + 3) % 7))     # 1970-01-01 = Thu
    return rows


def breakdown(rows, trades, name):
    p = np.array([t.pnl for t in trades]); n = len(p)
    print(f"\n-- {name}: which breaks paid (n={n}, terciles of each feature at entry time)")
    for f, lab in (("r30", "opening 30s range, ticks"), ("atr_t", "stop distance (ATR x mult), ticks"),
                   ("delay_s", "seconds from candle close to entry"), ("chase", "entry beyond the level, ticks"),
                   ("cvol", "volume in the 30s candle"), ("cdelta", "candle delta in break's favour, contracts"),
                   ("bdelta", "delta from break print to entry, in favour"), ("gap", "reopen gap vs 17:00 close, in favour, ticks")):
        x = np.array([r[f] for r in rows], float)
        qs = np.quantile(x, [1 / 3, 2 / 3]); b = np.digitize(x, qs)
        cells = []
        for j, tag in enumerate(("low", "mid", "high")):
            m = b == j; pp = p[m]
            if not m.any(): cells.append(f"{tag}: n=0"); continue
            gp, gl = pp[pp > 0].sum(), -pp[pp < 0].sum(); pf = gp / gl if gl else float("inf")
            cells.append(f"{tag}[{x[m].min():.0f}..{x[m].max():.0f}] n={m.sum():3d} win={np.mean(pp > 0):3.0%} avg=${pp.mean():5,.0f} PF={pf:4.2f}")
        print(f"  {lab:44} | " + " | ".join(cells))
    for f, lab, vals in (("side", "side", {1: "long", -1: "short"}), ("wday", "weekday", {0: "Mon", 1: "Tue", 2: "Wed", 3: "Thu", 4: "Fri", 6: "Sun"})):
        x = np.array([r[f] for r in rows]); cells = []
        for v, tag in vals.items():
            m = x == v; pp = p[m]
            if not m.any(): continue
            gp, gl = pp[pp > 0].sum(), -pp[pp < 0].sum(); pf = gp / gl if gl else float("inf")
            cells.append(f"{tag} n={m.sum():3d} win={np.mean(pp > 0):3.0%} avg=${pp.mean():5,.0f} PF={pf:4.2f}")
        print(f"  {lab:44} | " + " | ".join(cells))


def main():
    ap = argparse.ArgumentParser(); ap.add_argument("--stop", nargs="+", type=float, default=[1.5, 2.0])
    ap.add_argument("--no-features", action="store_true"); a = ap.parse_args()
    S = engine.LIBRARY["latigo_break"]
    for c, start in (("ALL", None), ("NQ 09-26", "2026-08-05")):
        t0 = time.time()
        ctx = engine.prepare(c, TF, start, None, False, "latigo_break")
        print(f"\n== {c}{' from ' + start if start else ''}: {ctx['days']} sessions {ctx['start']}..{ctx['end']}, "
              f"{len(ctx['bars']['t'])} 30s bars, holes={ctx['n_holes']} ({time.time() - t0:.0f}s)", flush=True)
        for sm in a.stop:
            out, (ei, dr, st, s) = run(ctx, S, sm)
            for k, tr in out.items():
                print(f"  stop {sm:.1f}xATR {k:8} {stats(tr)}", flush=True)
            if not a.no_features and len(out["signal"]) >= 30:
                breakdown(features(ctx, ei, dr, st, s), out["signal"], f"{c} stop {sm}xATR $500 target")
        del ctx


if __name__ == "__main__" and "--filters" not in sys.argv and "--dials" not in sys.argv:
    main()


# ---- candidate filters, thresholds FIXED from the ALL terciles above, judged on both tapes
FILTERS = {
    "r30>=97": lambda r: r["r30"] >= 97,
    "cvol>=346": lambda r: r["cvol"] >= 346,
    "delay<=230s": lambda r: r["delay_s"] <= 230,
    "r30>=97 & delay<=230": lambda r: r["r30"] >= 97 and r["delay_s"] <= 230,
    "cvol>=346 & delay<=230": lambda r: r["cvol"] >= 346 and r["delay_s"] <= 230,
    "shorts only": lambda r: r["side"] < 0,
}


def filters_main():
    S = engine.LIBRARY["latigo_break"]
    for c, start in (("ALL", None), ("NQ 09-26", "2026-08-05")):
        ctx = engine.prepare(c, TF, start, None, False, "latigo_break")
        print(f"\n== {c}{' from ' + start if start else ''}: {ctx['days']} sessions", flush=True)
        for sm in (1.5, 2.0):
            out, (ei, dr, st, s) = run(ctx, S, sm)
            rows = features(ctx, ei, dr, st, s)
            for tgt in ("signal", "atr_tgt"):
                tr = out[tgt]
                # trades map 1:1 to entries here (one window, one trade, no governor)
                assert len(tr) == len(rows), (len(tr), len(rows))
                for name, fn in FILTERS.items():
                    keep = [t for t, r in zip(tr, rows) if fn(r)]
                    print(f"  stop {sm:.1f} {'$500 ' if tgt == 'signal' else '2xATR'} {name:24} {stats(keep)}", flush=True)
        del ctx


if __name__ == "__main__" and "--filters" in sys.argv:
    filters_main()


# ---- the same idea as NT8 dials: MinR30Ticks and EntryWindowMinutes, no post-hoc filter
def dials_main():
    S = engine.LIBRARY["latigo_break"]
    for c, start in (("ALL", None), ("NQ 09-26", "2026-08-05")):
        ctx = engine.prepare(c, TF, start, None, False, "latigo_break")
        t = ctx["tape"]; px = t["px"]
        print(f"\n== {c}{' from ' + start if start else ''}: {ctx['days']} sessions", flush=True)
        for sm in (1.5, 2.0):
            for r30, win in ((97, 30), (97, 5), (4, 5), (80, 5), (120, 5)):
                s = S(); s.tick, s.point_value = engine.instrument(c)[::-1]
                p = base_params(S, sm); p.update(min_r30_ticks=r30, entry_window_minutes=win)
                ei, dr, st, tg_atr, _, _ = s.entries(ctx["bars"], t, p)
                e = px[ei].astype(float)
                for tgt, tgv in (("$500 ", e + dr * TARGET_USD / s.point_value), ("2xATR", tg_atr)):
                    tr = engine.resolve(t, ei, dr, st, tgv, COSTS, timeout_min=23 * 60)
                    print(f"  stop {sm:.1f} {tgt} MinR30={r30:3d} Window={win:2d}m  {stats(tr)}", flush=True)
        del ctx


if __name__ == "__main__" and "--dials" in sys.argv:
    dials_main()
