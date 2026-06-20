# Security Policy

## Supported Versions

This repository is a project template. Security fixes apply to the current
`master` branch and to generated projects after they adopt the fix.

## Reporting a Vulnerability

Please do not report security vulnerabilities through public GitHub issues.

Send a private report to [security@c3.do](mailto:security@c3.do) with:

- A short description of the issue
- Steps to reproduce or validate it
- Affected files, versions, or generated-project behavior
- Any known exploitability or impact

The maintainers will acknowledge the report, triage the impact, and coordinate a
fix before public disclosure when appropriate.

## Template Security Baseline

Generated projects start with:

- `gitleaks` secret scanning
- `gosec` static security analysis
- `govulncheck` dependency and call-path scanning
- GoReleaser SBOM generation via Syft
- Distroless Docker runtime images
