#!/usr/bin/env python3
"""Pre-registered LatigoBreak settings against prop-firm rules, 1 NQ contract.

Settings: 18:00 only, stop 2.0xATR, target 2.0xATR, EntryWindowMinutes 5,
MinR30Ticks 80, governor off (the defaults shipped 2026-09-12).
Two answers per firm: `replay` walks the ACTUAL trade sequence through the
rules once; `sim_eval` is the Monte Carlo on the trades' win rate / R / risk.
"""
import sys
import numpy as np
sys.path.insert(0, "/home/javlo/Code Projects/main-project/projects/Trading/PropSim")
import engine, sim, prop_rules  # noqa: E402
from tape_run import base_params, COSTS, TF, stats  # noqa: E402

FIRMS = (("lucid_trading", "luciddaily_dllon_eod", 50_000), ("lucid_trading", "luciddaily_dlloff_eod", 50_000),
         ("apex_trader_funding", "eod_drawdown", 50_000), ("topstep", None, 50_000),
         ("my_funded_futures", "rapid", 50_000), ("take_profit_trader", None, 50_000))


def trades_for(c, start):
    ctx = engine.prepare(c, TF, start, None, False, "latigo_break")
    S = engine.LIBRARY["latigo_break"]; s = S(); s.tick, s.point_value = engine.instrument(c)[::-1]
    p = base_params(S, 2.0); p.update(min_r30_ticks=80, entry_window_minutes=5)
    ei, dr, st, tg, _, _ = s.entries(ctx["bars"], ctx["tape"], p)
    return engine.resolve(ctx["tape"], ei, dr, st, tg, COSTS, timeout_min=23 * 60), ctx["days"]


def profile(tr, days):
    p = np.array([t.pnl for t in tr]); w, l = p[p > 0], p[p <= 0]
    risk = -l.mean()
    # tpd is an integer slot count in sim_eval; the setup fires ~1 in 4 sessions, so
    # MC 'days' are trades and get scaled to sessions below.
    return dict(p=len(w) / len(p), rr=w.mean() / risk, risk=risk, tpd=1, friction=0.0, per_session=len(p) / days)


def main():
    rng = np.random.default_rng(7)
    for c, start in (("ALL", None), ("NQ 09-26", "2026-08-05")):
        tr, days = trades_for(c, start)
        prof = profile(tr, days)
        print(f"\n== {c}{' from ' + start if start else ''}: {days} sessions  {stats(tr)}")
        print(f"   profile: win {prof['p']:.0%}, R {prof['rr']:.2f}, avg loss ${prof['risk']:.0f}, {prof['per_session']:.2f} trades/session")
        for firm, variant, size in FIRMS:
            try:
                if variant is None:
                    variant = next(v for v, ph, sz in prop_rules.variants(firm) if ph == "evaluation" and sz == size)
                rules = prop_rules.select(firm, variant, "evaluation", size)
            except Exception as ex:  # noqa: BLE001
                print(f"   {firm:22} {variant or '?':26} n/a ({ex})"); continue
            r = sim.replay(tr, rules)
            ev = sim.sim_eval(prof, sim.policy_fixed(1), 10_000, rng, rules)
            print(f"   {firm:22} {variant:26} target ${rules.profit_target:,.0f} dd ${rules.max_dd:,.0f} | "
                  f"replay: {r['outcome']} {r.get('why') or ''} {r.get('when') or ''} | "
                  f"MC: P(pass) {ev['p_pass']:.0%} P(bust) {ev['p_bust']:.0%} ~{ev['days'] / prof['per_session']:.0f} sessions")


if __name__ == "__main__":
    main()
