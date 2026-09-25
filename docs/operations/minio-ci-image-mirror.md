# MinIO CI image mirror

## Purpose

Reproducible GitHub-hosted CI. The upstream MinIO images on `quay.io/minio` stopped serving anonymous pulls (HTTP 401), so GitHub-hosted runners cannot pull them without credentials. The repository therefore pins controlled, public GHCR mirrors of the same upstream images. No registry login, token or `packages: read` permission is needed to pull them.

## Mirror properties

- Controlled mirrors, not forks. Each package contains the unmodified upstream image.
- No MinIO version upgrade was performed. The releases are the ones previously pinned from Quay.
- Platform: `linux/amd64` only. Each GHCR digest is the upstream `linux/amd64` child manifest of the Quay multi-arch index, pushed byte-for-byte, so the manifest digest is unchanged.
- No `latest` tag. Consumers pin tag and digest.
- Mirror created: 2026-09-24.

## Server

| Field | Value |
|---|---|
| Upstream project | MinIO server (<https://github.com/minio/minio>) |
| Upstream release | `RELEASE.2025-09-07T16-13-09Z` |
| Upstream commit (reported by `minio --version`) | `07c3a429` |
| Original reference | `quay.io/minio/minio:RELEASE.2025-09-07T16-13-09Z@sha256:14cea493d9a34af32f524e538b8346cf79f3321eff8e708c1e2960462bd8936e` (multi-arch index) |
| Mirror reference | `ghcr.io/eduard0corona/paquetenvia-minio:RELEASE.2025-09-07T16-13-09Z@sha256:a1a8bd4ac40ad7881a245bab97323e18f971e4d4cba2c2007ec1bedd21cbaba2` |
| License reported by the binary | GNU AGPLv3 |

## Client (mc)

| Field | Value |
|---|---|
| Upstream project | MinIO client `mc` (<https://github.com/minio/mc>) |
| Upstream release | `RELEASE.2025-08-13T08-35-41Z` |
| Upstream commit (reported by `mc --version`) | `7394ce0d` |
| Original reference | `quay.io/minio/mc:RELEASE.2025-08-13T08-35-41Z@sha256:a7fe349ef4bd8521fb8497f55c6042871b2ae640607cf99d9bede5e9bdf11727` (multi-arch index) |
| Mirror reference | `ghcr.io/eduard0corona/paquetenvia-minio-mc:RELEASE.2025-08-13T08-35-41Z@sha256:eb4ea9884b77704230e2423e9004d2fa738dc272876b9cc41a297d29443b8780` |
| License reported by the binary | GNU AGPLv3 |

## Consumers

- `deploy/docker-compose.yml`: `minio` (server) and `minio-init` (mc) services.
- `tests/Paqueteria.IntegrationTests/Custody/MinioProofStorageFixture.cs`: the Testcontainers server image for the SecureProofUpload tests.

## Changing the mirror

To move to another MinIO release, mirror the new upstream `linux/amd64` manifest to GHCR unmodified, record it here, and update every consumer's tag and digest together.
