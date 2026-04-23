using OnvifDiscovery;
using OnvifDiscovery.Models;
using Quantum.DevKit.Connection;
using Quantum.DevKit.Connection.Notification;
using Quantum.DevKit.Models.Data;
using Quantum.DevKit.Models.Streaming;
using Quantum.Models.Query;

namespace Upscale.Connectors.Onvif;

public class Connector : StreamerConnector
{
    private readonly List<Variable> _cache = new();
    private readonly Dictionary<string, OnvifCamera> _cameras = new();

    private NotificationEventListener<Variable>? _variableEvents;

    public override async Task InitAsync(Client client)
    {
        SetClient(client);

        _variableEvents = client.VariableService(true).Events!;
        _variableEvents.On("added", HandleVariableAdded);
        _variableEvents.On("updated", HandleVariableUpdated);
        _variableEvents.On("removed", HandleVariableRemoved);
        _cache.AddRange(await _client.GetMyVariablesAsync());

        await DiscoverOnvifDevicesAsync();
    }

    public override Task RunAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return Task.CompletedTask;

        foreach (var variable in _cache)
        {
            var identifier = variable.FindSetting("Identifier", "").ToString();
            if (string.IsNullOrEmpty(identifier)) continue;

            var camera = new OnvifCamera(variable.Name, variable, _client, this);
            _cameras[variable.Name] = camera;
        }

        _ = Task.Run(() => MonitorStatusAsync(cancellationToken));
        _ = Task.Run(() => PeriodicDiscoveryAsync(cancellationToken));

        cancellationToken.WaitHandle.WaitOne();

        _variableEvents?.Dispose();
        return Task.CompletedTask;
    }

    private async Task MonitorStatusAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await UpdateCameraStatusesAsync();

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task UpdateCameraStatusesAsync()
    {
        foreach (var camera in _cameras.Values.ToList())
        {
            try
            {
                var reachable = await camera.CheckReachableAsync();
                await ReportCameraStatus(camera.Name, reachable ? "Online" : "Offline");
            }
            catch
            {
                await ReportCameraStatus(camera.Name, "Offline");
            }
        }
    }

    private async Task PeriodicDiscoveryAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(60), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                await DiscoverOnvifDevicesAsync();
            }
            catch (Exception ex)
            {
                _ = _client.LogService().LogError("Onvif", $"Periodic discovery failed: {ex.Message}");
            }
        }
    }

    private async Task DiscoverOnvifDevicesAsync()
    {
        var settings = _client.Connector.Settings ?? [];
        var defaultUsername = settings.Find(s => s.Key == "DefaultUsername")?.Value?.ToString() ?? "";
        var defaultPassword = settings.Find(s => s.Key == "DefaultPassword")?.Value?.ToString() ?? "";

        List<DiscoveryDevice> devices;
        try
        {
            var discovery = new Discovery();
            var result = discovery.DiscoverAsync(5);
            devices = new();
            await foreach (var device in result)
            {
                devices.Add(device);
            }

            await _client.LogService().LogInformation("Onvif", $"Discovered {devices.Count} device(s) on the network.");
        }
        catch (Exception ex)
        {
            await _client.LogService().LogError("Onvif", $"WS-Discovery failed: {ex.Message}");
            return;
        }

        var cameraType = await _client.DataTypeService().FindWithFilterAsync(Filter.Create(f => f.Eq("Name", "Camera").Eq("IsSystem", true)));
        if (cameraType == null)
        {
            await _client.LogService().LogError("Onvif", "System Camera DataType not found. Cannot create variables.");
            return;
        }

        var existingByIdentifier = _cache
            .Select(v => new { Variable = v, Id = v.FindSetting("Identifier", "")?.ToString() ?? "" })
            .Where(x => !string.IsNullOrEmpty(x.Id))
            .ToDictionary(x => x.Id, x => x.Variable);

        var existingByName = _cache.Select(v => v.Name).ToHashSet();

        foreach (var device in devices)
        {
            var deviceAddress = device.Address ?? "";
            if (string.IsNullOrEmpty(deviceAddress)) continue;

            if (existingByIdentifier.ContainsKey(deviceAddress))
                continue;

            var deviceName = device.Model ?? device.Mfr ?? deviceAddress;
            var safeName = SanitizeName(deviceName);
            var fullName = $"{_client.Connector.Name}.{safeName}";

            if (existingByName.Contains(fullName))
            {
                var counter = 2;
                while (existingByName.Contains($"{fullName}_{counter}"))
                    counter++;
                fullName = $"{fullName}_{counter}";
            }

            try
            {
                var variable = new Variable
                {
                    Name = fullName,
                    Comment = deviceName,
                    ConnectorId = _client.Connector._id,
                    DataTypeId = cameraType._id,
                };

                var result = await _client.VariableService().AddAsync(variable);
                result.Settings = new List<Setting>
                {
                    new() { Key = "Identifier", Value = deviceAddress },
                    new() { Key = "OverrideUsername", Value = "", Type = "string" },
                    new() { Key = "OverridePassword", Value = "", Type = "password" }
                };
                result.OpenWithApp = "eye-viewer";
                await _client.VariableService().UpdateAsync(result);
                if (result != null)
                    _cache.Add(result);

                await _client.LogService().LogInformation("Onvif", $"Created variable for camera '{deviceName}' (address: {deviceAddress})");
            }
            catch (Exception ex)
            {
                await _client.LogService().LogError("Onvif", $"Failed to create variable for device '{deviceName}': {ex.Message}");
            }
        }
    }

    protected override async Task HandleStartStream(Variable camera)
    {
        await _client.LogService().LogDebug("Stream", $"Start stream: {camera.Name}");
        if (_cameras.TryGetValue(camera.Name, out var cam))
        {
            await cam.StartStreaming();
        }
        else
        {
            await _client.LogService().LogError("Stream", $"Camera '{camera.Name}' not found");
        }
    }

    protected override async Task HandleStopStream(Variable camera, string path)
    {
        if (!_cameras.TryGetValue(camera.Name, out var cam)) return;

        var playbackIndex = path.IndexOf("/playback/", StringComparison.Ordinal);
        if (playbackIndex >= 0)
        {
            var sessionId = path[(playbackIndex + "/playback/".Length)..];
            await _client.LogService().LogDebug("Stream", $"Stop playback: {camera.Name} session={sessionId}");
            await cam.StopPlayback(sessionId);
            return;
        }

        await _client.LogService().LogDebug("Stream", $"Stop stream: {camera.Name}");
        await cam.StopStreaming();
    }

    protected override async Task HandlePtzCommand(Variable camera, PtzCommand command)
    {
        await _client.LogService().LogDebug("PTZ", $"{camera.Name} Action={command.Action}");
        if (_cameras.TryGetValue(camera.Name, out var cam))
        {
            await cam.HandlePtzCommand(command);
        }
    }

    protected override async Task HandlePlaybackStream(Variable camera, PlaybackRequest playback)
    {
        await _client.LogService().LogDebug("Playback", $"{camera.Name} {playback.StartTime} - {playback.EndTime}");
        if (_cameras.TryGetValue(camera.Name, out var cam))
        {
            await cam.StartPlayback(playback);
        }
    }

    protected override Variable? FindCameraByPath(string path)
    {
        // Path format: {tenantId}/{connectorName}/{cameraName}[/playback/{sessionId}]
        var parts = path.Split('/');
        if (parts.Length < 3) return null;

        var cameraName = parts[2];
        return _cache.FirstOrDefault(v =>
            v.Name == cameraName ||
            v.Name.Split('.').Last().Equals(cameraName, StringComparison.OrdinalIgnoreCase));
    }

    private static string SanitizeName(string name)
    {
        return new string(name.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray()).Trim('_');
    }

    private void HandleVariableAdded(Variable variable)
    {
        if (variable.ConnectorId != _client.Connector._id) return;
        _cache.Add(variable);

        var identifier = variable.FindSetting("Identifier", "").ToString();
        if (!string.IsNullOrEmpty(identifier))
        {
            var camera = new OnvifCamera(variable.Name, variable, _client, this);
            _cameras[variable.Name] = camera;
        }
    }

    private async void HandleVariableUpdated(Variable variable)
    {
        try
        {
            if (variable.ConnectorId != _client.Connector._id) return;

            Variable? fullVariable = null;
            try
            {
                fullVariable = await _client.VariableService().FindAsync(variable._id!);
            }
            catch (Exception ex)
            {
                _ = _client.LogService().LogError("Variable", $"Failed to fetch variable {variable._id}: {ex.Message}");
            }

            var updated = fullVariable ?? variable;
            var old = _cache.FirstOrDefault(v => v._id == updated._id);
            var oldName = old?.Name ?? updated.Name;

            if (_cameras.TryGetValue(oldName, out var existingCamera))
            {
                await existingCamera.StopStreaming();
                _cameras.Remove(oldName);
            }

            if (old != null)
                _cache[_cache.IndexOf(old)] = updated;
            else
                _cache.Add(updated);

            var identifier = updated.FindSetting("Identifier", "").ToString();
            if (!string.IsNullOrEmpty(identifier))
            {
                var camera = new OnvifCamera(updated.Name, updated, _client, this);
                _cameras[updated.Name] = camera;
            }
        }
        catch (Exception ex)
        {
            _ = _client.LogService().LogError("Variable", $"Unhandled error in HandleVariableUpdated: {ex.Message}");
        }
    }

    private async void HandleVariableRemoved(Variable variable)
    {
        try
        {
            if (variable.ConnectorId != _client.Connector._id) return;

            if (_cameras.TryGetValue(variable.Name, out var camera))
            {
                await camera.StopStreaming();
                _cameras.Remove(variable.Name);
            }

            _cache.RemoveAll(v => v._id == variable._id);
        }
        catch (Exception ex)
        {
            _ = _client.LogService().LogError("Variable", $"Unhandled error in HandleVariableRemoved: {ex.Message}");
        }
    }

    public override void Dispose()
    {
        foreach (var camera in _cameras.Values)
        {
            camera.StopStreaming().GetAwaiter().GetResult();
        }

        _variableEvents?.Dispose();
        base.Dispose();
    }
}
