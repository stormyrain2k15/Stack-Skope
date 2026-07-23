#!/usr/bin/env bash
# Regenerate Python gRPC stubs from proto/*.proto into
# workers/inference_worker_py/src/stackscope_worker/_generated/.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$ROOT/workers/inference_worker_py/src/stackscope_worker/_generated"
mkdir -p "$OUT"

python -m grpc_tools.protoc \
    -I "$ROOT/proto" \
    --python_out="$OUT" \
    --grpc_python_out="$OUT" \
    "$ROOT/proto/events.proto" \
    "$ROOT/proto/worker.proto" \
    "$ROOT/proto/coordinator.proto"

# grpc_tools generates imports as `import events_pb2` (flat). Fix them
# to be relative to the _generated package so they work under our layout.
for f in "$OUT"/*_pb2*.py; do
    python - <<PY "$f"
import re, sys, pathlib
p = pathlib.Path(sys.argv[1])
t = p.read_text()
t = re.sub(r"^import (\w+_pb2)", r"from . import \1", t, flags=re.M)
t = re.sub(r"^import (\w+_pb2_grpc)", r"from . import \1", t, flags=re.M)
p.write_text(t)
PY
done

# Ensure __init__.py exists.
[ -f "$OUT/__init__.py" ] || echo '"""Generated gRPC stubs."""' > "$OUT/__init__.py"

echo "Generated proto stubs into $OUT"
