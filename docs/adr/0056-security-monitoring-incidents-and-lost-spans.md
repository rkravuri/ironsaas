# ADR-0056: Security monitoring over the audit trail, the operator's alerting, incidents with the 72-hour notice, and the lost spans

**Status:** Proposed
**Date:** 2026-10-29
**Decided by:** Product owner and architect, in the brief of S3 T10b; the details here are the task's, for the review

## Context

TF-079 asks that authentication failures, lockouts, permission denials, impersonations, break-glass, mass exports and configuration changes feed security alerts with thresholds, that a misconfigured rule be reported as failing rather than skipped, and for an incident runbook with severities, roles, communication, the post-incident review and the duty to tell a tenant about a personal data breach within 72 hours. TF-130 AC4 asks that requests complete while the trace exporter is unavailable, and that the spans it buffers and then drops be counted.

## Decision

### The lost spans (TF-130 AC4)

- **Spans leave through a processor of our own**, `CountedBatchExportProcessor`, in front of the OTLP trace exporter. Ending a span only puts it on a bounded queue (`Telemetry:Traces:QueueSize`, 2048) and never waits; a thread of the processor's own sends it in batches.
- **Every span lost is counted**: a batch whose export fails or times out, and a span that finds the queue full, add to `telemetry.spans_dropped` with the reason (`export_failed`, `queue_full`); `telemetry.spans_exported` counts the rest and `telemetry.spans_queued` shows the buffer. The counters are cumulative, so Prometheus shows the loss once the collector is back.
- **Why not the SDK's batch processor**: it behaves the same way but keeps its count of dropped spans internal. **Why each signal has its exporter**: `UseOtlpExporter` cannot be mixed with a processor of our own for traces, so metrics and logs take the SDK's exporters, read from the same variables.

## Options considered

- The SDK's batch processor, read by reflection: its count is internal and may change in any release. Not chosen.
- A fake OTLP receiver in the tests: simpler, but it would not prove what the exporter does against the collector the units use; the test starts the pinned collector, pauses it (a collector that hangs) and stops it.

## Consequences

- A down or hanging collector costs spans, never requests, and the loss is visible.
