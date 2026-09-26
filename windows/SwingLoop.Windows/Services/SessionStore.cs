using SwingLoop.Interop;
using SwingLoop.Models;

namespace SwingLoop.Services;

/// <summary>
/// One session folder, backed by the core's atomic session.json writer.
/// The live session and any past session opened for review are both stores,
/// so starring, comments and drawings work the same on either.
/// </summary>
public sealed class SessionStore : IDisposable
{
    private IntPtr _handle;

    private SessionStore(IntPtr handle)
    {
        _handle = handle;
        Folder = Native.Take(Native.sc_session_folder(handle)) ?? "";
        Reload();
    }

    public string Folder { get; }
    public SessionDto Data { get; private set; } = new();
    public string Name => Data.Name;
    public bool IsEnded => !string.IsNullOrEmpty(Data.EndedAt);

    public static SessionStore Create(string root, string name = "")
    {
        var h = Native.sc_session_create(root, name);
        if (h == IntPtr.Zero) throw new SwingCoreException($"Could not create session: {Native.LastError}");
        return new SessionStore(h);
    }

    public static SessionStore Open(string folder)
    {
        var h = Native.sc_session_open(folder);
        if (h == IntPtr.Zero) throw new SwingCoreException($"Could not open session: {Native.LastError}");
        return new SessionStore(h);
    }

    public static List<SessionSummaryDto> List(string root)
    {
        var json = Native.Take(Native.sc_sessions_list(root));
        return json is null ? [] : Json.Deserialize<List<SessionSummaryDto>>(json);
    }

    private void Reload()
    {
        var json = Native.Take(Native.sc_session_json(_handle)) ?? throw new SwingCoreException(Native.LastError);
        Data = Json.Deserialize<SessionDto>(json);
    }

    public string ShotFolder(ShotDto shot) => Path.Combine(Folder, shot.Folder.Replace('/', Path.DirectorySeparatorChar));
    public string ClipPath(ShotDto shot, ClipDto clip) => Path.Combine(ShotFolder(shot), clip.File);
    public string ThumbnailPath(ShotDto shot, ClipDto clip) => Path.Combine(ShotFolder(shot), clip.Thumbnail);

    public ShotDto AddShot(ShotDto shot)
    {
        var json = Native.Take(Native.sc_session_add_shot(_handle, Json.Serialize(shot)))
            ?? throw new SwingCoreException($"Could not save shot: {Native.LastError}");
        var stored = Json.Deserialize<ShotDto>(json);
        lock (this) Data.Shots.Add(stored);
        return stored;
    }

    public void UpdateShot(ShotDto shot)
    {
        if (Native.sc_session_update_shot(_handle, Json.Serialize(shot)) == 0)
            throw new SwingCoreException($"Could not update shot: {Native.LastError}");
        lock (this)
        {
            int i = Data.Shots.FindIndex(s => s.Id == shot.Id);
            if (i >= 0) Data.Shots[i] = shot;
        }
    }

    public void RemoveShot(ShotDto shot, bool deleteFiles = true)
    {
        Native.sc_session_remove_shot(_handle, shot.Id, deleteFiles ? 1 : 0);
        lock (this) Data.Shots.RemoveAll(s => s.Id == shot.Id);
    }

    public void SetPersistentAnnotations(List<ShapeDto> shapes)
    {
        Native.sc_session_set_persistent_annotations(_handle, Json.Serialize(shapes));
        Data.PersistentAnnotations = shapes;
    }

    public void SetCameras(List<CameraDto> cameras)
    {
        Native.sc_session_set_cameras(_handle, Json.Serialize(cameras));
        Data.Cameras = cameras;
    }

    public void Rename(string name)
    {
        Native.sc_session_rename(_handle, name);
        Data.Name = name;
    }

    public void End()
    {
        Native.sc_session_end(_handle);
        Reload();
    }

    public void Dispose()
    {
        var h = Interlocked.Exchange(ref _handle, IntPtr.Zero);
        if (h != IntPtr.Zero) Native.sc_session_close(h);
    }
}
