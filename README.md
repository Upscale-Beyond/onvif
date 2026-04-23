# Onvif Connector

ONVIF camera streaming connector for the Upscale Vision Hypervisor. Supports live and simultaneous playback streams, PTZ control, and automatic device discovery via WS-Discovery.

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Access to an Upscale Vision Hypervisor instance

## Installation

Upload the built package to your hypervisor through the protocol management interface. The protocol registers itself under the name defined in `package/definition.json` (`"name": "Onvif"`), which must match the assembly name set in the project (`<AssemblyName>Onvif</AssemblyName>` in `onvif.csproj`).

The connector requires the following hypervisor rights, declared in `package/definition.json` (which should be assigned automatically):

| Right | Purpose |
|---|---|
| `VARIABLESET` | Read and write camera variables |
| `VARIABLEMANAGE` | Create variables for auto-discovered cameras |
| `CONNECTORAPP` | Register and run as a connector |
| `EYESTREAMER` | Start and stop RTSP stream relays |
| `LOGMANAGE` | Write to the hypervisor log |

## Building

### Debug

```bash
dotnet build
```

Includes the `Quantum.DevKit` runtime so the connector can run standalone from `Program.cs`.

### Release (package)

```bash
dotnet build -c Release
```

Output is written to `package/binaries/`. The `Quantum.DevKit` runtime is excluded from the output (`ExcludeAssets>runtime`) because the hypervisor provides it at load time. Upload the entire `package/` directory as a zip file to the hypervisor.

## Versioning

The connector version is managed in [`package/definition.json`](package/definition.json):

```json
{
    "name": "Onvif",
    "version": "1.1.0.0",
    ...
}
```

Bump `version` before building a release. The `name` field must always match the `<AssemblyName>` in `onvif.csproj` — the hypervisor uses this to locate the connector entry point.

## Debugging

Edit [`Program.cs`](Program.cs) with your hypervisor address, tenant ID, and credentials before running:

```csharp
var client = new Client("https://<hypervisor-host>", "<tenantId>");
client.LoginDev("<username>", @"<password>", "<connector-name>", true).Wait();
```

| Parameter | Description |
|---|---|
| `https://<hypervisor-host>` | URL of your Upscale hypervisor |
| `<tenantId>` | Tenant identifier on the hypervisor |
| `"<username>"` | Username to authenticate with |
| `@"<password>"` | Password (verbatim string, safe for backslashes) |
| `"<connector-name>"` | Connector name as registered on the hypervisor |

Then run:

```bash
dotnet run
```

The connector will connect to the hypervisor, run WS-Discovery on the local network, and begin managing camera variables. Press `Ctrl+C` to shut down cleanly.

### Camera credentials

Per-camera credentials can be set on each camera variable via the `OverrideUsername` and `OverridePassword` settings. If left empty, the connector falls back to the `DefaultUsername` / `DefaultPassword` connector-level settings defined in `package/definition.json`.
