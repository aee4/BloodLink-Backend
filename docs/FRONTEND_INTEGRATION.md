# Frontend Integration

The API base path is `/api/v1`. Development Swagger is served at `/swagger`; its OpenAPI document is `/swagger/v1/swagger.json`. The frontend must use an explicitly configured origin and send bearer access tokens in the `Authorization: Bearer` header. CORS allows only configured origins, the required `GET`, `POST`, `PUT`, and `OPTIONS` methods, and `Authorization`/`Content-Type` headers. Credentialed CORS is disabled.

Authenticate with `POST /auth/login`; retain the short-lived token in memory where possible and discard it on logout. There is no refresh endpoint. Logout invalidates all existing tokens for that account; password change does likewise. Do not store role or facility scope as authority in the client: use the user returned by `/auth/me` for display only, and treat API policy results as authoritative. Never include the token in URLs, telemetry, screenshots, or logs.

JSON enum values use the numeric values of the Domain enums. All timestamps ending in `Utc` must be UTC ISO-8601 values (for example, `2030-03-01T14:30:00Z`). `NeededByUtc` must include a UTC offset and be in the future. Clients should preserve and submit SQL row-version values as base64 where a mutation accepts one. A 409 indicates a concurrency or current-state conflict; reload before retrying. Private records may deliberately appear as 404.

Facility registration returns a facility DTO and issues no session; sign in separately. Development may auto-approve registrations; production always requires SystemAdmin review. Password reset is unavailable. See [API contracts](API_CONTRACTS.md) for endpoint policies, scoping, statuses, and effects.
