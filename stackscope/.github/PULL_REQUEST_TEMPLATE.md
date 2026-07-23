<!--
  Thanks for the PR! Fill in the sections below. Delete any that don't apply.
  The GitHub build canaries (dotnet-build, python-worker, proto-lint,
  native-workers, msi-package, codeql) must all be green before merge.
-->

## Summary

<!-- One-paragraph description of the change and why it matters. -->

## Layers touched

- [ ] WPF UI (`app/desktop/**`)
- [ ] Core (`core/**`)
- [ ] Format adapters (`adapters/Formats/**`)
- [ ] Architecture adapters (`adapters/Architectures/**`)
- [ ] Driver capture (`adapters/Drivers/**`)
- [ ] Python worker (`workers/inference_worker_py/**`)
- [ ] llama.cpp worker (`workers/llamacpp_worker/**`)
- [ ] Coordinator / services (`services/**`)
- [ ] Proto contracts (`proto/**`)
- [ ] MSI / packaging (`packaging/**`)
- [ ] Docs / build scripts

## Testing

<!-- List new tests + how existing ones were verified. -->

- [ ] `dotnet test` (Core.Tests + Adapters.Tests) green
- [ ] `pytest tests/python_worker_tests` green
- [ ] Manual: <!-- describe -->

## Breaking changes

<!-- List any `.proto` renumbering, storage format changes, or
     command-line flag renames. -->

## Related issues

Closes #

## Checklist

- [ ] I followed the coding conventions in `.github/CONTRIBUTING.md`.
- [ ] I updated `docs/` where relevant.
- [ ] I added or updated tests for new behaviour.
- [ ] No stubs or mocks — every pipeline is real end-to-end.
