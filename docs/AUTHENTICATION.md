# Authentication

## Access tokens

`POST /api/v1/auth/login` verifies credentials through ASP.NET Core Identity and issues a signed bearer access token with a default 15-minute lifetime. The token contains Identity user/role claims and a security-stamp claim. The current account response contains safe profile and role information only; it never contains a password hash, security stamp, or reset token. User and role claims are checked against current database state for protected requests, so deactivation, role changes, facility lifecycle changes, staff lifecycle changes, mandatory password changes, and stamp rotation take effect without trusting stale client state.

`POST /api/v1/auth/login` also issues a cryptographically random refresh credential with a 14-day sliding lifetime. `POST /api/v1/auth/refresh` accepts only that credential, rotates it on each successful use, and returns a new 15-minute access token. Reuse of a rotated credential revokes the active token family. Refresh records contain only SHA-256 hashes, family identifiers, creation/expiry/revocation timestamps, replacement hashes, and an Identity security-stamp snapshot. Invalid, expired, revoked, unknown, or ineligible credentials receive the same generic 401. Current roles and account/facility state are reloaded from the database; tokens are not accepted for inactive users or users whose facility is not active. New facility registrations activate immediately. Legacy Pending facilities retain restricted access and cannot refresh operational access; suspended facilities remain blocked.

`POST /api/v1/auth/logout` rotates the user security stamp and revokes all active refresh sessions. `POST /api/v1/auth/change-password` uses Identity's password policy and also rotates the stamp, invalidating prior refresh sessions. Password-reset delivery and reset endpoints remain unavailable; no email provider is configured or required.

## Client handling

Use HTTPS. Browser clients keep access tokens in memory and may keep only the rotating refresh credential in `sessionStorage`, never `localStorage`, URLs, markup, logs, or analytics. This is a school-project compromise for separate hosting domains: script running in the frontend origin can read session storage, so a production deployment should prefer a backend-for-frontend with secure HttpOnly cookies and a separately designed CSRF contract. The API uses bearer headers and does not enable CORS credentials or cookie authentication.

Refresh sessions are stored in `RefreshSessions`; raw credentials are never stored. Periodically remove old records after their replay-detection window, for example: `DELETE FROM dbo.RefreshSessions WHERE ExpiresAtUtc < DATEADD(day, -30, SYSUTCDATETIME());`. This intentionally retains revoked token hashes for 30 days beyond expiry.

## Failure behavior

Invalid, unknown, blocked, or ineligible credentials receive the same generic 401 response. Protected requests return 401 when no valid bearer session is supplied and 403 when an authenticated account fails an operational policy. Private/missing records may return 404 to avoid disclosure. Exception details, signing keys, passwords, and tokens are not returned in API errors or application logs.
