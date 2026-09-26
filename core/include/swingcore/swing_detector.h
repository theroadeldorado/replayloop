// Decides when a real golf swing happened.
//
// Signals, in order of trust:
//   1. Pose  - the hand path relative to the golfer's own torso walks through
//              address -> takeaway -> top -> downswing -> impact -> finish.
//              Waggles never reach the top, so they are rejected here.
//   2. Audio - a sharp club/ball impact transient. A full pose swing with no
//              impact sound is a practice swing.
//   3. Motion - frame-difference energy. Confirms audio-only captures so a
//              dropped club or a neighbour's shot does not trigger a clip.
//
// The detector is pure and single-threaded: feed it samples in timestamp
// order (per stream) and drain events with poll().
#pragma once

#include <deque>
#include <optional>

#include "swingcore/types.h"

namespace swingcore {

enum class SwingPhase : int {
    NoGolfer = 0,
    Idle = 1,       // golfer in view, not set up
    Address = 2,    // set up over the ball, hands still
    Backswing = 3,
    Downswing = 4,
    FollowThrough = 5,
    Cooldown = 6,
};

enum class SwingEventType : int {
    GolferEntered = 1,
    GolferLeft = 2,
    AddressDetected = 3,
    SwingStarted = 4,
    WaggleIgnored = 5,
    PracticeSwing = 6,  // full swing, but no impact heard
    SwingCaptured = 7,  // a real shot: extract [clipStartUs, clipEndUs]
    SwingAborted = 8,
};

enum class CaptureSource : int { Pose = 0, AudioMotion = 1, Manual = 2 };

struct SwingTiming {
    TimestampUs addressUs = 0;
    TimestampUs takeawayUs = 0;
    TimestampUs topUs = 0;
    TimestampUs impactUs = 0;
    TimestampUs finishUs = 0;

    // Backswing duration / downswing duration. Tour average is ~3.0.
    float tempoRatio() const;
};

struct SwingEvent {
    SwingEventType type{};
    TimestampUs timestampUs = 0;
    SwingTiming timing;
    TimestampUs clipStartUs = 0;
    TimestampUs clipEndUs = 0;
    float confidence = 0;
    bool impactConfirmed = false;
    CaptureSource source = CaptureSource::Pose;
};

struct SwingDetectorConfig {
    // Which signals the host can provide.
    bool usePose = true;
    bool useAudio = true;
    bool useMotion = true;
    // When audio is available, a full swing without an impact sound is a
    // practice swing and is not captured.
    bool requireImpactSound = true;

    // Width / height of the analysed frames, so torso lengths are measured in
    // consistent units whatever the camera orientation.
    float frameAspect = 16.0f / 9.0f;

    // Presence
    float minJointScore = 0.3f;
    float minTorsoFraction = 0.05f;  // torso length / frame height
    float maxTorsoFraction = 0.9f;
    TimestampUs presenceEnterUs = 300'000;
    TimestampUs presenceExitUs = 1'500'000;

    // Hand height is measured in torso lengths above the hip line:
    // 0 = hips, 1 = shoulders.
    float addressMaxHandHeight = 0.35f;
    float stillSpeed = 0.9f;  // torso lengths per second
    TimestampUs addressHoldUs = 350'000;
    float takeawayHeight = 0.5f;
    float topMinHeight = 0.85f;
    float topDropForDownswing = 0.25f;
    float impactBand = 0.35f;  // hands within this of address height
    float finishMinHeight = 0.6f;
    float waggleMinTravel = 0.12f;

    TimestampUs maxBackswingUs = 3'000'000;
    TimestampUs maxDownswingUs = 700'000;
    TimestampUs maxFollowThroughUs = 1'500'000;
    TimestampUs maxHandDropoutUs = 450'000;  // hands blur out in the downswing
    TimestampUs cooldownUs = 800'000;

    // Audio/pose association
    TimestampUs impactWindowUs = 300'000;

    // Motion confirmation of audio-only captures
    float motionThreshold = 0.015f;  // fraction of changed pixels
    TimestampUs motionBeforeImpactUs = 600'000;
    TimestampUs motionAfterImpactUs = 250'000;

    // Clip window around impact
    TimestampUs preRollUs = 2'000'000;
    TimestampUs postRollUs = 1'500'000;
};

class SwingDetector {
public:
    explicit SwingDetector(SwingDetectorConfig cfg = {});

    const SwingDetectorConfig& config() const { return cfg_; }
    void setConfig(const SwingDetectorConfig& cfg) { cfg_ = cfg; }

    // Pose for one analysed frame. Pass nullptr when the pose engine found no person.
    void pushPose(TimestampUs ts, const Pose* pose);
    // A detected impact transient (from AudioImpactDetector).
    void pushImpact(TimestampUs ts, float strengthDb);
    // Motion energy for one frame (from MotionEnergy).
    void pushMotion(TimestampUs ts, float energy);
    // Advance time without new data (e.g. from a UI timer) so pending
    // decisions resolve even when a stream stalls.
    void tick(TimestampUs now);
    // "Save that one" button / hotkey: capture the window ending now.
    void triggerManual(TimestampUs now);

    bool poll(SwingEvent& out);
    SwingPhase phase() const { return phase_; }
    bool golferPresent() const { return present_; }
    void reset();

private:
    struct HandSample {
        TimestampUs ts;
        float height;  // torso lengths above hips
        float x, y;    // normalized image coords
    };
    struct PendingSwing {
        SwingTiming timing;
        float confidence;
        TimestampUs decideAtUs;
    };
    struct PendingImpact {
        TimestampUs ts;
        float strengthDb;
        bool claimed;
    };

    void emit(SwingEventType type, TimestampUs ts);
    void emitCapture(const SwingTiming& t, float confidence, bool impactConfirmed, CaptureSource src, TimestampUs ts);
    void setPhase(SwingPhase p, TimestampUs ts);
    void updatePresence(TimestampUs ts, bool visible);
    void stepSwing(const HandSample& h);
    void handleDropout(TimestampUs ts);
    void completeSwing(TimestampUs ts, bool sawFinish);
    void resolvePending(TimestampUs now);
    PendingImpact* findImpactNear(TimestampUs ts, TimestampUs window);
    float peakMotion(TimestampUs from, TimestampUs to) const;
    void advanceClock(TimestampUs ts);

    SwingDetectorConfig cfg_;
    std::deque<SwingEvent> events_;
    SwingPhase phase_ = SwingPhase::NoGolfer;
    TimestampUs phaseSinceUs_ = 0;
    TimestampUs nowUs_ = 0;

    // presence
    bool present_ = false;
    TimestampUs visibleSinceUs_ = -1;
    TimestampUs lastVisibleUs_ = -1;

    // hand tracking
    std::optional<HandSample> lastHand_;
    float smoothedSpeed_ = 0;
    TimestampUs stillSinceUs_ = -1;
    TimestampUs lastStillUs_ = -1;
    TimestampUs lastHandSeenUs_ = -1;
    TimestampUs dropoutStartUs_ = -1;

    // swing in progress
    SwingTiming cur_;
    float addressHeight_ = 0;
    float addressX_ = 0, addressY_ = 0;
    float maxTravel_ = 0;
    float peakHeight_ = 0;
    float troughHeight_ = 0;
    TimestampUs troughUs_ = 0;
    bool reachedTop_ = false;
    bool handsDropped_ = false;
    bool impactSeen_ = false;
    bool estimatedImpact_ = false;

    std::deque<PendingSwing> pendingSwings_;
    std::deque<PendingImpact> impacts_;
    std::deque<std::pair<TimestampUs, float>> motion_;
    TimestampUs lastCaptureImpactUs_ = -1;
};

}  // namespace swingcore
