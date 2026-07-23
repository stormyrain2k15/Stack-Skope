# StackScope Python inference worker

Python 3.11+ worker that loads HuggingFace Transformers models and
streams inference events (token/layer/head/attention/activation/logit)
over gRPC to the StackScope coordinator.

## Install

```bash
python -m pip install -r requirements.txt
```

## Generate gRPC stubs

The worker imports `stackscope_worker._generated.{events_pb2,
worker_pb2, worker_pb2_grpc}`. Generate them from the `.proto`
contracts before first run:

```bash
../../scripts/gen_proto.sh
```

## Run

```bash
python -m stackscope_worker.worker --endpoint 127.0.0.1:50501
```

## Test

```bash
python -m pytest ../../tests/python_worker_tests -q
```

The hook capture test uses `sshleifer/tiny-gpt2` (~5 MB), which will be
downloaded on first run from HuggingFace Hub.
