using Windows.Devices.Enumeration;
using Windows.Media;
using Windows.Media.Audio;
using Windows.Media.Capture;
using Windows.Media.Render;
using WinRT;

namespace SwingLoop.Capture;

/// <summary>
/// Streams microphone PCM (mono float) with capture timestamps on the shared
/// clock. The swing detector listens for the club/ball impact transient.
/// </summary>
public sealed class AudioCapture : IDisposable
{
    private AudioGraph? _graph;
    private AudioDeviceInputNode? _input;
    private AudioFrameOutputNode? _output;
    private float[] _scratch = new float[4096];

    /// <summary>Raised on the audio thread: (first sample time, mono samples, sample rate).</summary>
    public event Action<long, ReadOnlyMemory<float>, int>? SamplesArrived;

    public int SampleRate { get; private set; }
    public bool IsRunning => _graph is not null;

    public static async Task<IReadOnlyList<(string Id, string Name)>> EnumerateAsync()
    {
        var devices = await DeviceInformation.FindAllAsync(DeviceClass.AudioCapture);
        return devices.Where(d => d.IsEnabled).Select(d => (d.Id, d.Name)).ToList();
    }

    public async Task StartAsync(string? deviceId = null)
    {
        if (IsRunning) return;
        var settings = new AudioGraphSettings(AudioRenderCategory.Media)
        {
            QuantumSizeSelectionMode = QuantumSizeSelectionMode.LowestLatency,
        };
        var created = await AudioGraph.CreateAsync(settings);
        if (created.Status != AudioGraphCreationStatus.Success)
            throw new InvalidOperationException($"Audio unavailable: {created.Status}");
        _graph = created.Graph;

        var encoding = _graph.EncodingProperties;
        encoding.ChannelCount = 1;
        SampleRate = (int)encoding.SampleRate;

        DeviceInformation? device = deviceId is null ? null : await DeviceInformation.CreateFromIdAsync(deviceId);
        var input = device is null
            ? await _graph.CreateDeviceInputNodeAsync(MediaCategory.Other, encoding)
            : await _graph.CreateDeviceInputNodeAsync(MediaCategory.Other, encoding, device);
        if (input.Status != AudioDeviceNodeCreationStatus.Success)
        {
            Dispose();
            throw new InvalidOperationException(input.Status == AudioDeviceNodeCreationStatus.AccessDenied
                ? "Microphone access is blocked. Allow it in Settings > Privacy & security > Microphone."
                : $"Microphone unavailable: {input.Status}");
        }
        _input = input.DeviceInputNode;
        _output = _graph.CreateFrameOutputNode(encoding);
        _input.AddOutgoingConnection(_output);
        _graph.QuantumStarted += OnQuantum;
        _graph.Start();
    }

    private unsafe void OnQuantum(AudioGraph sender, object args)
    {
        if (_output is null) return;
        using var frame = _output.GetFrame();
        using var buffer = frame.LockBuffer(AudioBufferAccessMode.Read);
        using var reference = buffer.CreateReference();
        reference.As<IMemoryBufferByteAccess>().GetBuffer(out byte* data, out _);

        int count = (int)(buffer.Length / sizeof(float));
        if (count == 0) return;
        if (_scratch.Length < count) _scratch = new float[count];
        new ReadOnlySpan<float>(data, count).CopyTo(_scratch);

        long end = Clock.NowUs();
        long first = end - (long)count * 1_000_000 / SampleRate;
        SamplesArrived?.Invoke(first, new ReadOnlyMemory<float>(_scratch, 0, count), SampleRate);
    }

    public void Dispose()
    {
        if (_graph is not null)
        {
            _graph.QuantumStarted -= OnQuantum;
            _graph.Stop();
        }
        _output?.Dispose();
        _input?.Dispose();
        _graph?.Dispose();
        _output = null;
        _input = null;
        _graph = null;
    }
}
