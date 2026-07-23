"""Tests for accelerator enumeration."""
from stackscope_worker import devices


def test_enumerate_always_returns_cpu_last():
    xs = devices.enumerate_devices()
    assert len(xs) >= 1
    assert xs[-1].kind == "cpu"
    assert xs[-1].id == "cpu"


def test_exactly_one_default():
    xs = devices.enumerate_devices()
    defaults = [d for d in xs if d.is_default]
    assert len(defaults) == 1


def test_cpu_device_reports_core_count_in_name():
    d = devices._cpu_device()
    assert d.kind == "cpu"
    assert "cores" in d.name


def test_format_display_cpu_has_no_gb_suffix():
    d = devices.DeviceInfo(id="cpu", kind="cpu", name="CPU (16 logical cores)")
    s = devices.format_display(d)
    assert s.startswith("cpu · ")
    assert "GB" not in s


def test_format_display_cuda_includes_name_and_memory():
    d = devices.DeviceInfo(
        id="cuda:0", kind="cuda", name="NVIDIA RTX 4090",
        total_memory_bytes=24 * 1024**3, compute_capability="8.9",
    )
    s = devices.format_display(d)
    assert "cuda:0" in s
    assert "NVIDIA RTX 4090" in s
    assert "24 GB" in s
    assert "8.9" in s


def test_dataclass_default_fields():
    d = devices.DeviceInfo(id="dml:0", kind="dml")
    assert d.total_memory_bytes == 0
    assert d.driver_version == ""
    assert d.is_default is False
