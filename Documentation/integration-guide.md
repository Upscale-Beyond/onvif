# ONVIF Connector - Integration Guide

## Overview

The ONVIF connector integrates ONVIF-compatible IP cameras into the Upscale Vision platform. It provides live streaming, PTZ control, presets, and snapshots. Cameras are discovered automatically via WS-Discovery on the local network.

## Requirements

### Upscale Vision

- Minimum hypervisor version: **1.1.0**

### Network

- Your ONVIF cameras must be reachable from the machine running the connector (same network or routed).
- The ONVIF service port (typically 80 or 8080) must be accessible from the connector host.
- The RTSP port (typically 554) must be accessible for stream relay.

### Cameras

- You need the ONVIF username and password for your cameras. Most cameras ship with a default credential that must be changed on first use.
- Live streaming requires **ONVIF Profile S**.
- PTZ features require **ONVIF Profile T** or a camera with a PTZ service.

## Supported Features

| Feature | Supported | Notes |
|---|---|---|
| Live streaming | Yes | RTSP relay via ONVIF `GetStreamUri` |
| PTZ continuous move | Yes | Pan, tilt, zoom |
| PTZ absolute move | Yes | Normalized coordinates |
| PTZ presets | Yes | Create, update, delete, go-to |
| Snapshots | Yes | Via ONVIF `GetSnapshotUri` |
| Playback | No | Not available in this version |
| Recording control | No | Recording is managed by an NVR, not the camera |
| Bookmarks | No | ONVIF does not have a bookmark concept |
| Video export | No | ONVIF basic profile does not support video export |
| Analytics | No | Detection only — analytics events are not streamed |

## Installation

1. Navigate to the **Connectors** app in the Upscale Vision web client.
2. Click the **Protocols** button.
3. Click **Add**, and select the package downloaded from our website.
4. A screen will appear with information about the package. Review it and confirm.
5. The protocol should now appear in the list of protocols.
6. To create a connector, go back to the Connectors app and click **Add**. Input a name, select **Onvif** for the protocol, and select where you want to run it.
7. Click **Add**.
8. The connector will appear in your connectors list. It is not running by default — configure its settings first, then click the **Start** button.

## Connector Settings

Before starting the connector, configure the following settings on the connector itself:

| Setting | Type | Description |
|---|---|---|
| `DefaultUsername` | string | ONVIF username applied to all cameras that don't have per-camera credentials |
| `DefaultPassword` | password | ONVIF password applied to all cameras that don't have per-camera credentials |
| `EnablePeriodicDiscovery` | boolean | If enabled, re-runs WS-Discovery every 60 seconds to detect new cameras |
| `RootName` | string | Name of the root variable that groups all discovered cameras (defaults to the connector name) |

## Device Discovery

On startup, the connector runs WS-Discovery on the local network and automatically creates a variable for every ONVIF camera it finds.

If the connector can authenticate with a camera, it creates one variable per media profile (typically one for the main stream and one for the sub-stream).

If authentication fails (wrong or missing credentials), the connector creates a **placeholder variable** marked as dirty, with the camera's address stored in its `Identifier` setting. Once you enter valid credentials on the placeholder, the connector will retry authentication and promote it to active stream variables automatically.

## Per-Camera Credentials

By default, all cameras use the `DefaultUsername` and `DefaultPassword` set on the connector. To override credentials for a specific camera, edit its variable settings:

| Setting | Description |
|---|---|
| `User` | ONVIF username for this camera |
| `Password` | ONVIF password for this camera |

Leave these empty to fall back to the connector-level defaults.

## Variable Structure

Each discovered camera stream is created as a variable under the root:

```
{RootName}.{DeviceName}.{ProfileName}
```

For example, a camera named `Hikvision DS-2CD2` with profiles `MainStream` and `SubStream` would appear as:

```
Onvif.Hikvision_DS_2CD2.MainStream
Onvif.Hikvision_DS_2CD2.SubStream
```

Each variable has:
- **DataType**: `Camera` (system type)
- **OpenWithApp**: `eye-viewer`

## Required Authorizations

The connector requires the following platform rights (assigned automatically from the package definition):

| Right | Purpose |
|---|---|
| `VARIABLESET` | Update variable states (camera statuses, PTZ, etc.) |
| `VARIABLEMANAGE` | Create camera variables during discovery |
| `CONNECTORAPP` | Run as a connector application |
| `EYESTREAMER` | Relay live video streams |
| `LOGMANAGE` | Write operational logs to the platform |

## Running the Connector

Once the connector is configured, click the **Start** button in the connectors list.

On startup, the connector:

1. Runs WS-Discovery to find cameras on the local network.
2. Authenticates with each discovered camera and creates variables for its media profiles.
3. Begins a 30-second status polling loop that checks whether each camera is reachable and reports its capabilities.
4. If `EnablePeriodicDiscovery` is on, re-scans for new cameras every 60 seconds.

## States (read)

The connector publishes the following states for each camera variable:

| State | Type | Description |
|---|---|---|
| `{camera}.PtzStatus.Pan` | float | Current pan position |
| `{camera}.PtzStatus.Tilt` | float | Current tilt position |
| `{camera}.PtzStatus.Zoom` | float | Current zoom level |
| `{camera}.Presets` | array | List of PTZ presets (`[{id, name}]`) |

### Capabilities

Reported once per camera on each status check:

| State | Type | Description |
|---|---|---|
| `{camera}.Capabilities.Ptz` | bool | Camera has PTZ service and a PTZ-enabled profile |
| `{camera}.Capabilities.Audio` | bool | Profile has an audio encoder |
| `{camera}.Capabilities.Analytics` | bool | Device exposes an analytics service |
| `{camera}.Capabilities.Recording` | bool | Always `false` — recording is NVR-side in ONVIF |
| `{camera}.Capabilities.Exports` | bool | Always `false` — video export is NVR-side in ONVIF |
| `{camera}.Capabilities.Snapshots` | bool | Always `true` — all ONVIF media profiles support snapshots |

## Commands (write)

Commands are sent by writing a value to the corresponding state key. The connector reacts to changes on these states.

### PtzCmd

Set an absolute PTZ position.

```json
{"pan": 0.5, "tilt": 0.3, "zoom": 0.1}
```

Values are in the normalized range supported by the camera (typically `-1.0` to `1.0`).

After the move, `PtzStatus.Pan`, `PtzStatus.Tilt`, and `PtzStatus.Zoom` are updated with the camera's reported position.

### PresetCmd

Manage PTZ presets. In ONVIF, creating or updating a preset saves the camera's **current position** at the time of the command.

```json
{"command": "goto", "id": "<token>"}
{"command": "create", "name": "My Preset"}
{"command": "update", "id": "<token>", "name": "New Name"}
{"command": "delete", "id": "<token>"}
```

After any preset command, the `{camera}.Presets` state is refreshed automatically.

### SnapshotCmd

Capture a snapshot from the camera and upload it. The value is used as the file name.

**Value**: `"my-snapshot.jpg"` (any string)
