# Threat model: files

- **Modules:** files
- **Endpoints:** none
- **Reviewed:** 2026-10-28 (S3 T10a)

## What it protects

The files a tenant stores, in its own blob container, and the names people see for them.

## Who can reach it

Signed-in people with the files' permissions, through the API; the unit's storage account key, which only the unit's
secret holds.

## Threats

| Category | Threat | Controls | Requirements |
|---|---|---|---|
| Spoofing | Reaching another tenant's files. | Each tenant has its own container and its own storage credentials in its unit's secret. | TF-005, TF-160 |
| Tampering | A file name that walks out of its folder or hides its type. | The bytes live under a random key; a name is display text only, stripped of paths, control and direction characters, device names and leading dots. | TF-071 |
| Tampering | Bytes changed in storage. | The upload's SHA-256 is kept with the file and each download is checked against it. | TF-160 |
| Repudiation | Denying an upload or a removal. | Each is a state change with its audit event. | TF-060 |
| Information disclosure | Reading a file without the right. | Files are served through the API, which checks the permission on each read. | TF-031 |
| Denial of service | Uploads that fill the storage. | An upload is at most 50 MB (`Storage:MaxUploadMegabytes`), counted while it streams; per-tenant size rules come with S4. | TF-071, TF-161 |
| Elevation of privilege | An uploaded file that runs. | Files are never served from the web root and never executed. | TF-078 |

## What remains

- Malware scanning, type checks by content and signed, expiring download links (TF-078) come with S4's uploads.
