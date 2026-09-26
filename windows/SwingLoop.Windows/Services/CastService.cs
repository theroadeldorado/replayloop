using Windows.Devices.Enumeration;
using Windows.Media.Casting;
using Windows.Media.Playback;

namespace SwingLoop.Services;

/// <summary>
/// Two ways to get SwingLoop onto a TV from Windows:
///  * Cast the replay video to a Miracast/DLNA device (this class), or
///  * mirror the whole screen with the system Cast flyout (Win+K), which is
///    how the full app or the PiP window sits on top of a golf simulator.
/// AirPlay is not available on Windows; the iPhone/iPad app provides it.
/// </summary>
public sealed class CastService
{
    private CastingConnection? _connection;

    public bool IsCasting => _connection?.State is CastingConnectionState.Connected or CastingConnectionState.Rendering;
    public string? DeviceName { get; private set; }
    public event Action? StateChanged;

    public async Task<IReadOnlyList<DeviceInformation>> FindDevicesAsync()
    {
        string selector = CastingDevice.GetDeviceSelector(CastingPlaybackTypes.Video);
        var devices = await DeviceInformation.FindAllAsync(selector);
        return devices.ToList();
    }

    public async Task<string?> CastAsync(DeviceInformation info, MediaPlayer player)
    {
        await StopAsync();
        var device = await CastingDevice.FromIdAsync(info.Id);
        if (device is null) return "That device is no longer available.";
        _connection = device.CreateCastingConnection();
        _connection.StateChanged += (_, _) => StateChanged?.Invoke();
        _connection.ErrorOccurred += (_, e) => StateChanged?.Invoke();
        var status = await _connection.RequestStartCastingAsync(player.GetAsCastingSource());
        if (status != CastingConnectionErrorStatus.Succeeded)
        {
            _connection.Dispose();
            _connection = null;
            return $"Casting failed: {status}";
        }
        DeviceName = info.Name;
        StateChanged?.Invoke();
        return null;
    }

    public async Task StopAsync()
    {
        if (_connection is null) return;
        try { await _connection.DisconnectAsync(); } catch (Exception) { }
        _connection.Dispose();
        _connection = null;
        DeviceName = null;
        StateChanged?.Invoke();
    }
}
