# ALPHA Backend agent guide

## Purpose
ALPHA backend is a .NET 10 modular monolith for multi-tenant pension operations. PostgreSQL/EF Core persistence supports the hierarchy Platform -> Organization -> Employer -> Employee/Employment and the reporting, billing and integration domains.

## Before changing code
1. Read `docs/CODEMAP.md`, `docs/architecture.md` and the affected endpoint/application/domain/persistence files.
2. Trace the full request path: endpoint -> application service -> domain/persistence -> external provider when applicable.
3. For Employer Interface 006 work, read the authoritative files under `docs/specifications/employer-interface/006` and `docs/specifications/mislaka`; do not infer schema rules from UI behavior.
4. Preserve tenant authorization and audit/security invariants.

## Architecture rules
- API transport belongs in `Alpha.Api`; business orchestration belongs in `Alpha.Application`; entities/invariants belong in `Alpha.Domain`; EF/database implementation belongs in `Alpha.Infrastructure`.
- Every organization-owned query must be scoped server-side.
- Browser-provided organization/employer IDs are never authorization by themselves.
- Submitted financial/reporting evidence should be immutable or superseded/reversed rather than destructively rewritten.
- Payment provider-specific code must stay behind `IPaymentProvider` / resolver boundaries.
- Avoid logging national IDs, bank data, report payloads, credentials or tokens.

## Employer Interface 006
The repository contains the official workbook, XSDs and clearing-house rules. Validate manual, Excel and XML/DAT/TST paths against the same canonical business/schema rules wherever possible. Keep export, validation, naming, reference/codebook and transmission responsibilities separated.

## Verification
Before considering a backend change complete run:
```bash
dotnet restore AlphaBackend.slnx
dotnet build AlphaBackend.slnx --no-restore --configuration Release
dotnet test --solution AlphaBackend.slnx --no-build --configuration Release
```
Do not claim verification passed unless it was run or CI confirms it.

## Documentation
Update `docs/CODEMAP.md` when major modules move or are added. Update `docs/architecture.md` when architectural boundaries/invariants change.
