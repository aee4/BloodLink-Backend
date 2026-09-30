# Architecture

## Projects

`BloodLink.Domain` contains entities, enums, domain rules, and exceptions and has no project references. `BloodLink.Application` depends only on Domain and owns service interfaces, request contracts, DTOs, and application-level policies. `BloodLink.Infrastructure` depends on Application and Domain and implements EF Core persistence, SQL Server transactions, Identity, authorization handlers, initialization, and business services. `BloodLink.Api` is the independent HTTP host and references Application and Infrastructure. It contains versioned controllers, transport DTOs, bearer authentication, CORS, safe exception handling, headers, Swagger, and health endpoints.

Tests are divided by Domain, Application, Infrastructure, API, Acceptance, and SQL Server Relational boundaries. API/relational test suites use disposable LocalDB databases for relational behaviors; do not replace these tests with EF InMemory substitutes.

## Request flow and authority

Controllers validate transport shape and map API bodies to existing application requests. Services enforce workflow invariants, actor eligibility, ownership, facility isolation, atomic mutations, audit evidence, and notifications. The API current-user adapter resolves the authenticated Identity user and asks `AccountAccessService` for fresh authoritative roles, security stamp, facility lifecycle, and staff lifecycle. Client-provided facility IDs are inputs only where the workflow requires selecting a participant; services validate that relationship against current records. The API never trusts client role claims for business authorization.

Mutations preserve the established SQL Server transaction boundaries. Inventory, need, and request records use rowversion concurrency tokens; conflicts are returned as 409. Audit and lifecycle history are persisted with the associated workflow where the service provides an atomic operation. Operational evidence uses restrictive foreign keys rather than cascade deletion.

## Host lifecycle

The API host registers Infrastructure, Identity, authorization policies, application services, and SQL-backed health checks. Development initializes migrations when `BloodLink:DatabaseInitialization:Enabled` is true and ensures canonical roles. Startup never changes existing facility states. Production never runs EF migrations automatically; optional initialization can ensure roles/bootstrap state without changing facility state. Development Swagger is enabled; production OpenAPI requires explicit configuration. Production requires HTTPS, an explicit HTTPS CORS allowlist, a stable signing secret, and SQL Server configuration.

## Deferred boundaries

The repository contains no frontend, email provider, password-reset delivery, or refresh-token store/endpoint. Password reset remains deliberately unavailable. A future frontend should use bearer tokens as documented; a BFF/cookie design would need separate CSRF and origin review.
