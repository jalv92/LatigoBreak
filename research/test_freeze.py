import json

import pytest

import freeze_labels


def test_x_formula():
    # np.percentile([10,20,30,40,88], 90) = 68.8 -> 5*round(13.76) = 70
    assert freeze_labels.x_from_rets([10, 20, 30, 40, 88]) == 70.0
    assert freeze_labels.x_from_rets([1, 2, 3]) == 30.0          # clamp low
    assert freeze_labels.x_from_rets([200.0] * 10) == 90.0       # clamp high
    assert freeze_labels.x_from_rets([]) == 60.0                 # fallback, flagged


def test_refuses_overwrite(tmp_path, monkeypatch):
    tgt = tmp_path / "preregistration.json"
    tgt.write_text("{}")
    monkeypatch.setattr(freeze_labels, "TARGET", tgt)
    with pytest.raises(SystemExit):
        freeze_labels.main()
