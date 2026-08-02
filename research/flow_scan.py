"""v4 flow-gate offline scan: are big-print-supported breaks better breaks?

Approximates the NT8 v4 cluster detector (same-side aggressor prints,
<=150 ms gaps, <=1500 ms span, cluster fires at total volume >= threshold)
over the .ncd corpus, then scores every frozen-label break event as
supported vs unsupported and compares real-rates and Trigger-style
bracket economics per support threshold.

CAVEATS (report carries them): aggressor side here is the bid/ask
HEURISTIC (imbalance/return corr +0.52) — NT8's OnMarketData
classification is ground truth; and this runs on the 18:00 tramo already
burned by Phase 0/1, so results are calibration-only.
"""
import math
from datetime import datetime
from pathlib import Path

import numpy as np

import corpus
import events

REG = __import__("json").loads(
    (Path(__file__).parent / "preregistration.json").read_text())
DOCS = Path(__file__).parent.parent / "docs" / "research"

GAP_MS = 150
SPAN_MS = 1500
NET_MS = corpus.NET_S // 1000
THRESHOLDS = (30, 50, 55, 60, 65, 70, 80, 120)
SUPPORT_WINDOW_S = 120           # pre-break lookback, mirrors SupportWindowSec
TRIGGER_GRACE_S = 5              # cluster firing this soon after the break = Trigger entry


def clusters(a):
    """All finalized sweep clusters [(t_end, side, vol, max_print)], NT8 fold rules."""
    m = a["side"] != 0
    ts, side, vol = a["ts"][m], a["side"][m], a["vol"][m]
    out = []
    if not len(ts):
        return out
    c_start = c_last = int(ts[0])
    c_side = int(side[0])
    c_vol = c_mp = int(vol[0])
    for i in range(1, len(ts)):
        t, s, v = int(ts[i]), int(side[i]), int(vol[i])
        if (s == c_side and t - c_last <= GAP_MS * NET_MS
                and t - c_start <= SPAN_MS * NET_MS):
            c_vol += v
            c_mp = max(c_mp, v)
            c_last = t
            continue
        out.append((c_last, c_side, c_vol, c_mp))
        c_start = c_last = t
        c_side, c_vol, c_mp = s, v, v
    out.append((c_last, c_side, c_vol, c_mp))
    return out


def support(cls, ev, thr, mp_min=0):
    """(supported_pre, trigger) mirroring the NT8 verdict.

    supported_pre: same-side cluster volume >= thr AND >= opposite volume in
    the 120 s before the break. trigger: first same-side cluster >= thr that
    fires within TRIGGER_GRACE_S after the break (balance re-checked then),
    returned as its (t_end, same_vol, opp_vol) or None. mp_min mirrors
    SupportMinMaxPrint: clusters whose largest single print is smaller are
    invisible to the verdict.
    """
    lo = ev.t_break - SUPPORT_WINDOW_S * corpus.NET_S
    same = opp = 0
    for t_end, side, vol, mp in cls:
        if lo <= t_end <= ev.t_break and vol >= thr and mp >= mp_min:
            if side == ev.side:
                same += vol
            else:
                opp += vol
    pre = same >= thr and same >= opp
    trig = None
    for t_end, side, vol, mp in cls:
        if ev.t_break < t_end <= ev.t_break + TRIGGER_GRACE_S * corpus.NET_S \
                and vol >= thr and mp >= mp_min:
            if side == ev.side:
                s2 = same + vol
                if s2 >= thr and s2 >= opp:
                    trig = (t_end, s2, opp)
                    break
            else:
                opp += vol
    return pre, trig


def sim_bracket_at(a, i_entry, side, entry_px, h, l, rr, inst, t0):
    """phase0b.sim_bracket with an explicit entry point (Trigger-style)."""
    tick = corpus.TICK_SIZE
    entry = entry_px + side * 3 * tick               # 2 spread + 1 slip, frozen cost model
    stop = l if side > 0 else h
    r_pts = abs(entry - stop)
    if r_pts < tick:
        return None
    target = entry + side * rr * r_pts
    t_end = t0 + 1800 * corpus.NET_S
    exit_px = entry
    for j in range(i_entry + 1, len(a)):
        px = float(a["px"][j])
        if int(a["ts"][j]) > t_end:
            break
        exit_px = px
        if (side > 0 and px <= stop) or (side < 0 and px >= stop):
            exit_px = stop
            break
        if (side > 0 and px >= target) or (side < 0 and px <= target):
            exit_px = target
            break
    gross = (exit_px - entry) * side * corpus.POINT_VALUE[inst]
    net = gross - corpus.COMMISSION_RT[inst]
    return {"net": net, "r": net / (r_pts * corpus.POINT_VALUE[inst])}


def z_two_prop(k1, n1, k2, n2):
    if min(n1, n2) == 0:
        return float("nan")
    p1, p2 = k1 / n1, k2 / n2
    p = (k1 + k2) / (n1 + n2)
    se = math.sqrt(p * (1 - p) * (1 / n1 + 1 / n2)) + 1e-12
    return (p1 - p2) / se


def main():
    lines = ["# v4 flow-gate scan — big-print support vs break outcome", "",
             "Cluster rules: same-side aggressor prints, <=150 ms gap, <=1500 ms span; "
             f"support window {SUPPORT_WINDOW_S} s pre-break + trigger grace {TRIGGER_GRACE_S} s. "
             "Aggressor side = bid/ask HEURISTIC (corr +0.52) — NT8 tape is ground truth. "
             "18:00 tramo is burned (Phase 0/1) — **calibration-only numbers.**", ""]
    for inst in ("NQ", "MNQ"):
        combos = [(t, 0) for t in THRESHOLDS] + [(60, mp) for mp in (3, 5, 8, 10, 15, 20)]
        rows = {c: dict(sup=[], unsup=[], mfe_s=[], mfe_u=[], trig={1: [], 2: []})
                for c in combos}
        n_sess = 0
        for d in sorted(corpus.sessions(inst)):
            a = corpus.load_session(inst, d)
            if a is None or len(a) < REG["min_prints_watch"]:
                continue
            t0 = corpus.dt_to_net(datetime(d.year, d.month, d.day, 18, 0))
            evs, _ = events.detect_events(
                a, t0, k=REG["k"], x_s=REG["x_timeout_s"],
                e_floor=REG["e_floor_ticks"], r30_min=REG["r30_min_ticks"])
            if not evs:
                continue
            fc = events.first_candle(a, t0)
            h, l, _r30 = fc
            n_sess += 1
            cls = clusters(a)
            for combo in combos:
                thr, mp = combo
                trig_taken = False
                for ev in evs:
                    pre, trig = support(cls, ev, thr, mp)
                    is_sup = pre or trig is not None
                    real = ev.label == "real"
                    (rows[combo]["sup"] if is_sup else rows[combo]["unsup"]).append(real)
                    mfe = float(np.max(
                        (a["px"][(a["ts"] > ev.t_break)
                                 & (a["ts"] <= ev.t_break + 120 * corpus.NET_S)]
                         - ev.break_px) * ev.side / corpus.TICK_SIZE, initial=0.0))
                    (rows[combo]["mfe_s"] if is_sup else rows[combo]["mfe_u"]).append(mfe)
                    # first Trigger-style entry per session per combo
                    if trig is not None and not trig_taken:
                        trig_taken = True
                        i_entry = int(np.searchsorted(a["ts"], trig[0], side="left"))
                        i_entry = min(i_entry, len(a) - 1)
                        px_entry = float(a["px"][i_entry])
                        for rr in (1, 2):
                            t = sim_bracket_at(a, i_entry, ev.side, px_entry,
                                               h, l, rr, inst, t0)
                            if t:
                                rows[combo]["trig"][rr].append(t)
        lines += [f"## {inst} ({n_sess} sessions)", ""]
        for combo in combos:
            thr, mp = combo
            r = rows[combo]
            ns, nu = len(r["sup"]), len(r["unsup"])
            ks, ku = sum(r["sup"]), sum(r["unsup"])
            rs = 100 * ks / ns if ns else float("nan")
            ru = 100 * ku / nu if nu else float("nan")
            z = z_two_prop(ks, ns, ku, nu)
            label = f"threshold {thr}c" + (f" + max-print >= {mp}c" if mp else "")
            lines.append(
                f"### {label} — supported {ns}/{ns + nu} breaks "
                f"({100 * ns / max(1, ns + nu):.0f}%)")
            lines.append(
                f"- real-rate: supported **{rs:.1f}%** ({ks}/{ns}) vs "
                f"unsupported {ru:.1f}% ({ku}/{nu}) | z={z:+.2f}")
            med_s = np.median(r["mfe_s"]) if r["mfe_s"] else float("nan")
            med_u = np.median(r["mfe_u"]) if r["mfe_u"] else float("nan")
            lines.append(f"- MFE@120s median (ticks): supported {med_s:.0f} "
                         f"vs unsupported {med_u:.0f}")
            for rr in (1, 2):
                tr = r["trig"][rr]
                if tr:
                    net = [t["net"] for t in tr]
                    wins = sum(1 for x in net if x > 0)
                    tstat = np.mean(net) / (np.std(net) / math.sqrt(len(net)) + 1e-9)
                    lines.append(
                        f"- TRIGGER {rr}R (first supported break/session): "
                        f"n={len(net)} | win%={100 * wins / len(net):.0f} | "
                        f"E[$/trade]={np.mean(net):+.2f} (t={tstat:.2f}) | "
                        f"total=${np.sum(net):+.0f}")
                else:
                    lines.append(f"- TRIGGER {rr}R: n=0")
            lines.append("")
    lines += ["## Reference baselines (phase0b/phase1, same cost model)", "",
              "- Naive first-break 1R: -$110.34/trade (n=203). Oracle 1R: +$236.29 (n=19).",
              "- Best Phase-1 cell h30_x0.25: -0.0442 R/session on validation.", "",
              "## How to read this", "",
              "- The Filter arm needs supported real-rate >= ~38-47% to break even at the level.",
              "- Kill signals: no separation (z ~ 0), support fires on nearly every break, or "
              "supported events rarer than 1 per 15 sessions.",
              "- Playback `latigo_flow_log.jsonl` (real tape) is ground truth over this scan."]
    DOCS.mkdir(parents=True, exist_ok=True)
    (DOCS / "flow-scan-report.md").write_text("\n".join(lines))
    print("\n".join(lines))


if __name__ == "__main__":
    main()
