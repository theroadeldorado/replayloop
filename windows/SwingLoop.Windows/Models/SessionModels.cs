using System.Text.Json;
using System.Text.Json.Serialization;

namespace SwingLoop.Models;

// Mirrors the session.json schema written by the core (core/src/session.cpp).

public sealed class ClipDto
{
    public string CameraId { get; set; } = "";
    public string File { get; set; } = "";
    public string Thumbnail { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public double Fps { get; set; }
    public long DurationUs { get; set; }
    public long ImpactOffsetUs { get; set; }
    public int RotationDeg { get; set; }
}

public sealed class TimingDto
{
    public long AddressUs { get; set; }
    public long TakeawayUs { get; set; }
    public long TopUs { get; set; }
    public long ImpactUs { get; set; }
    public long FinishUs { get; set; }
    public float TempoRatio { get; set; }
}

public enum ShapeKind { Line, Arrow, Circle, Rectangle, Angle, Freehand, Text }

public sealed class ShapeDto
{
    public string Id { get; set; } = "";
    public ShapeKind Kind { get; set; }
    public List<float[]> Points { get; set; } = [];
    public string Color { get; set; } = "#FF3B30FF";
    public float StrokeWidth { get; set; } = 4;
    public bool Dashed { get; set; }
    public bool Persistent { get; set; }
    public string? CameraId { get; set; }
    public string? Text { get; set; }

    public ShapeDto Clone() => new()
    {
        Id = Id, Kind = Kind, Points = Points.Select(p => (float[])p.Clone()).ToList(), Color = Color,
        StrokeWidth = StrokeWidth, Dashed = Dashed, Persistent = Persistent, CameraId = CameraId, Text = Text,
    };
}

public sealed class ShotDto
{
    public string Id { get; set; } = "";
    public int Number { get; set; }
    public string CreatedAt { get; set; } = "";
    public string Folder { get; set; } = "";
    public List<ClipDto> Clips { get; set; } = [];
    public bool Starred { get; set; }
    public string Comment { get; set; } = "";
    public string Club { get; set; } = "";
    public List<string> Tags { get; set; } = [];
    public TimingDto Timing { get; set; } = new();
    public float Confidence { get; set; }
    public bool ImpactConfirmed { get; set; }
    public string Source { get; set; } = "pose";
    public List<ShapeDto> Annotations { get; set; } = [];
    public JsonElement? Extra { get; set; }
}

public sealed class CameraDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Kind { get; set; } = "local";
    public string Position { get; set; } = "other";
}

public sealed class SessionDto
{
    public int SchemaVersion { get; set; }
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string StartedAt { get; set; } = "";
    public string? EndedAt { get; set; }
    public List<CameraDto> Cameras { get; set; } = [];
    public List<ShapeDto> PersistentAnnotations { get; set; } = [];
    public List<ShotDto> Shots { get; set; } = [];
}

public sealed class SessionSummaryDto
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string StartedAt { get; set; } = "";
    public string EndedAt { get; set; } = "";
    public string Folder { get; set; } = "";
    public int ShotCount { get; set; }
    public int StarredCount { get; set; }

    public string Subtitle => $"{ShotCount} shots · {StarredCount} starred";
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        // Core writes enum names in lower camel case ("line", "freehand").
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options)
        ?? throw new JsonException($"empty {typeof(T).Name}");
}
