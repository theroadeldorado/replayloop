#pragma once

#include <array>
#include <cstdint>

namespace swingcore {

// All timestamps are microseconds on a single monotonic clock shared by every
// camera and microphone in a session (QPC on Windows, mach_continuous_time on
// iOS, SystemClock.elapsedRealtimeNanos on Android; remote phones are mapped
// onto the host clock by the sync protocol).
using TimestampUs = int64_t;

// COCO-17 keypoint order. Platform pose engines (ONNX MoveNet, Apple Vision,
// ML Kit) are mapped onto this order before reaching the core.
enum class Joint : int {
    Nose = 0, LeftEye, RightEye, LeftEar, RightEar,
    LeftShoulder, RightShoulder, LeftElbow, RightElbow, LeftWrist, RightWrist,
    LeftHip, RightHip, LeftKnee, RightKnee, LeftAnkle, RightAnkle,
    Count
};

constexpr int kJointCount = static_cast<int>(Joint::Count);

struct Keypoint {
    float x = 0, y = 0;  // normalized image coordinates, origin top-left, y down
    float score = 0;     // 0..1 confidence
};

struct Pose {
    std::array<Keypoint, kJointCount> kp{};
    const Keypoint& operator[](Joint j) const { return kp[static_cast<int>(j)]; }
    Keypoint& operator[](Joint j) { return kp[static_cast<int>(j)]; }
};

struct PointF {
    float x = 0, y = 0;
};

struct RectF {
    float x = 0, y = 0, w = 0, h = 0;
};

}  // namespace swingcore
