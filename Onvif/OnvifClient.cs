using Onvif.Core.Client.Camera;
using Onvif.Core.Client.Common;

namespace Upscale.Connectors.Onvif.Onvif;

public class OnvifClient : IDisposable
{
    private readonly string _address;
    private readonly string _username;
    private readonly string _password;
    private Camera? _camera;
    private string? _profileToken;

    public OnvifClient(string address, string username, string password)
    {
        _address = address;
        _username = username;
        _password = password;
        
    }

    public async Task ConnectAsync()
    {
        var account = new Account(_address, _username, _password);
        _camera = await Camera.CreateAsync(account, ex =>
        {
            throw ex;
        });

        if (_camera == null)
            throw new Exception($"Failed to connect to ONVIF device at {_address}");

        _profileToken = _camera.Profile?.token;
    }

    public async Task<string> GetStreamUriAsync()
    {
        if (_camera == null)
            throw new InvalidOperationException("Not connected. Call ConnectAsync first.");

        if (string.IsNullOrEmpty(_profileToken))
            throw new InvalidOperationException("No media profile available on device.");

        var streamSetup = new StreamSetup
        {
            Stream = StreamType.RTPUnicast,
            Transport = new Transport { Protocol = TransportProtocol.RTSP }
        };

        var uri = await _camera.Media.GetStreamUriAsync(streamSetup, _profileToken);
        return uri?.Uri ?? throw new Exception("Failed to get stream URI from device.");
    }

    public Task<OnvifDeviceInfo> GetDeviceInfoAsync()
    {
        if (_camera == null)
            throw new InvalidOperationException("Not connected. Call ConnectAsync first.");

        var info = _camera.Profile;
        return Task.FromResult(new OnvifDeviceInfo
        {
            Address = _address,
            Name = info?.Name ?? _address,
            Manufacturer = "",
            Model = ""
        });
    }

    public async Task ContinuousMoveAsync(float pan, float tilt, float zoom, float speed)
    {
        if (_camera == null || string.IsNullOrEmpty(_profileToken)) return;

        var velocity = new PTZSpeed
        {
            PanTilt = new Vector2D { x = pan * speed, y = tilt * speed },
            Zoom = new Vector1D { x = zoom * speed }
        };

        await _camera.Ptz.ContinuousMoveAsync(_profileToken, velocity, TimeSpan.FromSeconds(5));
    }

    public async Task StopAsync()
    {
        if (_camera == null || string.IsNullOrEmpty(_profileToken)) return;

        await _camera.Ptz.StopAsync(_profileToken, true, true);
    }

    public async Task GotoPresetAsync(int presetId)
    {
        if (_camera == null || string.IsNullOrEmpty(_profileToken)) return;

        var presetsResponse = await _camera.Ptz.GetPresetsAsync(_profileToken);
        var presets = presetsResponse?.Preset;
        var preset = presets?.FirstOrDefault(p => p.Name == presetId.ToString())
                     ?? presets?.ElementAtOrDefault(presetId - 1);

        if (preset?.token != null)
        {
            await _camera.Ptz.GotoPresetAsync(_profileToken, preset.token, null);
        }
    }

    public Task<bool> IsReachableAsync()
    {
        try
        {
            var account = new Account(_address, _username, _password);
            var camera = Camera.Create(account, _ => { });
            return Task.FromResult(camera != null);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    public void Dispose()
    {
        _camera = null;
    }
}
