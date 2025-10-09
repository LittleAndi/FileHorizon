# Service Bus Notifications – Security & Secret Handling

## Overview

Processed file notifications may contain metadata (file path, size, timestamps). We must ensure credentials and sensitive data are protected while enabling reliable delivery.

## Authentication Modes

Supported via `ServiceBusNotificationOptions.AuthMode`:

1. ConnectionString – single secret reference containing full connection string.
2. AadManagedIdentity – uses Azure AD; requires `FullyQualifiedNamespace` and assigned role `Azure Service Bus Data Sender`.
3. SasKeyRef – separate secret references for SAS key name and SAS key value plus `FullyQualifiedNamespace`.

## Required Option Combinations

| AuthMode           | Required                                               | Must Be Null                                           |
| ------------------ | ------------------------------------------------------ | ------------------------------------------------------ |
| ConnectionString   | ConnectionSecretRef                                    | FullyQualifiedNamespace, SasKeyNameRef, SasKeyValueRef |
| AadManagedIdentity | FullyQualifiedNamespace                                | ConnectionSecretRef, SasKeyNameRef, SasKeyValueRef     |
| SasKeyRef          | FullyQualifiedNamespace, SasKeyNameRef, SasKeyValueRef | ConnectionSecretRef                                    |

Validator (`ServiceBusNotificationOptionsValidator`) enforces these rules.

## Secret Resolution

Secrets are retrieved via `ISecretResolver` when first needed and cached for the process lifetime (or until configuration reload). No secret values are logged or emitted in metrics.

## Logging Policy

Do Log:

- EntityName
- AuthMode
- Attempt counts & publish duration (ms)
- Sanitized idempotency key prefix (first 8 chars)

Do NOT Log:

- Connection strings
- SAS key values
- Full idempotency key
- Raw secret ref values (only names or truncated identifiers)

## Telemetry

Metrics:

- `notify.published`, `notify.failed`, `notify.suppressed`, `notify.publish.duration.ms`
  None include secret values or raw file paths when `LogFullPaths=false`.

## Idempotent Suppression

`notify:{idempotencyKey}:{status}` used as dedupe key in `IIdempotencyStore`. TTL configurable via `IdempotencyTtlMinutes`.

## Failure Classification (Future)

Transient vs terminal (auth/permission). On terminal auth failures, perform single re-resolution then apply backoff/circuit breaker to avoid flooding.

## Rotation Strategy

- ConnectionString: rotate secret by updating reference; add optional secondary secret ref future.
- SAS: update key value secret; previous key invalidated externally.
- AAD: relies on token lifetime; no rotation required.

## Threat Mitigations

| Threat                          | Mitigation                                 |
| ------------------------------- | ------------------------------------------ |
| Secret leakage in logs          | Strict logging policy & code review.       |
| Duplicate notification flooding | Idempotent suppression & consumer dedupe.  |
| Unauthorized publish            | Least-privilege role (Send only).          |
| DoS via failures                | Controlled retry + backoff (future).       |
| Path sensitivity                | Optional redaction (`LogFullPaths=false`). |

## Future Enhancements

- Add circuit breaker around publish failures.
- Add secondary secret ref for rolling deployments.
- Add redaction policy for file paths by glob or prefix.
- Integration with managed identity token acquisition instrumentation.

## Quick Checklist

- [ ] Options validated at startup
- [ ] Secret resolver wired
- [ ] No secret values in logs/metrics
- [ ] Dedupe enabled
- [ ] Retry policy documented

End of document.
