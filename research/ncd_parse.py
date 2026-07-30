#!/usr/bin/env python3
"""NT8 .ncd tick-file parser -> per-minute order-flow features.

Vendored 2026-07-30 from projects/Trading/MFF-Sim/ncd_parse.py (validated:
price corr 0.9998 vs external ref; spread field NOT trustworthy - side only).

Format ported from bboyle1234/NTDFileReader (MIT-ish, credits JR Stokka):
header = <uint32> <double incrementSize> <double price0> <int64 .NET ticks>,
then 2-byte-header records with variable-length extensions:
  byte1 = ppsssttt (price flags, spread flags, time flags)
  byte2 = vvvppppp (volume flags, 5-bit price delta when pp==01)
Crucially the LAST tick stream carries bid/ask offsets per trade ->
true aggressor side (trade at ask = buy, at bid = sell).

Output per 1-min bar: OHLCV + buy_vol, sell_vol (aggressor-signed),
big_buy, big_sell (prints >= BIG lots), n_trades, avg_spread_ticks.

Usage: python3 ncd_parse.py <contract-dir> ...   (caches one pkl per dir)
"""

import struct
import sys
from pathlib import Path

import numpy as np
import pandas as pd

BIG = 10          # lots per print to count as "large" (NQ outright)
NET_EPOCH_S = 62135596800  # seconds between 0001-01-01 and 1970-01-01


def read_ticks(path):
    data = Path(path).read_bytes()
    if len(data) < 28:
        return
    inc, price, tt = struct.unpack_from("<ddq", data, 4)
    pos = 28
    n = len(data)
    rnd = round
    while pos + 2 <= n:
        b1 = data[pos]
        b2 = data[pos + 1]
        pos += 2
        tf = b1 & 7
        if tf == 0:
            pass
        elif tf == 1:
            tt += data[pos]; pos += 1
        elif tf == 2:
            tt += int.from_bytes(data[pos:pos + 2], "big"); pos += 2
        elif tf == 3:
            tt += int.from_bytes(data[pos:pos + 4], "big"); pos += 4
        elif tf == 4:
            tt += int.from_bytes(data[pos:pos + 8], "big"); pos += 8
        elif tf == 5:
            tt += data[pos] * 10_000_000; pos += 1
        else:
            raise ValueError(f"time flag {tf} at {pos}")
        pf = b1 >> 6
        if pf == 0:
            dp = 0
        elif pf == 1:
            dp = (b2 & 31) - 16
        elif pf == 2:
            dp = data[pos] - 128; pos += 1
        else:
            dp = int.from_bytes(data[pos:pos + 4], "big") - (1 << 31); pos += 4
        if dp:
            price = inc * (rnd(price / inc) + dp)
        sf = (b1 >> 3) & 7
        if sf == 6:
            x = data[pos]; pos += 1
            boff, aoff = x >> 4, x & 15
        elif sf == 7:
            boff, aoff = data[pos], data[pos + 1]; pos += 2
        else:
            boff = sf & 1
            aoff = 1 - boff
            if sf & 2:
                boff *= 2; aoff *= 2
            elif sf & 4:
                boff *= 3; aoff *= 3
        vf = b2 >> 5
        if vf == 1:
            vol = data[pos]; pos += 1
        elif vf == 2:
            vol = 100 * data[pos]; pos += 1
        elif vf == 3:
            vol = 500 * data[pos]; pos += 1
        elif vf == 4:
            vol = 1000 * data[pos]; pos += 1
        elif vf == 5:
            vol = int.from_bytes(data[pos:pos + 2], "big"); pos += 2
        elif vf == 6:
            vol = int.from_bytes(data[pos:pos + 4], "big"); pos += 4
        elif vf == 7:
            vol = int.from_bytes(data[pos:pos + 8], "big"); pos += 8
        else:
            raise ValueError(f"volume flag {vf} at {pos}")
        yield tt, price, boff, aoff, vol


def minute_features(contract_dir):
    """Aggregate all hourly .Last.ncd files of a contract into 1-min rows."""
    rows = {}
    files = sorted(Path(contract_dir).glob("*.Last.ncd"))
    for f in files:
        try:
            for tt, price, boff, aoff, vol in read_ticks(f):
                minute = (tt // 600_000_000)  # .NET ticks -> minutes
                r = rows.get(minute)
                if r is None:
                    r = rows[minute] = [price, price, price, price, 0, 0, 0,
                                        0, 0, 0, 0.0]
                r[3] = price
                if price > r[1]:
                    r[1] = price
                if price < r[2]:
                    r[2] = price
                r[4] += vol
                # aggressor: at/above ask = buy; at/below bid = sell
                if aoff == 0:
                    side = 1
                elif boff == 0:
                    side = -1
                else:
                    side = 0
                if side > 0:
                    r[5] += vol
                    if vol >= BIG:
                        r[7] += vol
                elif side < 0:
                    r[6] += vol
                    if vol >= BIG:
                        r[8] += vol
                r[9] += 1
                r[10] += (boff + aoff)
        except Exception as e:  # ponytail: skip corrupt hour, log it
            print(f"  parse error {f.name}: {e}")
    if not rows:
        return None
    idx = sorted(rows)
    arr = np.array([rows[i] for i in idx])
    ts = pd.to_datetime((np.array(idx, np.int64) * 60 - NET_EPOCH_S) * 10**9)
    df = pd.DataFrame(arr, index=ts,
                      columns=["open", "high", "low", "close", "volume",
                               "buy", "sell", "big_buy", "big_sell",
                               "n_trades", "spread_sum"])
    df["avg_spread"] = df["spread_sum"] / df["n_trades"].clip(lower=1)
    return df


def main():
    for d in sys.argv[1:]:
        d = Path(d)
        out = Path(__file__).parent / "data" / f"of_{d.name.replace(' ', '_')}.pkl"
        print(f"parsing {d.name} ({len(list(d.glob('*.Last.ncd')))} files)...")
        df = minute_features(d)
        if df is None:
            print("  no data")
            continue
        df.to_pickle(out)
        print(f"  {len(df)} minutes {df.index[0]} -> {df.index[-1]} saved {out.name}")


if __name__ == "__main__":
    main()
