using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SwingLoop.Capture;

namespace SwingLoop.Analysis;

/// <summary>
/// Single-person pose with MoveNet (SinglePose Lightning or Thunder) on ONNX
/// Runtime, GPU-accelerated through DirectML when available. Output is the
/// 17 COCO keypoints the core detector expects, normalized to the (rotated)
/// view the golfer is seen in.
///
/// Get the model with tools/export_movenet.py; it is looked up at
/// Models/movenet.onnx next to the executable.
/// </summary>
public sealed class PoseEstimator : IDisposable
{
    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly bool _intInput;
    private readonly int _size;
    private readonly int[] _intBuffer;
    private readonly float[] _floatBuffer;

    public bool UsesGpu { get; }

    private PoseEstimator(InferenceSession session, bool gpu)
    {
        _session = session;
        UsesGpu = gpu;
        var input = session.InputMetadata.First();
        _inputName = input.Key;
        _intInput = input.Value.ElementType == typeof(int);
        // NHWC: [1, size, size, 3]. Dynamic dims (-1) fall back to Lightning's 192.
        int dim = input.Value.Dimensions.Length >= 3 ? input.Value.Dimensions[1] : -1;
        _size = dim > 0 ? dim : 192;
        _intBuffer = _intInput ? new int[_size * _size * 3] : [];
        _floatBuffer = _intInput ? [] : new float[_size * _size * 3];
    }

    public static string DefaultModelPath => Path.Combine(AppContext.BaseDirectory, "Models", "movenet.onnx");

    public static PoseEstimator? TryCreate(string modelPath, out string status)
    {
        if (!File.Exists(modelPath))
        {
            status = "Pose model not installed: using sound + motion detection.";
            return null;
        }
        try
        {
            var options = new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL };
            bool gpu = true;
            try
            {
                options.AppendExecutionProvider_DML(0);
            }
            catch (Exception)
            {
                gpu = false;
            }
            var session = new InferenceSession(modelPath, options);
            status = gpu ? "Pose detection on GPU." : "Pose detection on CPU.";
            return new PoseEstimator(session, gpu);
        }
        catch (Exception ex)
        {
            status = $"Pose model failed to load ({ex.Message}); using sound + motion detection.";
            return null;
        }
    }

    /// <summary>
    /// Runs pose on an NV12 frame viewed with a 0/90/180/270 rotation.
    /// Returns 51 floats (x, y, score) × 17 in the rotated view's normalized space.
    /// </summary>
    public float[] Estimate(VideoFrame frame, int rotationDeg)
    {
        int rot = ((rotationDeg % 360) + 360) % 360 / 90;
        int w = frame.Width, h = frame.Height;
        // Size of the upright view the golfer is seen in.
        int vw = rot % 2 == 0 ? w : h, vh = rot % 2 == 0 ? h : w;

        // Letterbox the view into size x size so proportions are preserved.
        float scale = (float)_size / Math.Max(vw, vh);
        float padX = (_size - vw * scale) / 2, padY = (_size - vh * scale) / 2;
        FillInput(frame, rot, vw, vh, scale, padX, padY);

        var dims = new[] { 1, _size, _size, 3 };
        NamedOnnxValue input = _intInput
            ? NamedOnnxValue.CreateFromTensor(_inputName, new DenseTensor<int>(_intBuffer, dims))
            : NamedOnnxValue.CreateFromTensor(_inputName, new DenseTensor<float>(_floatBuffer, dims));

        using var results = _session.Run([input]);
        var output = results.First().AsTensor<float>().ToArray();  // [1,1,17,3] as (y, x, score)

        var kp = new float[51];
        for (int i = 0; i < 17; i++)
        {
            float y = output[i * 3], x = output[i * 3 + 1], score = output[i * 3 + 2];
            kp[i * 3] = (x * _size - padX) / scale / vw;
            kp[i * 3 + 1] = (y * _size - padY) / scale / vh;
            kp[i * 3 + 2] = score;
        }
        return kp;
    }

    private void FillInput(VideoFrame frame, int rot, int vw, int vh, float scale, float padX, float padY)
    {
        int w = frame.Width, h = frame.Height;
        byte[] d = frame.Data;
        int uvBase = w * h;
        int o = 0;
        for (int oy = 0; oy < _size; oy++)
        {
            float vy = (oy + 0.5f - padY) / scale;
            for (int ox = 0; ox < _size; ox++, o += 3)
            {
                float vx = (ox + 0.5f - padX) / scale;
                if (vx < 0 || vy < 0 || vx >= vw || vy >= vh)
                {
                    Write(o, 0, 0, 0);
                    continue;
                }
                // View -> source pixel for the camera's mounting rotation (clockwise).
                int ix = (int)vx, iy = (int)vy, sx, sy;
                switch (rot)
                {
                    case 1: sx = iy; sy = h - 1 - ix; break;
                    case 2: sx = w - 1 - ix; sy = h - 1 - iy; break;
                    case 3: sx = w - 1 - iy; sy = ix; break;
                    default: sx = ix; sy = iy; break;
                }
                int yv = d[sy * w + sx] - 16;
                int uvi = uvBase + (sy >> 1) * w + (sx & ~1);
                int u = d[uvi] - 128, v = d[uvi + 1] - 128;
                // BT.601 limited range -> RGB
                int c = 298 * yv + 128;
                Write(o, Clamp((c + 409 * v) >> 8), Clamp((c - 100 * u - 208 * v) >> 8), Clamp((c + 516 * u) >> 8));
            }
        }
    }

    private void Write(int o, int r, int g, int b)
    {
        if (_intInput)
        {
            _intBuffer[o] = r; _intBuffer[o + 1] = g; _intBuffer[o + 2] = b;
        }
        else
        {
            _floatBuffer[o] = r; _floatBuffer[o + 1] = g; _floatBuffer[o + 2] = b;
        }
    }

    private static int Clamp(int v) => v < 0 ? 0 : v > 255 ? 255 : v;

    public void Dispose() => _session.Dispose();
}
