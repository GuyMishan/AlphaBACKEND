# Simulated clearing-house vault

This directory is the persistent local development root for the TEST-only simulated clearing-house transport.

Runtime folders are created automatically and are ignored by Git:

- `outbox/{EmployerId}` — outbound Employer Interface DAT/TST payloads and attachment folders.
- `inbox/{EmployerId}` — simulated or real-format feedback waiting to be processed.
- `processing/{EmployerId}` — files currently claimed by the worker.
- `processed/{EmployerId}` — feedback successfully handled.
- `failed/{EmployerId}` — feedback that failed routing/validation/ingestion.
- `responded/{EmployerId}` — auto-responder markers.

The simulator is active only when `EmployerInterface006:EnvironmentCode=1` and `EmployerInterface006:SimulatedVault:Enabled=true`.
Do not commit runtime report or feedback files from this directory.
