# Security Baseline

This file describes the API checkpoint's implemented boundaries; it is not a penetration test or production certification.

## Identity and authority

ASP.NET Core Identity owns password hashing, verification, lockout, password changes, and security stamps. Access tokens are signed, short-lived bearer tokens. The signing key must be stable and secret-managed in Production, at least 32 bytes, and must not be logged. The API validates token issuer, audience, signature, and lifetime. Logout and password changes rotate the account security stamp; requests compare current account eligibility, role set, stamp, facility status, and staff membership with the authenticated principal.

Canonical roles are `SystemAdmin`, `FacilityAdmin`, and `FacilityStaff`. Operational policies require active eligible accounts; facility roles require an approved facility, and staff additionally require one matching Active staff membership. SystemAdmin scope is platform-wide and does not imply facility scope. Service-layer authorization remains authoritative for private records, lifecycle transitions, request participants, notification recipients, and facility isolation.

## Browser and origin boundary

The API uses bearer headers, not authentication cookies. CORS accepts only configured exact origins, a constrained method/header set, and no credentials. Production origins must be HTTPS. HTTPS redirection and HSTS are enabled outside Development. Configure TLS and trusted forwarded headers only at a controlled reverse proxy; arbitrary forwarded headers must not be trusted. Browser token storage should minimize persistence; see `AUTHENTICATION.md` and `FRONTEND_INTEGRATION.md`.

## Errors and secrets

Controllers return transport DTOs, not EF entities. Password hashes, security stamps, tokens, reset tokens, connection strings, stack traces, and internal exception messages are not response data. Login errors are generic. Exception logs record trace identifiers and exception types rather than request secrets or exception messages. Never enable request-body/header logging for authentication traffic.

## Recovery and limitations

Password-reset delivery is disabled. There is no operational reset endpoint and no SendGrid/SMTP provider or credential. Staff onboarding uses an administrator-chosen initial password; users can change it through the Identity-backed API. Do not claim self-service recovery until a separately reviewed delivery and token-lifecycle design exists.

This implementation still needs deployment-specific secret management, TLS/proxy configuration, monitoring, backup/restore validation, rate limiting at the edge, and independent security review before real operational use.
