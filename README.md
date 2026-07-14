# OT Guard — Cybersecurity Gap Assessment

An operational technology security assessment workspace for importing asset inventory and switch configurations, applying an approved hardening baseline, and exporting a defensible gap report.

## Run the prototype

Open `index.html` in a modern browser. Use one of these demo accounts:

| Role | Credentials | Access |
| --- | --- | --- |
| Admin | `admin / admin` | Baselines, inventories, configuration upload, results, reports |
| Assessor | `assessor / assessor` | Inventories, configuration upload, assessments, results, reports |
| Viewer | `viewer / viewer` | Results and reports |

The prototype has seeded North Plant data to make the complete workflow immediately explorable. Upload controls are wired for the specified formats; report export produces an Excel-compatible CSV and print-ready results page.

## API starter

The `backend/OtGuard.Api` project is a minimal .NET API starter. It includes login, assessment lifecycle, inventory/baseline/configuration upload endpoints, Cisco-style switch configuration parsing, comparison logic, and a CSV report endpoint.

```powershell
cd backend/OtGuard.Api
dotnet run
```

For production, replace the in-memory `AssessmentStore` with Entity Framework Core + PostgreSQL, use ASP.NET Identity/OIDC with JWTs, store uploads in Azure Blob Storage, and add ClosedXML for robust `.xlsx` parsing and a PDF service for generated reports.

## Key API routes

- `POST /api/auth/login`
- `POST /api/assessments`
- `POST /api/assessments/{id}/inventory`
- `POST /api/baselines`
- `POST /api/assessments/{id}/configurations?assetName=...`
- `POST /api/assessments/{id}/run?baselineId=...`
- `GET /api/assessments/{id}/report.csv`
