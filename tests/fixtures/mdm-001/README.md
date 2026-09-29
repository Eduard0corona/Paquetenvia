# MDM-001 synthetic master data (SYNTHETIC — not real data)

Two **synthetic** examples of the reviewed file the MDM-001 operator loader reads
(`paquetenvia.master-data.v1`). Every city, area, zone and price in them is invented for tests:

- `synthetic-platform-cities.json` creates the city. Only a PLATFORM organization's load may carry
  cities; its `owner_org_id` (`22222222-…`) is a placeholder for your synthetic PLATFORM organization.
- `synthetic-master-data.json` carries one organization's areas, zones and tariffs, and references that
  city (its `cities` section is empty).
- Both load only into a database whose deployment marker is `SYNTHETIC`
  (`master-data-gate --deployment-class SYNTHETIC`); a `REAL` database refuses them.

- `classification` is `SYNTHETIC`, and every name starts with `Synthetic`.
- The polygons are arbitrary squares; they are not the pilot's service zones (GATE-010 is open).
- The amounts are placeholder cents; they are not the pilot's tariffs (GATE-010 is open).
- Every tariff rule is `VAT_INCLUDED`: the amount is the total with IVA included, and the loader creates no other
  tax mode (GATE-011-VAT-INCLUDED-2026-09-29).
- Every tariff rule carries `policy_version: synthetic-v1`, stored with the rule (PRC-POLICY-VERSION-PER-ORG).
- `driver_profiles` is empty: real driver profiles are personal data and wait on GATE-007. The contract
  tests add synthetic driver profiles in memory for users they create.
- In `synthetic-master-data.json`, `owner_org_id` is the DevSeed synthetic organization
  (`11111111-1111-1111-1111-111111111111`).

Do not copy this file into `database/seeds` and do not load it into the pilot database. The format and
the operator procedure are described in `docs/development/mdm-001-master-data-loader.md`.
