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

## Internal referents

Referents are explicitly designated users (identity.users.IsReferent) assigned by platform admins to complete organizations and/or individually selected employers across organizations. Two identity-owned assignment tables preserve the distinction; employer assignments reference the employer ID directly and survive ownership transfers. Referents are not platform admins, organization members, employer customer seats or billing customers. All effective access requires the active referent marker and a live assignment. Organization assignment provides operational access to its employers plus editing the organization's general profile; employer assignment provides operational access only to the named employer. Neither grants billing configuration, user/permission administration, or platform administration. Administrative changes are audited; deactivating a referent removes assignments transactionally.

## Organization size classification

SmallOrganization (type 7) is restricted to a single employer across all states; RegularOrganization (type 8) supports multiple employers subject to the subscription plan. Historic organization types 1–6 retain their existing semantics. Employer creation and transfer check the limit under the existing organization entitlement advisory locks; changing a multi-employer organization to SmallOrganization is rejected until extra employers are transferred. Self-service onboarding preserves its existing type 6 semantics.

## Full employer ownership transfer

Each employer has precisely one organization. Platform administrators can atomically transfer an employer to a different organization. The transfer acquires source and target quota advisory locks, refuses duplicate employer identities and duplicate employee identifiers (Israeli IDs or passports), enforces target plan limits and rejects pending transmissions. Employee identities shared with other source employers are cloned so those employers retain their own records. All employer-specific references (employment, report, transmission, feedback and employer payment accounts) are re-scoped; historical encrypted evidence stays bound to its existing record identity and is not decrypted or rewritten. Direct employer-only grants follow the employer, former organization membership-based grants are revoked, and pending employer invitations from the former organization are cancelled. Historic billing ledger and append-only audit records remain untouched. Billing for periods spanning an employer move is deferred and must not be automatically retroactively invoiced without a separate effective-date attribution feature.

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
