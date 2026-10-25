# Test concurrency and evidence scope

[中文](TESTING-NOTES.md) | English

## Configuration

xunit.runner.json limits maxParallelThreads to one and disables parallel collections and is copied by the test project. Integration cases use independent processes, ports, and temporary state. Limiting concurrency controls resources/timing windows, not correctness.

Earlier records include serial/parallel 196/196 passes. They establish only the asserted inputs/sequences at that revision, not that every test is correct or that product defects are absent. See [daemon limits](../../README.en.md) for uncovered notification, group-state, replay, and in-flight-target issues.

## Investigate instability

1. Preserve the first failure, configuration, port/process state.
2. Rerun the affected test and compare serial/parallel execution when useful.
3. A serial pass suggests scheduling/resource sensitivity; it does not rule out product races. Distinguish fixture/resource/product causes and repair the responsible layer.
4. Run affected checks after changes. Reuse valid evidence when no new change or unresolved concern requires repetition.

Historical cases involved idle timing margins, discovery order, WS closing, snapshot persistence. Assert observable behavior with appropriate waits; do not label failures harmless solely because they are intermittent.

## Acceptance boundary

Fake editors, direct SDK, shim, real Godot, real hosts, and GUI are distinct evidence layers. Explicit tools/list does not prove host refresh; handshake does not prove editor identity/gameplay; headless does not prove visuals.

Use the test project. For serial comparison pass `-- xUnit.MaxParallelThreads=1`. Five-host/version notification and mixed-version acceptance remain incomplete.
