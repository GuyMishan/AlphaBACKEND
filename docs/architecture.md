# Backend architecture

## Context hierarchy

```text
Platform
  Organization
    Employer
      Employment
        Pension product
          Payroll contribution
            Report item
              Payment allocation
              Feedback item
```

Platform roles do not imply organization membership. Organization membership grants access to
either every employer in that organization or an explicit set of employers.

## Module roadmap

Implemented foundation:

- Identity and platform users
- Organizations and memberships
- Employers and employer-level access
- People and employments
- Append-only audit events

Next modules:

1. Pension providers, funds, products, and effective-dated contribution settings
2. Payroll periods and staged imports
3. Versioned validation rules and validation findings
4. Immutable report snapshots, batches, and submissions
5. Payment instructions, references, allocations, and reconciliation
6. Raw feedback files, parsed feedback items, and operational exceptions
7. Blob storage and queue-backed background jobs
8. Clearing-house gateway adapters

## Source-of-truth rule

Employer assertions, submitted snapshots, payment evidence, and institutional feedback must be
stored separately. One source must never overwrite another; reconciliation describes the gaps.

## Security invariants

- Scope every organization query server-side.
- Never trust an organization or employer identifier from the browser without access validation.
- Keep platform support access separate, time-bound, and audited.
- Store secrets outside configuration files in production.
- Do not log national IDs, bank data, report files, or tokens.
- Do not delete submitted financial records; reverse or supersede them.
