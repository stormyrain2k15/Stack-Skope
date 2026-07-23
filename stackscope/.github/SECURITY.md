# Security Policy

## Supported versions

StackScope is pre-1.0. Only the current `main` branch and the most recent
tagged release receive security fixes.

| Version | Supported          |
| ------- | ------------------ |
| main    | :white_check_mark: |
| 0.1.x   | :white_check_mark: |
| < 0.1   | :x:                |

## Reporting a vulnerability

**Do not open a public issue for security problems.** Instead:

1. Use GitHub's private vulnerability reporting:
   `Security → Report a vulnerability` on the repository page.
2. Include:
   - A minimal reproducer (model, workload, config).
   - The affected component (WPF UI / core / adapter / driver capture /
     worker / MSI installer).
   - Impact assessment (RCE, information disclosure, denial of service,
     supply-chain, etc.).
3. You will receive an acknowledgement within **72 hours** and a triage
   status within **7 days**.

## Scope

In scope:
- Memory safety issues in C# / native interop.
- Path traversal, unsafe deserialization in safetensors/GGUF/TF parsers.
- gRPC transport misconfiguration (StackScope binds to loopback by default).
- MSI installer privilege escalation.

Out of scope (report upstream instead):
- Vulnerabilities in `llama.cpp`, PyTorch, Transformers, CUPTI, ROCm, or
  Vulkan SDK.
- Model weights supplied by the user.

## Disclosure

We follow **coordinated disclosure**. Public advisory + fix are published
after a patched release is available on `main` and any tagged installer.
