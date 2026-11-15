# Desktop ↔ Broker IPC Contract

Status: proposed v1 contract. This is a design specification, not a claim that the named-pipe service has already been implemented.

## Transport

- Local named pipe: `\\.\pipe\Interstellar.Broker.v1`.
- Duplex, message-framed protocol with one request and one response per message.
- Explicit DACL: the current interactive user's logon identity plus the broker service identity; deny remote/network access. Never use a default pipe ACL without inspection.
- Authenticate the client process/session and authorize each operation. A pipe connection alone is not authorization.
- Maximum serialized message: 64 KiB. Maximum string length and collection counts are set per operation.
- Protocol version is required. Unknown version or operation returns a typed error; never fall back to executing arbitrary text.

## Envelope

```json
{
  "version": 1,
  "requestId": "guid",
  "operation": "GetBrokerStatus",
  "payload": {}
}
```

Response:

```json
{
  "version": 1,
  "requestId": "same-guid",
  "ok": true,
  "error": null,
  "payload": { "brokerVersion": "1.0.0", "deviceBridge": "not-installed" }
}
```

## Initial allowlist

The first release should expose only status and event subscription. Add operations only with a clear product requirement and a threat-model review.

| Operation | Access | Purpose |
|---|---|---|
| `GetBrokerStatus` | Read | Service version, uptime, health, feature availability |
| `GetDeviceBridgeStatus` | Read | Whether an explicitly supported device/driver is present and healthy |
| `SubscribeEvents` | Read | Receive allowlisted device/health state changes |

Any later hardware-specific operation must have its own typed request schema, input bounds, authorization rule, audit event, and cancellation behavior. Never add `RunCommand`, `ExecuteShell`, arbitrary PowerShell, arbitrary executable launch, arbitrary DLL load, arbitrary file read/write, raw IOCTL passthrough, or generic registry editing.

## Error model

- `UnsupportedVersion`
- `UnknownOperation`
- `UnauthorizedClient`
- `OperationNotAllowed`
- `InvalidPayload`
- `DeviceNotPresent`
- `DriverUnavailable`
- `Timeout`
- `InternalError`

Errors must not include secrets, raw kernel addresses, internal stack traces, or unrestricted filesystem paths.
