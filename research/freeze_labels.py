"""Mechanical pre-registration freeze. Runs ONCE; refuses to overwrite."""
import json
import sys
from datetime import datetime, timezone
from pathlib import Path

import numpy as np

TARGET = Path(__file__).parent / "preregistration.json"
STATS = Path(__file__).parent / "out" / "phase0a_stats.json"


def x_from_rets(rets):
    """clamp(5*round(P90/5), 30, 90); 60 s documented fallback if no data."""
    if not rets:
        return 60.0
    p90 = float(np.percentile(rets, 90))
    return float(min(90.0, max(30.0, 5 * round(p90 / 5))))


def main():
    if TARGET.exists():
        sys.exit("preregistration.json already exists — labels are FROZEN. "
                 "Amendments require a new dated file plus a spec note.")
    stats = json.loads(STATS.read_text())
    rets = [b["ret_s"] for r in stats["NQ"] for b in r["breaks"]
            if b["ret_s"] is not None
            and r["n_prints_watch"] >= 200 and r["r30_ticks"] >= 4]
    reg = {
        "frozen_at": datetime.now(timezone.utc).isoformat(timespec="seconds"),
        "break": "first print >=1 tick beyond H/L, inside->outside transition, "
                 "window 18:00:30-18:15:30 ET",
        "k": 1.0, "e_floor_ticks": 8, "r30_min_ticks": 4, "min_prints_watch": 200,
        "x_timeout_s": x_from_rets(rets),
        "x_formula": "clamp(5*round(P90(NQ pooled re-entry times <=120s)/5), 30, 90)",
        "x_n_reentries": len(rets),
        "entry": "break print px + 3 ticks adverse (2 spread + 1 slip)",
        "commission_rt": {"NQ": 4.5, "MNQ": 1.5},
        "stop": "opposite candle extreme", "brackets": [1, 2],
        "tie_rule": "stop wins", "time_stop": "18:30:00 ET",
        "horizons_min": [1, 2, 5, 15],
        "g0": "PASS iff oracle net expectancy/trade > 0 in >=1 bracket view on NQ",
        "phase1_grid": {"hold_s": [0, 2, 5, 10, 20, 30],
                        "ext_r30": [0, 0.25, 0.5, 1.0]},
        "phase1_split": "chronological 60/40",
        "phase1_metric": "expectancy per session in R, 1R bracket view",
    }
    TARGET.write_text(json.dumps(reg, indent=2))
    print(f"FROZEN: X={reg['x_timeout_s']}s from {len(rets)} re-entries")


if __name__ == "__main__":
    main()
