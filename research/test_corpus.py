"""Real-data smoke tests. Skip everything if the NT8 tick db is absent."""
from datetime import time

import numpy as np
import pytest

import corpus

pytestmark = pytest.mark.skipif(not corpus.DB.exists(),
                                reason="NT8 tick db not on this machine")


def test_sessions_nq_nonempty_and_sane():
    s = corpus.sessions("NQ")
    assert len(s) > 100                       # ~200-230 expected
    assert all(p.name.endswith("1900.Last.ncd") for p in s.values())
    assert all(p.exists() for p in s.values())
    years = {d.year for d in s}
    assert years <= {2025, 2026}


def test_load_session_covers_reopen_hour():
    s = corpus.sessions("NQ")
    d = sorted(s)[len(s) // 2]                # a mid-corpus date
    a = corpus.load_session("NQ", d)
    assert a is not None and len(a) > 100
    assert np.all(np.diff(a["ts"]) >= 0)      # sorted
    first, last = corpus.net_to_dt(int(a["ts"][0])), corpus.net_to_dt(int(a["ts"][-1]))
    assert time(18, 0) <= first.time() <= time(18, 5)
    assert last.time() <= time(19, 0)
    assert 10_000 < float(np.median(a["px"])) < 40_000
    assert set(np.unique(a["side"])) <= {-1, 0, 1}


def test_mnq_mirror_exists():
    assert len(corpus.sessions("MNQ")) > 50
