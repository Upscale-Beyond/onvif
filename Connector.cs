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
    private NotificationEventListener<State>? _stateEvents;
    private NotificationEventListener<Quantum.DevKit.Models.Sense.Connector>? _connectorEvents;

    private string _rootName;
    public override async Task InitAsync(Client client)
    {
        SetClient(client);

        _variableEvents = client.VariableService(true).Events!;
        _connectorEvents = client.ConnectorService(true).Events!;
        _stateEvents = client.StateService(true).Events!;
        _variableEvents.On("added", HandleVariableAdded);
        _variableEvents.On("updated", HandleVariableUpdated);
        _variableEvents.On("removed", HandleVariableRemoved);
        _connectorEvents.On("updated", HandleConnectorUpdated);
        _stateEvents.On("updated", HandleStateUpdated);
        _cache.AddRange(await _client.GetMyVariablesAsync());
        _rootName = _client.Connector.FindSetting("RootName", _client.Connector.Name).ToString()!;

        var exists = await _client.VariableService().FindAsync(_rootName);
        if (exists == null)
        {
            await _client.VariableService().AddAsync(new()
            {
                Name = _rootName,
                Comment = $"Connector {_client.Connector.Name}'s root variable"
            });
        }

        await DiscoverOnvifDevicesAsync();
    }

    public override Task RunAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) return Task.CompletedTask;

        foreach (var variable in _cache)
        {
            var identifier = variable.FindSetting("Identifier", "")?.ToString() ?? "";
            var profileToken = variable.FindSetting("ProfileToken", "")?.ToString() ?? "";
            if (string.IsNullOrEmpty(identifier) || string.IsNullOrEmpty(profileToken)) continue;

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
                await camera.CheckCapabilitiesAsync();
            }
            catch (Exception ex)
            {
                await ReportCameraStatus(camera.Name, "Offline");
                await _client.LogService().LogError(camera.Name, ex.Message);
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

            var enabled = (_client.Connector.Settings ?? []).Find(s => s.Key == "EnablePeriodicDiscovery")?.Value;
            if (enabled is not true) continue;

            try
            {
                await DiscoverOnvifDevicesAsync();
            }
            catch (Exception ex)
            {
                _ = _client.LogService().LogError(_client.Connector.Name, $"Periodic discovery failed: {ex.Message}");
            }
        }
    }

    private async Task DiscoverOnvifDevicesAsync()
    {
        var connSettings = _client.Connector.Settings ?? [];
        var defaultUsername = connSettings.Find(s => s.Key == "DefaultUsername")?.Value?.ToString() ?? "";
        var defaultPassword = connSettings.Find(s => s.Key == "DefaultPassword")?.Value?.ToString() ?? "";

        var cameraType = await _client.DataTypeService().FindWithFilterAsync(Filter.Create(f => f.Eq("Name", "Camera").Eq("IsSystem", true)));
        if (cameraType == null)
        {
            await _client.LogService().LogError(_client.Connector.Name, "System Camera DataType not found. Cannot create variables.");
            return;
        }

        // Real stream variables have a non-empty ProfileToken
        var existingStreams = _cache
            .Where(v => !string.IsNullOrEmpty(v.FindSetting("ProfileToken", "")?.ToString()))
            .Select(v => $"{v.FindSetting("Identifier", "")}|{v.FindSetting("ProfileToken", "")}")
            .ToHashSet();

        // Placeholder variables have Identifier but no ProfileToken
        var existingPlaceholderAddresses = _cache
            .Where(v => string.IsNullOrEmpty(v.FindSetting("ProfileToken", "")?.ToString())
                     && !string.IsNullOrEmpty(v.FindSetting("Identifier", "")?.ToString()))
            .Select(v => v.FindSetting("Identifier", "")?.ToString() ?? "")
            .ToHashSet();

        var existingByName = _cache.Select(v => v.Name).ToHashSet();

        try
        {
            var discovery = new Discovery();
            int streamsCreated = 0;
            await foreach (var device in discovery.DiscoverAsync(10))
            {
                var xAddr = device.XAddresses.FirstOrDefault(a => a.StartsWith("http"));
                if (string.IsNullOrEmpty(xAddr)) continue;
                var deviceAddress = new Uri(xAddr).Authority;

                var deviceName = device.Model ?? device.Mfr ?? deviceAddress;
                var deviceSafeName = SanitizeName(deviceName);

                IEnumerable<(string Token, string Name)> profiles;
                try
                {
                    using var onvifClient = new Onvif.OnvifClient(deviceAddress, defaultUsername, defaultPassword);
                    await onvifClient.ConnectAsync();
                    profiles = await onvifClient.GetProfilesAsync();
                }
                catch (Exception ex)
                {
                    await _client.LogService().LogError("Onvif", $"Could not connect to '{deviceName}' ({deviceAddress}): {ex.Message}");

                    if (!existingPlaceholderAddresses.Contains(deviceAddress))
                        await CreatePlaceholderVariableAsync(deviceAddress, deviceName, deviceSafeName, cameraType, existingByName);

                    continue;
                }

                streamsCreated += await CreateStreamVariablesAsync(deviceAddress, deviceName, deviceSafeName, profiles, cameraType, existingByName, existingStreams);
            }

            await _client.LogService().LogInformation("Onvif", $"Discovery complete — {streamsCreated} new stream(s) created.");
        }
        catch (Exception ex)
        {
            await _client.LogService().LogError("Onvif", $"WS-Discovery failed: {ex.Message}");
        }
    }

    private async Task CreatePlaceholderVariableAsync(string deviceAddress, string deviceName, string deviceSafeName, DataType cameraType, HashSet<string> existingByName)
    {
        var fullName = $"{_rootName}.{deviceSafeName}";
        if (existingByName.Contains(fullName))
        {
            var counter = 2;
            while (existingByName.Contains($"{fullName}_{counter}")) counter++;
            fullName = $"{fullName}_{counter}";
        }

        try
        {
            var variable = new Variable
            {
                Name = fullName,
                Comment = deviceName,
                ConnectorId = _client.Connector._id,
                IsDirty = true,
                DirtyReason = "invalid credentials",
                Settings = cameraType.Settings
            };

            var result = await _client.VariableService().AddAsync(variable);
            if (result == null) return;
            if (result.Settings?.FirstOrDefault(s => s.Key == "Identifier") is { } idSetting)
                idSetting.Value = deviceAddress;
            result.OpenWithApp = "eye-viewer";
            await _client.VariableService().UpdateAsync(result);
            _cache.Add(result);
            existingByName.Add(fullName);

            await _client.LogService().LogInformation("Onvif", $"Created placeholder '{fullName}' for '{deviceName}' — set credentials to activate streams.");
        }
        catch (Exception ex)
        {
            await _client.LogService().LogError("Onvif", $"Failed to create placeholder for '{deviceName}': {ex.Message}");
        }
    }

    private async Task<int> CreateStreamVariablesAsync(string deviceAddress, string deviceName, string deviceSafeName, IEnumerable<(string Token, string Name)> profiles, DataType cameraType, HashSet<string> existingByName, HashSet<string> existingStreams, Variable? placeholder = null)
    {
        var created = 0;
        foreach (var (profileToken, profileName) in profiles)
        {
            var streamKey = $"{deviceAddress}|{profileToken}";
            if (existingStreams.Contains(streamKey)) continue;

            var profileSafeName = SanitizeName(profileName);
            var fullName = $"{_rootName}.{deviceSafeName}.{profileSafeName}";

            if (existingByName.Contains(fullName))
            {
                var counter = 2;
                while (existingByName.Contains($"{fullName}_{counter}")) counter++;
                fullName = $"{fullName}_{counter}";
            }

            try
            {
                var variable = new Variable
                {
                    Name = fullName,
                    Comment = $"{deviceName} — {profileName}",
                    ConnectorId = _client.Connector._id,
                    DataTypeId = cameraType._id,
                };

                var result = await _client.VariableService().AddAsync(variable);
                if (result == null) continue;
                if (result.Settings?.FirstOrDefault(s => s.Key == "Identifier") is { } idSetting)
                    idSetting.Value = deviceAddress;
                if (result.Settings?.FirstOrDefault(s => s.Key == "ProfileToken") is { } ptSetting)
                    ptSetting.Value = profileToken;
                if (placeholder != null)
                {
                    if (result.Settings?.FirstOrDefault(s => s.Key == "User") is { } userSetting)
                        userSetting.Value = placeholder.FindSetting("User", "");
                    if (result.Settings?.FirstOrDefault(s => s.Key == "Password") is { } passwordSetting)
                        passwordSetting.Value = placeholder.FindSetting("Password", "");
                }

                result.OpenWithApp = "eye-viewer";
                await _client.VariableService().UpdateAsync(result);
                _cache.Add(result);
                existingStreams.Add(streamKey);
                existingByName.Add(fullName);

                await _client.LogService().LogInformation("Onvif", $"Created stream '{fullName}' (profile: {profileName})");
                created++;
            }
            catch (Exception ex)
            {
                await _client.LogService().LogError("Onvif", $"Failed to create stream variable '{fullName}': {ex.Message}");
            }
        }
        return created;
    }

    private async Task TryPromotePlaceholderAsync(Variable placeholder)
    {
        var deviceAddress = placeholder.FindSetting("Identifier", "")?.ToString() ?? "";
        if (string.IsNullOrEmpty(deviceAddress)) return;

        var username = placeholder.FindSetting("User", "")?.ToString() ?? "";
        var password = placeholder.FindSetting("Password", "")?.ToString() ?? "";
        if (string.IsNullOrEmpty(username))
        {
            var connSettings = _client.Connector.Settings ?? [];
            username = connSettings.Find(s => s.Key == "DefaultUsername")?.Value?.ToString() ?? "";
            password = connSettings.Find(s => s.Key == "DefaultPassword")?.Value?.ToString() ?? "";
        }

        IEnumerable<(string Token, string Name)> profiles;
        try
        {
            using var onvifClient = new Onvif.OnvifClient(deviceAddress, username, password);
            await onvifClient.ConnectAsync();
            profiles = await onvifClient.GetProfilesAsync();
        }
        catch
        {
            return; // Credentials still wrong — leave placeholder dirty
        }

        var cameraType = await _client.DataTypeService().FindWithFilterAsync(Filter.Create(f => f.Eq("Name", "Camera").Eq("IsSystem", true)));
        if (cameraType == null) return;

        var deviceName = placeholder.Comment ?? deviceAddress;
        var deviceSafeName = SanitizeName(deviceName);

        var existingStreams = _cache
            .Where(v => !string.IsNullOrEmpty(v.FindSetting("ProfileToken", "")?.ToString()))
            .Select(v => $"{v.FindSetting("Identifier", "")}|{v.FindSetting("ProfileToken", "")}")
            .ToHashSet();
        var existingByName = _cache.Select(v => v.Name).ToHashSet();

        var created = await CreateStreamVariablesAsync(deviceAddress, deviceName, deviceSafeName, profiles, cameraType, existingByName, existingStreams, placeholder);
        if (created == 0) return;

        // Mark placeholder as clean — streams are now active
        try
        {
            placeholder.IsDirty = false;
            placeholder.DirtyReason = null;
            placeholder.ConnectorId = null;
            placeholder.Settings = [];
            await _client.VariableService().UpdateAsync(placeholder);
            await _client.LogService().LogInformation("Onvif", $"Placeholder '{placeholder.Name}' activated with {created} stream variable(s).");
        }
        catch (Exception ex)
        {
            await _client.LogService().LogError("Onvif", $"Failed to clear dirty state on '{placeholder.Name}': {ex.Message}");
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

        var identifier = variable.FindSetting("Identifier", "")?.ToString() ?? "";
        var profileToken = variable.FindSetting("ProfileToken", "")?.ToString() ?? "";
        if (!string.IsNullOrEmpty(identifier) && !string.IsNullOrEmpty(profileToken))
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



            var updated = variable;
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

            var identifier = updated.FindSetting("Identifier", "")?.ToString() ?? "";
            var profileToken = updated.FindSetting("ProfileToken", "")?.ToString() ?? "";

            if (!string.IsNullOrEmpty(identifier) && string.IsNullOrEmpty(profileToken) && updated.IsDirty == true)
            {
                // Dirty placeholder updated — re-attempt profile discovery with (possibly fixed) credentials
                await TryPromotePlaceholderAsync(updated);
            }
            else if (!string.IsNullOrEmpty(identifier) && !string.IsNullOrEmpty(profileToken))
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

    private async void HandleConnectorUpdated(Quantum.DevKit.Models.Sense.Connector connector)
    {
        if (connector._id != _client.Connector._id) return;

        // updated settings
        _client.Connector.Settings = connector.Settings;
    }

    private async void HandleStateUpdated(State state)
    {
        if (state.VariableName == null) return;

        var cameraVariable = _cache.FirstOrDefault(v => state.VariableName.StartsWith(v.Name + "."));
        if (cameraVariable == null) return;
        if (!_cameras.TryGetValue(cameraVariable.Name, out var camera)) return;

        if (state.VariableName.EndsWith(".PtzCmd"))
            await camera.AbsolutePtzAsync(state.Value);
        else if (state.VariableName.EndsWith(".PresetCmd"))
            await camera.HandlePresetCmdAsync(state.Value);
        else if (state.VariableName.EndsWith(".SnapshotCmd"))
            await camera.HandleSnapshotCmdAsync(state.Value);
    }
    public override void Dispose()
    {
        foreach (var camera in _cameras.Values)
        {
            camera.StopStreaming().GetAwaiter().GetResult();
        }

        _variableEvents?.Dispose();
        _connectorEvents?.Dispose();
        base.Dispose();
    }
}
