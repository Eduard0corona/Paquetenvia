# MinIO local

El servicio `minio` proporciona almacenamiento S3-compatible exclusivamente
para desarrollo. `minio-init` crea de forma idempotente el bucket indicado por
`MINIO_BUCKET` y termina; no define cuarentena, retención, IAM ni taxonomía
productiva.

Los datos viven en el volumen nombrado `minio_data`. La guía operativa está en
`docs/development/local-environment.md`.
