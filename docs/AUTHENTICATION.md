# Authentication

## Access tokens

`POST /api/v1/auth/login` verifies credentials through ASP.NET Core Identity and issues a signed bearer access token with a default 15-minute lifetime. The token contains Identity user/role claims and a security-stamp claim. The current account response contains safe profile and role information only; it never contains a password hash, security stamp, or reset token. User and role claims are checked against current database state for protected requests, so deactivation, role changes, facility lifecycle changes, staff lifecycle changes, mandatory password changes, and stamp rotation take effect without trusting stale client state.

There is deliberately no refresh endpoint or long-lived refresh credential. Clients must sign in again after expiry. `POST /api/v1/auth/logout` rotates the user security stamp, revoking all outstanding tokens for that account. `POST /api/v1/auth/change-password` uses Identity's password policy and also rotates the stamp. Password-reset delivery and reset endpoints are unavailable; no email provider is configured or required.

## Client handling

Use HTTPS. Keep access tokens in memory where practical, avoid local storage and URL/query parameters, and never log or analytics-capture authorization headers. A backend-for-frontend with secure HttpOnly cookies is preferable for a production browser deployment; it would require a separately designed CSRF and cookie contract. This API currently uses bearer headers and does not enable CORS credentials or cookie authentication.

## Failure behavior

Invalid, unknown, blocked, or ineligible credentials receive the same generic 401 response. Protected requests return 401 when no valid bearer session is supplied and 403 when an authenticated account fails an operational policy. Private/missing records may return 404 to avoid disclosure. Exception details, signing keys, passwords, and tokens are not returned in API errors or application logs.
