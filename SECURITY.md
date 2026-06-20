# Security Policy

## Supported Versions

Security fixes apply to the current `master` branch and tagged releases that
remain in active use.

## Reporting a Vulnerability

Please do not report security vulnerabilities through public GitHub issues.

Send a private report to [security@c3.do](mailto:security@c3.do) with:

- A short description of the issue
- Steps to reproduce or validate it
- Affected files, versions, or runtime behavior
- Any known exploitability or impact

The maintainers will acknowledge the report, triage the impact, and coordinate a
fix before public disclosure when appropriate.

## Security Baseline

This repository uses:

- `gitleaks` secret scanning
- `gosec` static security analysis
- `govulncheck` dependency and call-path scanning
- GoReleaser changelog generation for tagged releases
