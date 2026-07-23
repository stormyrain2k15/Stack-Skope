"""Tests for the markers module (NVTX / rocTX bindings)."""


def test_now_ns_is_monotonic():
    from stackscope_worker.markers import now_ns
    a = now_ns()
    b = now_ns()
    assert b >= a
    assert isinstance(a, int)


def test_next_correlation_id_is_monotonic():
    from stackscope_worker.markers import next_correlation_id
    a = next_correlation_id()
    b = next_correlation_id()
    assert b > a


def test_range_marker_noop_if_no_profiler():
    """On a host without NVTX/rocTX libraries loaded, the range_marker
    context manager should still work and just yield a correlation id."""
    from stackscope_worker.markers import range_marker
    with range_marker("test") as corr:
        assert isinstance(corr, int)
        assert corr > 0
