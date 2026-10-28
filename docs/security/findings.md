# Findings register

Findings from penetration tests and security reviews, each with its source, severity, the date it was opened, its
owner and its state (TF-077 AC3, ADR-0055). The scanners' findings are not kept here: the build reads them from each
run, and those that may wait are exceptions in `security/exceptions.yaml`.

The monthly security report reads this table (`tools/security_report.py`). A critical or high finding open for more
than 30 days is escalated as an issue in the repository, named by its id, until it is closed.

**The yearly independent penetration test** is the product owner's to commission. The first one is done, and its
findings are entered here, before the first customer goes live; then once a year, and after a change of the
platform's architecture.

How to keep a row: give it the next id (`F-001`, `F-002`, ...); severity is critical, high, medium or low as the
source rated it; state is open, fixed (with the commit or pull request that fixed it in its notes) or accepted (with the
exception that accepts it); dates are `YYYY-MM-DD`; a closed row keeps its place and gains its closing date.

| Id | Finding | Source | Severity | Opened | Owner | State | Closed | Notes |
|---|---|---|---|---|---|---|---|---|
