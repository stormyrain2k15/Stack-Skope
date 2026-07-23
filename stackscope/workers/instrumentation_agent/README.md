# workers/instrumentation_agent/

Reserved for a future in-process instrumentation agent that would sit
alongside the worker and stream OS-level counters (Windows ETW, Linux
perf) into the capture pipeline as a separate `ICaptureBackend`.

**Status this pass:** deferred honestly (per project rule §38). There
is no stub agent process here. The CPU counter path we do ship is
implemented in `adapters/Drivers/Cpu/CpuCaptureBackend.cs`, which
samples `Process.TotalProcessorTime` from the coordinator's own
process. It is not fake — it just reports what the coordinator can
observe without ETW privileges.
