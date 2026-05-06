using Onvif.Core.Client;
using Onvif.Core.Client.Common;
using Onvif.Core.Client.Ptz;
using Quantum.DevKit.Connection;
using Quantum.DevKit.Models.Data;
using Quantum.DevKit.Models.Streaming;
using Upscale.Connectors.Onvif.Models;
using Upscale.Connectors.Onvif.Onvif;

namespace Upscale.Connectors.Onvif;

public class OnvifCamera
{
    public string Name { get; set; }
    public Variable Variable { get; set; }

    private readonly Client _client;
    private readonly Connector _connector;
    private OnvifClient? _onvifClient;
    private StreamHandle? _streamHandle;
    private readonly Dictionary<string, StreamHandle> _playbackHandles = [];
    private CancellationTokenSource? _monitorCts;
    private readonly SemaphoreSlim _streamLock = new(1, 1);
    private readonly string _profileToken;

    public OnvifCamera(string name, Variable variable, Client client, Connector connector)
    {
        Name = name;
        Variable = variable;
        _client = client;
        _connector = connector;
        _profileToken = variable.FindSetting("ProfileToken", "").ToString()!;
    }

    public async Task StartStreaming()
    {
        if (_streamHandle is { IsActive: true })
            return;

        if (!await _streamLock.WaitAsync(0))
            return;

        try
        {
            if (_streamHandle is { IsActive: true })
                return;

            var address = Variable.FindSetting("Identifier", "").ToString();
            if (string.IsNullOrEmpty(address))
            {
                _ = _client.LogService().LogError("Camera", $"{Name}: No Identifier configured, cannot stream");
                return;
            }

            var (username, password) = ResolveCredentials();
            _onvifClient = new OnvifClient(address, username, password);
            await _onvifClient.ConnectAsync();

            var profileToken = Variable.FindSetting("ProfileToken", "")?.ToString();
            var rtspUrl = InjectCredentials(await _onvifClient.GetStreamUriAsync(profileToken), username, password);

            _streamHandle = _connector.StartStreamRelay(rtspUrl, Variable);

            _monitorCts = new CancellationTokenSource();
            _ = Task.Run(() => _connector.MonitorStreamAsync(_streamHandle, _monitorCts.Token));
        }
        catch (Exception ex)
        {
            _ = _client.LogService().LogError("Camera", $"{Name}: Failed to start stream: {ex.Message}");
            _onvifClient?.Dispose();
            _onvifClient = null;
        }
        finally
        {
            _streamLock.Release();
        }
    }

    public async Task StopStreaming()
    {
        if (_monitorCts != null)
        {
            await _monitorCts.CancelAsync();
            _monitorCts.Dispose();
            _monitorCts = null;
        }

        if (_streamHandle != null)
        {
            _connector.StopStreamRelay(_streamHandle);
            _streamHandle = null;
        }

        foreach (var handle in _playbackHandles.Values)
            _connector.StopStreamRelay(handle);
        _playbackHandles.Clear();

        _onvifClient?.Dispose();
        _onvifClient = null;
    }

    public async Task StartPlayback(PlaybackRequest playback)
    {
        var address = Variable.FindSetting("Identifier", "").ToString();
        if (string.IsNullOrEmpty(address))
        {
            _ = _client.LogService().LogError("Camera", $"{Name}: No Identifier configured, cannot playback");
            return;
        }

        try
        {
            var (username, password) = ResolveCredentials();
            string rtspUrl;
            using (var client = new OnvifClient(address, username, password))
            {
                await client.ConnectAsync();
                var profileToken = Variable.FindSetting("ProfileToken", "")?.ToString();
                rtspUrl = InjectCredentials(await client.GetStreamUriAsync(profileToken), username, password);
            }

            var playbackPath = $"{_client.Connector.Name}/{Variable.Name}/playback/{playback.SessionId}";

            _ = _client.LogService().LogDebug("Camera", $"{Name}: Starting playback relay → {playbackPath}");
            var handle = _connector.StartStreamRelay(rtspUrl, Variable, playbackPath);
            _playbackHandles[playback.SessionId] = handle;
        }
        catch (Exception ex)
        {
            _ = _client.LogService().LogError("Camera", $"{Name}: Failed to start playback: {ex.Message}");
        }
    }

    public Task StopPlayback(string sessionId)
    {
        if (_playbackHandles.TryGetValue(sessionId, out var handle))
        {
            _connector.StopStreamRelay(handle);
            _playbackHandles.Remove(sessionId);
        }
        return Task.CompletedTask;
    }

    public async Task HandlePtzCommand(PtzCommand cmd)
    {
        var address = Variable.FindSetting("Identifier", "").ToString();
        if (string.IsNullOrEmpty(address)) return;

        try
        {
            var (username, password) = ResolveCredentials();
            var ptz = await OnvifClientFactory.CreatePTZClientAsync(address, username, password);

            switch (cmd.Action)
            {
                case PtzAction.Move:
                case PtzAction.Zoom:
                    await ptz.ContinuousMoveAsync(_profileToken, new PTZSpeed
                    {
                        PanTilt = new()
                        {
                            x = cmd.Pan,
                            y = cmd.Tilt
                        },
                        Zoom = new()
                        {
                            x = cmd.Zoom
                        }
                    }, null);
                    var ptz_status = await ptz.GetStatusAsync(_profileToken);
                    await _client.StateService().SetStateAsync($"{Variable.Name}.PtzStatus.Pan", ptz_status.Position?.PanTilt?.x ?? -255);
                    await _client.StateService().SetStateAsync($"{Variable.Name}.PtzStatus.Tilt", ptz_status.Position?.PanTilt?.y ?? -255);
                    await _client.StateService().SetStateAsync($"{Variable.Name}.PtzStatus.Zoom", ptz_status.Position?.Zoom?.x ?? -255);
                    break;

                case PtzAction.Stop:
                    await ptz.StopAsync(_profileToken, true, true);
                    break;

                case PtzAction.GoToPreset:
                    if (cmd.PresetId.HasValue)
                    {
                        var presets = await ptz.GetPresetsAsync(_profileToken);
                        var preset = presets.Preset[cmd.PresetId.Value];
                        await ptz.GotoPresetAsync(_profileToken, preset.token, null);
                    }
                    break;

                case PtzAction.SetPreset:
                    break;
            }
        }
        catch (Exception ex)
        {
            _ = _client.LogService().LogError("PTZ", $"Command failed for {Name}: {ex.Message}");
        }
    }

    public async Task AbsolutePtzAsync(dynamic value)
    {
        var address = Variable.FindSetting("Identifier", "").ToString();
        if (string.IsNullOrEmpty(address)) return;

        var (username, password) = ResolveCredentials();

        try
        {
            var json = value?.ToString() ?? "";
            var pos = System.Text.Json.JsonSerializer.Deserialize<PtzPosition>(json);
            if (pos == null) return;

            var ptz = await OnvifClientFactory.CreatePTZClientAsync(address, username, password);
            await ptz.AbsoluteMoveAsync(_profileToken, new PTZVector
            {
                PanTilt = new Vector2D { x = (float)pos.Pan, y = (float)pos.Tilt },
                Zoom = new Vector1D { x = (float)pos.Zoom }
            }, null);

            var ptz_status = await ptz.GetStatusAsync(_profileToken);
            var state = _client.StateService();
            await state.SetStateAsync($"{Name}.PtzStatus.Pan", ptz_status.Position?.PanTilt?.x ?? -255);
            await state.SetStateAsync($"{Name}.PtzStatus.Tilt", ptz_status.Position?.PanTilt?.y ?? -255);
            await state.SetStateAsync($"{Name}.PtzStatus.Zoom", ptz_status.Position?.Zoom?.x ?? -255);
        }
        catch (Exception ex)
        {
            await _client.LogService().LogError("PTZ", $"Absolute move failed for {Name}: {ex.Message}");
        }
    }

    public async Task HandlePresetCmdAsync(dynamic value)
    {
        var address = Variable.FindSetting("Identifier", "").ToString();
        if (string.IsNullOrEmpty(address)) return;

        var (username, password) = ResolveCredentials();

        try
        {
            var json = value?.ToString() ?? "";
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;

            var command = root.GetProperty("command").GetString()?.ToLowerInvariant() ?? "";
            var ptz = await OnvifClientFactory.CreatePTZClientAsync(address, username, password);

            switch (command)
            {
                case "goto":
                {
                    var token = root.GetProperty("id").GetString() ?? "";
                    if (string.IsNullOrEmpty(token)) return;
                    await ptz.GotoPresetAsync(_profileToken, token, null);
                    await _client.LogService().LogInformation("Preset", $"{Name}: Activated preset '{token}'");
                    break;
                }
                case "create":
                {
                    var name = root.GetProperty("name").GetString() ?? "Preset";
                    var result = await ptz.SetPresetAsync(new SetPresetRequest
                    {
                        ProfileToken = _profileToken,
                        PresetName = name
                    });
                    await _client.LogService().LogInformation("Preset", $"{Name}: Created preset '{result.PresetToken}'");
                    break;
                }
                case "update":
                {
                    var token = root.GetProperty("id").GetString() ?? "";
                    var name = root.GetProperty("name").GetString() ?? "";
                    if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(name)) return;
                    await ptz.SetPresetAsync(new SetPresetRequest
                    {
                        ProfileToken = _profileToken,
                        PresetToken = token,
                        PresetName = name
                    });
                    await _client.LogService().LogInformation("Preset", $"{Name}: Updated preset '{token}' to '{name}'");
                    break;
                }
                case "delete":
                {
                    var token = root.GetProperty("id").GetString() ?? "";
                    if (string.IsNullOrEmpty(token)) return;
                    await ptz.RemovePresetAsync(_profileToken, token);
                    await _client.LogService().LogInformation("Preset", $"{Name}: Deleted preset '{token}'");
                    break;
                }
                default:
                    await _client.LogService().LogError("Preset", $"{Name}: Unknown command '{command}'");
                    return;
            }

            await RefreshPresetsAsync(address, username, password);
        }
        catch (Exception ex)
        {
            await _client.LogService().LogError("Preset", $"Preset command failed for {Name}: {ex.Message}");
        }
    }

    public async Task HandleSnapshotCmdAsync(dynamic value)
    {
        var address = Variable.FindSetting("Identifier", "").ToString();
        if (string.IsNullOrEmpty(address)) return;

        try
        {
            var (username, password) = ResolveCredentials();
            using var onvifClient = new OnvifClient(address, username, password);
            await onvifClient.ConnectAsync();
            var snapshotUrl = await onvifClient.GetSnapshotUriAsync(_profileToken);

            using var http = new HttpClient();
            var imageData = await http.GetByteArrayAsync(snapshotUrl);
            if (imageData == null || imageData.Length == 0)
            {
                await _client.LogService().LogError("Snapshot", $"{Name}: Empty snapshot image");
                return;
            }

            var snapshotName = value?.ToString() ?? $"{Name}.jpg";
            await _client.SnapshotService().UploadAsync(imageData, snapshotName);
            await _client.LogService().LogInformation("Snapshot", $"{Name}: Snapshot uploaded as '{snapshotName}'");
        }
        catch (Exception ex)
        {
            await _client.LogService().LogError("Snapshot", $"Snapshot failed for {Name}: {ex.Message}");
        }
    }

    private async Task RefreshPresetsAsync(string address, string username, string password)
    {
        try
        {
            var ptz = await OnvifClientFactory.CreatePTZClientAsync(address, username, password);
            var response = await ptz.GetPresetsAsync(_profileToken);
            var presets = response.Preset?.Select(p => new { id = p.token, name = p.Name }).ToList() ?? [];
            await _client.StateService().SetStateAsync($"{Name}.Presets", presets);
        }
        catch (Exception ex)
        {
            _ = _client.LogService().LogError("Preset", $"{Name}: Failed to refresh presets: {ex.Message}");
        }
    }

    public async Task<bool> CheckReachableAsync()
    {
        var address = Variable.FindSetting("Identifier", "").ToString();
        if (string.IsNullOrEmpty(address)) return false;

        var (username, password) = ResolveCredentials();
        var client = new OnvifClient(address, username, password);
        return await client.IsReachableAsync();
    }

    public async Task CheckCapabilitiesAsync()
    {
        var address = Variable.FindSetting("Identifier", "").ToString();
        if (string.IsNullOrEmpty(address)) return;

        var profileToken = Variable.FindSetting("ProfileToken", "").ToString();
        if (string.IsNullOrWhiteSpace(profileToken)) return;

        var (username, password) = ResolveCredentials();

        try
        {
            var device = await OnvifClientFactory.CreateDeviceClientAsync(address, username, password);
            var media = await OnvifClientFactory.CreateMediaClientAsync(address, username, password);

            var caps = await device.GetCapabilitiesAsync([CapabilityCategory.All]);
            var profile = await media.GetProfileAsync(profileToken);

            var hasPtz = caps.Capabilities.PTZ != null && profile.PTZConfiguration != null;
            var hasAudio = profile.AudioEncoderConfiguration != null;
            var hasAnalytics = caps.Capabilities.Analytics != null;

            // ONVIF basic profile cameras don't expose recording/export control — those are NVR-side
            var hasRecording = false;
            var hasExports = false;

            // All ONVIF cameras with a media profile support GetSnapshotUri
            var hasSnapshots = true;

            var state = _client.StateService();
            await state.SetStateAsync($"{Name}.Capabilities.Ptz", hasPtz);
            await state.SetStateAsync($"{Name}.Capabilities.Recording", hasRecording);
            await state.SetStateAsync($"{Name}.Capabilities.Audio", hasAudio);
            await state.SetStateAsync($"{Name}.Capabilities.Analytics", hasAnalytics);
            await state.SetStateAsync($"{Name}.Capabilities.Exports", hasExports);
            await state.SetStateAsync($"{Name}.Capabilities.Snapshots", hasSnapshots);

            if (hasPtz)
                await RefreshPresetsAsync(address, username, password);
        }
        catch (Exception ex)
        {
            _ = _client.LogService().LogError("Capabilities", $"{Name}: Failed to check capabilities: {ex.Message}");
        }
    }

    private (string username, string password) ResolveCredentials()
    {
        var username = Variable.FindSetting("User", "")?.ToString() ?? "";
        var password = Variable.FindSetting("Password", "")?.ToString() ?? "";

        if (string.IsNullOrEmpty(username))
        {
            var settings = _client.Connector.Settings ?? [];
            username = settings.Find(s => s.Key == "DefaultUsername")?.Value?.ToString() ?? "";
            password = settings.Find(s => s.Key == "DefaultPassword")?.Value?.ToString() ?? "";
        }

        return (username, password);
    }

    private static string InjectCredentials(string rtspUrl, string username, string password)
    {
        if (string.IsNullOrEmpty(username)) return rtspUrl;

        var uri = new Uri(rtspUrl);
        return $"{uri.Scheme}://{Uri.EscapeDataString(username)}:{Uri.EscapeDataString(password)}@{uri.Authority}{uri.PathAndQuery}";
    }
}
