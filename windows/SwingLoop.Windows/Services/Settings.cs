using System.Text.Json;

namespace SwingLoop.Services;

public sealed class CameraSettings
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public bool ShowLive { get; set; } = true;
    public bool ShowReplay { get; set; } = true;
    /// <summary>Clockwise mounting rotation: 0, 90, 180 or 270.</summary>
    public int Rotation { get; set; }
    /// <summary>Small leveling correction in degrees (-15..15); cropped so no corners show.</summary>
    public float FineRotation { get; set; }
    public bool Mirror { get; set; }
    public float Zoom { get; set; } = 1;
    public string Position { get; set; } = "face-on";
    /// <summary>The camera whose frames drive swing detection.</summary>
    public bool IsDetector { get; set; }
}

public sealed class PipSettings
{
    public int X { get; set; } = -1;
    public int Y { get; set; } = -1;
    public int Width { get; set; } = 480;
    public int Height { get; set; } = 300;
    public string Content { get; set; } = "replay";  // replay | live | both
    public bool LockAspect { get; set; } = true;
    public bool ShowAnnotations { get; set; } = true;
}

public sealed class AppSettings
{
    public string SessionsRoot { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "SwingLoop");
    public List<CameraSettings> Cameras { get; set; } = [];
    public string? MicrophoneId { get; set; }

    public int MaxCaptureHeight { get; set; } = 1080;
    public double PreRollSeconds { get; set; } = 2.0;
    public double PostRollSeconds { get; set; } = 1.5;
    public int BufferMemoryMbPerCamera { get; set; } = 1200;

    public bool UsePose { get; set; } = true;
    public bool UseAudio { get; set; } = true;
    public bool UseMotion { get; set; } = true;
    public bool RequireImpactSound { get; set; } = true;
    public bool SavePracticeSwings { get; set; }

    public bool AutoReplay { get; set; } = true;
    public double DefaultSpeed { get; set; } = 1.0;
    public bool ShowLiveInset { get; set; } = true;
    public bool MultiLiveView { get; set; } = true;
    public bool MultiReplayView { get; set; } = true;
    /// <summary>"off" keeps each camera's own shape; otherwise every tile is cropped to it.</summary>
    public string UniformAspect { get; set; } = "off";

    public PipSettings Pip { get; set; } = new();
    public List<string> RecentColors { get; set; } = ["#FF3B30FF", "#FFD60AFF", "#30D158FF", "#0A84FFFF", "#FFFFFFFF"];

    public CameraSettings CameraFor(string id, string name)
    {
        var c = Cameras.FirstOrDefault(x => x.Id == id);
        if (c is null)
        {
            c = new CameraSettings { Id = id, Name = name, IsDetector = Cameras.Count == 0 };
            Cameras.Add(c);
        }
        c.Name = name;
        return c;
    }

    public static float ParseAspect(string s) => s switch
    {
        "16:9" => 16f / 9f,
        "4:3" => 4f / 3f,
        "1:1" => 1f,
        "9:16" => 9f / 16f,
        "3:4" => 3f / 4f,
        _ => 0f,
    };
}

public sealed class SettingsService
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SwingLoop");
    private static readonly string FilePath = Path.Combine(Dir, "settings.json");
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private CancellationTokenSource? _pendingSave;

    public AppSettings Current { get; private set; } = new();

    public void Load()
    {
        try
        {
            if (File.Exists(FilePath))
                Current = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options) ?? new();
        }
        catch (Exception)
        {
            // Corrupt settings: keep defaults, but don't overwrite the file until the user changes something.
            Current = new AppSettings();
        }
    }

    /// <summary>Debounced so sliders don't hammer the disk.</summary>
    public void Save()
    {
        _pendingSave?.Cancel();
        var cts = _pendingSave = new CancellationTokenSource();
        _ = Task.Delay(400, cts.Token).ContinueWith(t =>
        {
            if (!t.IsCanceled) SaveNow();
        }, TaskScheduler.Default);
    }

    public void SaveNow()
    {
        Directory.CreateDirectory(Dir);
        string tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(Current, Options));
        File.Move(tmp, FilePath, overwrite: true);
    }
}
