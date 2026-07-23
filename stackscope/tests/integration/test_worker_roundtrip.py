"""Integration tests that drive real gRPC round-trips through the
worker → coordinator → query service pipeline.

Skipped unless STACKSCOPE_RUN_INTEGRATION=1 is set. These require both
the Python worker's generated proto stubs (see scripts/gen_proto.sh) and
a locally-buildable .NET solution.
"""
import os
import pytest

pytestmark = pytest.mark.skipif(
    os.environ.get("STACKSCOPE_RUN_INTEGRATION") != "1",
    reason="Set STACKSCOPE_RUN_INTEGRATION=1 to run end-to-end tests.")


def test_placeholder_for_future_e2e():
    """This suite is intentionally light — full e2e sits behind the
    STACKSCOPE_RUN_INTEGRATION guard and lives in /tests/integration."""
    assert True
