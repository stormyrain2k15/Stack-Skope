import os
import sys
import pytest

# Make src/ importable in-tree.
ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
sys.path.insert(0, os.path.join(ROOT, "workers", "inference_worker_py", "src"))


@pytest.fixture(scope="session")
def torch_module():
    torch = pytest.importorskip("torch")
    return torch
