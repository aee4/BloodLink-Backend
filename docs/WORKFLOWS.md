# Workflows

Business workflows are implemented in Application contracts and Infrastructure services; the API controllers are only transport adapters. See [API contracts](API_CONTRACTS.md) for endpoint behavior and [domain model](DOMAIN_MODEL.md) for persisted records.

## Facility onboarding

Anonymous facility registration creates the facility and its first FacilityAdmin account. Development may approve on registration when its explicit Development-only option is enabled; production remains Pending for SystemAdmin review. Approval initializes any missing blood-type inventory rows without replacing existing balances. The client must separately sign in after registration.

## Staff lifecycle

FacilityAdmin creates staff with an initial password; the password is passed to Identity and never returned or logged. Operational staff access requires an active account, an approved facility, no mandatory password-change restriction, and exactly one matching Active staff membership. Admins can activate/deactivate staff; these checks are re-evaluated against current server-side state.

## Inventory, needs, and requests

FacilityAdmins adjust their own inventory with optional rowversion concurrency. FacilityStaff create and view their own needs; FacilityAdmins review facility needs and can start search, reject, or fulfil internally. External requests are created by FacilityAdmins, checked against current need/source state and inventory, and do not reserve stock until accepted. Only the source facility's active FacilityAdmin can accept, reject, cancel, or fulfil a request. Acceptance reserves the accepted quantity; cancellation releases it; fulfilment transfers exact accepted units. Mutations preserve the existing transaction, audit, notification, history, and concurrency behavior in services.

## Notifications and dashboards

Notifications are recipient-scoped; related links are returned only for allowlisted record kinds after access is checked. Dashboard results are selected by current server-validated role and scoped to the platform, facility, or staff member as appropriate.

## Account behavior

Login, current-user, logout, and password-change routes use Identity-backed bearer authentication. Logout/password change rotate the security stamp and revoke prior tokens. No refresh token or password-reset delivery/endpoint exists. See [Authentication](AUTHENTICATION.md).
