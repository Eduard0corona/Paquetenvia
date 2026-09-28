# MDM-001 synthetic master data (SYNTHETIC — not real data)

`synthetic-master-data.json` is a **synthetic** example of the reviewed file the MDM-001 operator loader
reads (`paquetenvia.master-data.v1`). Every city, area, zone and price in it is invented for tests:

- `classification` is `SYNTHETIC`, and every name starts with `Synthetic`.
- The polygons are arbitrary squares; they are not the pilot's service zones (GATE-010 is open).
- The amounts are placeholder cents; they are not the pilot's tariffs (GATE-010 and GATE-011 are open).
- Every tariff rule carries `policy_version: synthetic-v1` (validated, not yet persisted; see the dev doc).
- `driver_profiles` is empty: real driver profiles are personal data and wait on GATE-007. The contract
  tests add synthetic driver profiles in memory for users they create.
- `owner_org_id` is the DevSeed synthetic organization (`11111111-1111-1111-1111-111111111111`).

Do not copy this file into `database/seeds` and do not load it into the pilot database. The format and
the operator procedure are described in `docs/development/mdm-001-master-data-loader.md`.
