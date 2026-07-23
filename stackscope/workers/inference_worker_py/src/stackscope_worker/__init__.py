"""StackScope Python inference worker.

Provides a gRPC service (InferenceWorker) that loads HuggingFace Transformers
models, installs forward hooks on every ``nn.Module`` in the layer graph, and
streams captured events to the coordinator. NVTX/rocTX ranges are injected
around token/layer/head boundaries so the driver-level capture backends
(CUPTI, rocprofiler) can correlate their kernel launches back to semantic
regions.
"""

__version__ = "0.1.0"
