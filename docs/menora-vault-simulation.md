# Multi-vault TEST simulation: clearinghouse and Menora

This setup is **TEST only** and does not connect to a real Menora or clearinghouse service. No real bank transfers or pension transactions are performed. The source of truth for 006 XML/XSD validation remains `docs/specifications/employer-interface/006` and `docs/specifications/mislaka`.

## Test environment configuration

In a **nonproduction** backend environment, configure:

```text
EmployerInterface006:EnvironmentCode=1
EmployerInterface006:SimulatedVault:Enabled=true
EmployerInterface006:SimulatedVault:AutoRespond=true
EmployerInterface006:SimulatedVault:DefaultScenario=success
EmployerInterface006:SimulatedVault:Manufacturers:Menora:Enabled=true
EmployerInterface006:SimulatedVault:Manufacturers:Menora:DefaultScenario=success
Reporting:ManufacturerRouting:EnableHybridDispatch=true
Reporting:ManufacturerRouting:Funds:<ACTUAL_MENORA_FUND_CODE>=SimulatedVault-Menora
```

Replace `<ACTUAL_MENORA_FUND_CODE>` with the **actual value in `ManualReportProduct.FundCode`** from the demo employer's chosen Menora product. Do not guess fund codes from the manufacturer's brand name. Products with no explicit routing override remain on `SimulatedVault`. Both provider names must match the backend registry exactly. In environment-variable notation, use double underscores between configuration segments (e.g. `EmployerInterface006__SimulatedVault__Manufacturers__Menora__Enabled=true`). Do not enable hybrid mode on the production tenant.

The simulation creates two segregated folders:

```text
simulated-clearinghouse-vault/
  outbox/<employer-id>/                       # clearinghouse
  inbox/<employer-id>/
  processed/<employer-id>/
  manufacturers/
    menora/
      outbox/<employer-id>/                    # Menora only
      inbox/<employer-id>/
      processed/<employer-id>/
      failed/<employer-id>/
      responded/<employer-id>/
```

Both channels share the official Employer Interface 006 outgoing file naming and schema validation, but **not** their directories. Menora messages are accepted by the Menora inbox worker only when their `TransmissionId` belongs to `SimulatedVault-Menora`. The simulated contribution feedback is limited to the `RoutedProductIdsJson` products of that transmission; it may not acknowledge unrelated clearinghouse contributions.

Each manufacturer can override `DefaultScenario`, `AutoRespond`, `PollIntervalSeconds` and `ResponseDelaySeconds` under its own `SimulatedVault:Manufacturers:<Key>` section, while inheriting defaults when absent. For example, set Menora's `DefaultScenario=error` while the clearinghouse remains `success`. No shared TEST inbox or sent-file directory is used.

## Acceptance scenario for the later E2E session

1. Create a TEST employer with at least two employees/products: one Menora fund with the overridden `FundCode` and another fund without an override. Supply plausible but entirely fake payroll data.
2. Finish the canonical report validation. GET `/api/organizations/{orgId}/employers/{employerId}/manual-reports/{reportId}/transmission-routing`. Confirm exactly two providers: `SimulatedVault` and `SimulatedVault-Menora`.
3. GET `/{reportId}/transmission-routing/validate`. Both XSD-validated scoped packages must pass before transmission.
4. POST `/{reportId}/transmissions` once. Inspect the two immutable transmission rows and confirm the files appear in **different outboxes** with no cross-manufacturer employee/product data. Distinct file sequences and payload hashes are expected.
5. Wait for separate auto-generated feedback in both inboxes. Verify both are correlated to their correct transmission and only their products are marked successful. The parent report may be shown as sent at transport level, but institutional feedback status remains independent.
6. Re-run with `DefaultScenario=error` on a TEST vault, or write a per-payload `.scenario` sidecar before auto-response. Assert an error from Menora does not overwrite clearinghouse feedback or close the original correction. Repair the Menora product using the correction workspace, materialize negative/current 006 technical documents and verify they retain the historical Menora routing.
7. Simulate a provider rejection to test `POST /{reportId}/transmissions/resume-hybrid`. Already accepted destinations must not be sent again. Pending/definitively rejected routes may be resumed, but uncertain outcomes must require reconciliation.
8. After a second successful feedback, confirm only the actual corrected products become resolved. Repeat after process restart and an attempted duplicate resume.

## Limitations / go-live gates

- This is a filesystem simulation, **not** an authenticated manufacturer-specific vault connection.
- An actual Menora connection requires their official protocol, sender/recipient identifiers, vault credentials, accepted file layout and official feedback formats. No such values are invented in code.
- The hybrid feature is **off by default**. Never enable it until the backend solution test suite and the full TEST-PostgreSQL scenario pass.
- Render's successful Docker `dotnet publish` is only a compilation/deployment signal; it does not prove the scenario above ran.
