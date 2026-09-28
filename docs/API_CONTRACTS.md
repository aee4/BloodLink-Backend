# API Contracts

All business endpoints use JSON under `/api/v1`. DTOs are defined in `BloodLink.Api.Contracts` and `BloodLink.Application.DTOs`; EF entities are never serialized. Authorization policies are evaluated against current Identity, facility, staff, and record state, and business services retain their own scope and transition checks. `page` is 1-based and `pageSize` is bounded by the shared paging contract. Enum values are numeric Domain enum values. Times ending in `Utc` must be UTC ISO-8601. Error bodies use safe Problem Details without stack traces or internal messages.

Common failures: 400 malformed/invalid input, 401 missing/invalid/expired bearer token, 403 authenticated policy denial, 404 missing or private record, 409 concurrency or invalid workflow state, and 500 generic server failure. Created resources return 201; action endpoints without a representation return 204. A concurrency client should reload state and rowversion before retrying.

| Method and path | Request / response | Policy and scope | Success; failure; database effect |
| --- | --- | --- | --- |
| `POST /auth/login` | `LoginRequest`; `AccessTokenResponse` with safe `ApiUserResponse` | Anonymous; Identity validates credentials and account eligibility | 200; 400/401. Updates last login and writes audit row. |
| `POST /auth/refresh` | `RefreshRequest`; rotated `AccessTokenResponse` | Anonymous; one-time refresh credential | 200; generic 401. Rotates the credential atomically; stores only its hash. |
| `GET /auth/me` | none; `ApiUserResponse` | `AccountSession`; own current account | 200; 401/403/404. Read only. |
| `POST /auth/logout` | none; empty | `AccountSession`; current user | 204; 401/403/503. Rotates security stamp, revoking all current tokens. |
| `POST /auth/change-password` | `ChangePasswordRequest`; empty | `AccountSession`; current user | 204; 400/401/403/503. Identity password and stamp update plus audit. |
| `POST /facilities/register` | `RegisterFacilityBody`; `FacilityDto` | Anonymous; facility/admin created as one workflow | 201; 400/409. Facility, admin identity/role, audit and applicable initial inventory. Development-only auto-approval; production Pending. |
| `GET /facilities/me` | none; `FacilityDto` | Approved facility user; own server-resolved facility | 200; 401/403/404. Read only. |
| `PUT /facilities/me` | `FacilityUpdateBody`; empty | FacilityAdmin; own facility | 204; 400/401/403/404. Facility contact data and audit. |
| `GET /system/facilities` | `status`, `page`, `pageSize`; paged `FacilityDto` | SystemAdmin | 200; 401/403. Read only. |
| `GET /system/facilities/{id}` | none; `FacilityDto` | SystemAdmin | 200; 401/403/404. Read only. |
| `POST /system/facilities/{id}/approve` | none; empty | SystemAdmin | 204; 401/403/404/409. Status, audit, and missing inventory initialization atomically. |
| `POST /system/facilities/{id}/reject` | `DecisionBody`; empty | SystemAdmin | 204; 400/401/403/404/409. Status and audit. |
| `POST /system/facilities/{id}/suspend` | `DecisionBody`; empty | SystemAdmin | 204; 400/401/403/404/409. Status/reason and audit. |
| `POST /system/facilities/{id}/restore` | none; empty | SystemAdmin | 204; 401/403/404/409. Status and audit. |
| `GET /staff` | `page`, `pageSize`; paged `StaffDto` | FacilityAdmin; own facility | 200; 401/403. Read only. |
| `POST /staff` | `CreateStaffBody`; `StaffDto` | FacilityAdmin; own facility | 201; 400/401/403/409. Identity user/role, staff membership, notification and audit. Password is never returned. |
| `POST /staff/{id}/activate` | none; empty | FacilityAdmin; own facility member | 204; 401/403/404/409. Membership state and audit. |
| `POST /staff/{id}/deactivate` | `StaffReasonBody`; empty | FacilityAdmin; own facility member | 204; 400/401/403/404/409. Membership/account state and audit. |
| `GET /inventory` | none; list of `InventoryItemDto` | Approved facility user; own facility | 200; 401/403. Read only. |
| `POST /inventory/adjustments` | `InventoryAdjustmentBody` including optional base64 `rowVersion`; empty | FacilityAdmin only; own facility | 204; 400/401/403/404/409. Inventory, transaction and audit atomically; stale rowversion conflicts. |
| `GET /inventory/history` | `page`, `pageSize`; paged `InventoryTransactionDto` | Approved facility user; own facility | 200; 401/403. Read only. |
| `GET /inventory/search` | `bloodType`, `minimumAvailableUnits`, paging; paged `AvailabilityResultDto` | Approved facility user | 200; 400/401/403. Read-only availability search; no reservation. |
| `GET /inventory/low-stock` | `daysLookAhead`; list `LowStockAlertDto` | Approved facility user; own facility | 200; 400/401/403. Read only. |
| `POST /needs` | `NeedCreateBody`; `BloodNeedDto` | FacilityStaff; creator's approved facility; `NeededByUtc` must be future UTC | 201; 400/401/403/409. Need, history, audit and notification per service transaction. |
| `GET /needs/mine` | paging/status; paged `BloodNeedDto` | FacilityStaff; own created needs | 200; 400/401/403. Read only. |
| `GET /needs` | paging/status; paged `BloodNeedDto` | FacilityAdmin; own facility | 200; 400/401/403. Read only. |
| `GET /needs/{id}` | none; `BloodNeedDetailDto` | Creator or same-facility FacilityAdmin | 200; 401/403/404. Read only; private records may be 404. |
| `GET /needs/{id}/timeline` | none; chronological `BloodNeedTimelineItemDto[]` | FacilityAdmin; same facility | 200; 401/403/404. Read only. |
| `POST /needs/{id}/start-search` | none; empty | FacilityAdmin; own facility | 204; 401/403/404/409. Validated status transition/history/audit/notification. |
| `POST /needs/{id}/fulfil-internally` | `NeedDecisionBody`; empty | FacilityAdmin; own facility | 204; 400/401/403/404/409. Need, history, inventory, transaction, audit and notification are atomic. |
| `POST /needs/{id}/reject` | `NeedDecisionBody`; empty | FacilityAdmin; own facility | 204; 400/401/403/404/409. Status transition/history/audit/notification. |
| `POST /needs/{id}/cancel` | `NeedDecisionBody`; empty | FacilityStaff; creator/service-eligible need | 204; 400/401/403/404/409. Status transition/history/audit/notification. |
| `POST /requests` | `CreateRequestBody`; `BloodRequestDto` | FacilityAdmin; service validates need and source facility | 201; 400/401/403/404/409. Request/history/audit/notification; no reservation at creation. |
| `GET /requests/sent` | paging/status; paged `BloodRequestDto` | FacilityAdmin; own requesting facility | 200; 400/401/403. Read only. |
| `GET /requests/received` | paging/status; paged `BloodRequestDto` | FacilityAdmin; own source facility | 200; 400/401/403. Read only. |
| `GET /requests/{id}` | none; `BloodRequestDto` | FacilityAdmin at either participant facility | 200; 401/403/404. Read only. |
| `GET /requests/{id}/timeline` | none; chronological `RequestTimelineItemDto[]` | FacilityAdmin at either participant facility | 200; 401/403/404. Read only. |
| `POST /requests/{id}/accept` | `RequestDecisionBody`; empty | FacilityAdmin at source facility only | 204; 400/401/403/404/409. Reserves accepted stock and records history/audit/notification atomically. |
| `POST /requests/{id}/reject` | `RequestDecisionBody`; empty | FacilityAdmin at source facility only | 204; 400/401/403/404/409. Status/history/audit/notification. |
| `POST /requests/{id}/cancel` | none; empty | FacilityAdmin at source facility only; requester cancellation is forbidden | 204; 401/403/404/409. Accepted reservation release and request history/audit/notification atomically. |
| `POST /requests/{id}/fulfil` | `FulfilRequestBody`; empty | FacilityAdmin at source facility only | 204; 400/401/403/404/409. Stock transfer, transaction, status/history/audit/notification atomically. |
| `GET /notifications` | `page`, `pageSize`; paged `NotificationDto` | Authenticated; recipient only | 200; 400/401. Read only. Related links are allowlisted and record-authorized. |
| `GET /notifications/unread-count` | none; `UnreadNotificationCountDto` | Authenticated; recipient only | 200; 401. Read only. |
| `POST /notifications/{id}/read` | none; empty | Authenticated; recipient only | 204; 401/404. Idempotent recipient-scoped update. |
| `POST /notifications/read-all` | none; empty | Authenticated; current recipient | 204; 401. Marks only current user's notifications read. |
| `GET /dashboard` | none; role-specific dashboard DTO | Approved operational user; SystemAdmin platform scope, facility roles own facility scope, staff own needs | 200; 401/403. Read only. |
| `GET /health` | none; health status | Anonymous | 200 when process is healthy. No business DB mutation. |
| `GET /health/ready` | none; readiness status | Anonymous | 200 when database check passes; unhealthy status otherwise. Read only. |

## Deliberately absent

There is no password-reset route because delivery is disabled. Access tokens are short-lived; refresh credentials rotate and are stored only as hashes. Logout/password change invalidates refresh sessions and rotates the Identity security stamp. No EF entity, password hash, security stamp, plaintext password, reset token, stack trace, or internal exception message is a response contract.
