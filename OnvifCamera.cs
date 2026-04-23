using Quantum.DevKit.Connection;
using Quantum.DevKit.Models.Data;
using Quantum.DevKit.Models.Streaming;
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

    public OnvifCamera(string name, Variable variable, Client client, Connector connector)
    {
        Name = name;
        Variable = variable;
        _client = client;
        _connector = connector;
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

            var rtspUrl = InjectCredentials(await _onvifClient.GetStreamUriAsync(), username, password);

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
                rtspUrl = InjectCredentials(await client.GetStreamUriAsync(), username, password);
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
            if (_onvifClient == null)
            {
                var (username, password) = ResolveCredentials();
                _onvifClient = new OnvifClient(address, username, password);
                await _onvifClient.ConnectAsync();
            }

            switch (cmd.Action)
            {
                case PtzAction.Move:
                case PtzAction.Zoom:
                    await _onvifClient.ContinuousMoveAsync(cmd.Pan, cmd.Tilt, cmd.Zoom, cmd.Speed);
                    break;

                case PtzAction.Stop:
                    await _onvifClient.StopAsync();
                    break;

                case PtzAction.GoToPreset:
                    if (cmd.PresetId.HasValue)
                        await _onvifClient.GotoPresetAsync(cmd.PresetId.Value);
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

    public async Task<bool> CheckReachableAsync()
    {
        var address = Variable.FindSetting("Identifier", "").ToString();
        if (string.IsNullOrEmpty(address)) return false;

        var (username, password) = ResolveCredentials();
        var client = new OnvifClient(address, username, password);
        return await client.IsReachableAsync();
    }

    private (string username, string password) ResolveCredentials()
    {
        var username = Variable.FindSetting("OverrideUsername", "")?.ToString() ?? "";
        var password = Variable.FindSetting("OverridePassword", "")?.ToString() ?? "";

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
