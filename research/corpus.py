"""Session discovery + tick loading for the 18:00 ET Globex reopen study.

NT8 hourly .ncd files are named by END of hour: *1900.Last.ncd holds
18:00:00-18:59:59 ET (verified 2026-07-30; the near-empty *1800 bucket is
the 17:00-18:00 maintenance halt). Timestamps are naive ET wall clock.
"""
import json
import os
from datetime import date, datetime, timedelta
from pathlib import Path

import numpy as np

from ncd_parse import read_ticks

DB = Path(os.environ.get("NT8_TICK_DB",
                         "/mnt/c/Users/javlo/Documents/NinjaTrader 8/db/tick"))
OUT = Path(__file__).parent / "out"

NET_S = 10_000_000            # .NET ticks per second
TICK_SIZE = 0.25
POINT_VALUE = {"NQ": 20.0, "MNQ": 2.0}
COMMISSION_RT = {"NQ": 4.50, "MNQ": 1.50}
SPREAD_TICKS = 2
SLIP_TICKS = 1

DTYPE = np.dtype([("ts", "i8"), ("px", "f8"), ("side", "i1"), ("vol", "i4")])


def net_to_dt(tt):
    return datetime(1, 1, 1) + timedelta(microseconds=tt // 10)


def dt_to_net(dt):
    return int((dt - datetime(1, 1, 1)).total_seconds() * NET_S)


def _file_ticks(path):
    rows = [(tt, px, 1 if aoff == 0 else (-1 if boff == 0 else 0), vol)
            for tt, px, boff, aoff, vol in read_ticks(path)]
    return np.array(rows, dtype=DTYPE)


def sessions(instrument):
    """date -> front-month *1900.Last.ncd path (dominant volume per date)."""
    cache = OUT / f"sessions_{instrument}.json"
    if cache.exists():
        raw = json.loads(cache.read_text())
        return {date.fromisoformat(k): Path(v) for k, v in raw.items()}
    cand = {}
    for f in DB.glob(f"{instrument} */*1900.Last.ncd"):
        cand.setdefault(f.name[:8], []).append(f)
    out = {}
    for dstr, files in sorted(cand.items()):
        best = max(files, key=lambda f: int(_file_ticks(f)["vol"].sum()))
        d = date(int(dstr[:4]), int(dstr[4:6]), int(dstr[6:]))
        out[d] = best
    OUT.mkdir(exist_ok=True)
    cache.write_text(json.dumps({k.isoformat(): str(v) for k, v in out.items()},
                                indent=0))
    return out


def load_session(instrument, d):
    """DTYPE array for 18:00:00-18:59:59 ET of date d, ts-sorted. npz-cached."""
    cache = OUT / "cache" / f"{instrument}_{d.strftime('%Y%m%d')}.npz"
    if cache.exists():
        return np.load(cache)["a"]
    f = sessions(instrument).get(d)
    if f is None:
        return None
    a = _file_ticks(f)
    a = a[np.argsort(a["ts"], kind="stable")]
    cache.parent.mkdir(parents=True, exist_ok=True)
    np.savez_compressed(cache, a=a)
    return a


def prev_close(instrument, d):
    """Last print of the 16:00-17:00 file (pre-halt reference), or None."""
    f = sessions(instrument).get(d)
    if f is None:
        return None
    g = f.with_name(f.name.replace("1900.Last", "1700.Last"))
    if not g.exists():
        return None
    a = _file_ticks(g)
    return float(a["px"][np.argmax(a["ts"])]) if len(a) else None
