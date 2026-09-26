#include "swingcore/swing_detector.h"

#include <algorithm>
#include <cmath>

namespace swingcore {

namespace {

bool usable(const Keypoint& k, float minScore) { return k.score >= minScore; }

PointF mid(const Keypoint& a, const Keypoint& b) { return {(a.x + b.x) * 0.5f, (a.y + b.y) * 0.5f}; }

// The swing phases during which the hands are moving fast and a capture may be
// about to claim a recent impact sound.
bool swingInFlight(SwingPhase p) {
    return p == SwingPhase::Backswing || p == SwingPhase::Downswing || p == SwingPhase::FollowThrough;
}

}  // namespace

float SwingTiming::tempoRatio() const {
    if (takeawayUs <= 0 || topUs <= takeawayUs || impactUs <= topUs) return 0.0f;
    return static_cast<float>(topUs - takeawayUs) / static_cast<float>(impactUs - topUs);
}

SwingDetector::SwingDetector(SwingDetectorConfig cfg) : cfg_(cfg) {}

void SwingDetector::reset() {
    events_.clear();
    pendingSwings_.clear();
    impacts_.clear();
    motion_.clear();
    phase_ = SwingPhase::NoGolfer;
    phaseSinceUs_ = 0;
    present_ = false;
    visibleSinceUs_ = lastVisibleUs_ = -1;
    lastHand_.reset();
    smoothedSpeed_ = 0;
    stillSinceUs_ = lastStillUs_ = lastHandSeenUs_ = -1;
    cur_ = {};
    reachedTop_ = handsDropped_ = impactSeen_ = false;
    lastCaptureImpactUs_ = -1;
}

bool SwingDetector::poll(SwingEvent& out) {
    if (events_.empty()) return false;
    out = events_.front();
    events_.pop_front();
    return true;
}

void SwingDetector::emit(SwingEventType type, TimestampUs ts) {
    SwingEvent e;
    e.type = type;
    e.timestampUs = ts;
    e.timing = cur_;
    events_.push_back(e);
}

void SwingDetector::emitCapture(const SwingTiming& t, float confidence, bool impactConfirmed, CaptureSource src,
                                TimestampUs ts) {
    SwingEvent e;
    e.type = SwingEventType::SwingCaptured;
    e.timestampUs = ts;
    e.timing = t;
    e.confidence = std::clamp(confidence, 0.0f, 1.0f);
    e.impactConfirmed = impactConfirmed;
    e.source = src;
    e.clipStartUs = t.impactUs - cfg_.preRollUs;
    if (t.takeawayUs > 0) e.clipStartUs = std::min(e.clipStartUs, t.takeawayUs - 300'000);
    e.clipEndUs = t.impactUs + cfg_.postRollUs;
    if (t.finishUs > 0) e.clipEndUs = std::max(e.clipEndUs, t.finishUs + 300'000);
    lastCaptureImpactUs_ = t.impactUs;
    events_.push_back(e);
}

void SwingDetector::setPhase(SwingPhase p, TimestampUs ts) {
    phase_ = p;
    phaseSinceUs_ = ts;
}

void SwingDetector::advanceClock(TimestampUs ts) {
    if (ts > nowUs_) nowUs_ = ts;
    resolvePending(nowUs_);
}

void SwingDetector::tick(TimestampUs now) { advanceClock(now); }

void SwingDetector::triggerManual(TimestampUs now) {
    advanceClock(now);
    SwingTiming t;
    t.impactUs = now - cfg_.postRollUs;
    emitCapture(t, 1.0f, false, CaptureSource::Manual, now);
}

void SwingDetector::pushImpact(TimestampUs ts, float strengthDb) {
    if (cfg_.useAudio) impacts_.push_back({ts, strengthDb, false});
    advanceClock(ts);
}

void SwingDetector::pushMotion(TimestampUs ts, float energy) {
    if (cfg_.useMotion) motion_.emplace_back(ts, energy);
    advanceClock(ts);
}

void SwingDetector::pushPose(TimestampUs ts, const Pose* pose) {
    advanceClock(ts);
    if (!cfg_.usePose) return;

    bool visible = false;
    float torso = 0;
    PointF hip{};
    if (pose) {
        const Pose& p = *pose;
        const float s = cfg_.minJointScore;
        bool shoulders = usable(p[Joint::LeftShoulder], s) || usable(p[Joint::RightShoulder], s);
        bool hips = usable(p[Joint::LeftHip], s) || usable(p[Joint::RightHip], s);
        if (shoulders && hips) {
            auto pick = [&](Joint a, Joint b) {
                const Keypoint& ka = p[a];
                const Keypoint& kb = p[b];
                if (usable(ka, s) && usable(kb, s)) return mid(ka, kb);
                const Keypoint& k = usable(ka, s) ? ka : kb;
                return PointF{k.x, k.y};
            };
            PointF shoulder = pick(Joint::LeftShoulder, Joint::RightShoulder);
            hip = pick(Joint::LeftHip, Joint::RightHip);
            float dx = (shoulder.x - hip.x) * cfg_.frameAspect;
            float dy = shoulder.y - hip.y;
            torso = std::sqrt(dx * dx + dy * dy);
            visible = torso >= cfg_.minTorsoFraction && torso <= cfg_.maxTorsoFraction;
        }
    }

    updatePresence(ts, visible);
    if (!present_) return;
    if (!visible) {
        handleDropout(ts);
        return;
    }

    const Pose& p = *pose;
    const Keypoint& lw = p[Joint::LeftWrist];
    const Keypoint& rw = p[Joint::RightWrist];
    bool l = usable(lw, cfg_.minJointScore);
    bool r = usable(rw, cfg_.minJointScore);
    if (!l && !r) {
        handleDropout(ts);
        return;
    }
    PointF hand = (l && r) ? mid(lw, rw) : (l ? PointF{lw.x, lw.y} : PointF{rw.x, rw.y});

    HandSample h;
    h.ts = ts;
    h.height = (hip.y - hand.y) / torso;
    h.x = hand.x * cfg_.frameAspect / torso;
    h.y = hand.y / torso;

    if (lastHand_ && ts > lastHand_->ts) {
        float dt = static_cast<float>(ts - lastHand_->ts) / 1e6f;
        float speed = std::hypot(h.x - lastHand_->x, h.y - lastHand_->y) / dt;
        smoothedSpeed_ = 0.5f * smoothedSpeed_ + 0.5f * speed;
    }

    bool wasDropped = handsDropped_ && (ts - dropoutStartUs_) >= 150'000;
    handsDropped_ = false;
    lastHand_ = h;
    lastHandSeenUs_ = ts;

    // Hands vanished (motion blur) after the top and came back already in the
    // finish: the downswing happened unseen. Count it, estimating impact.
    if (wasDropped && reachedTop_ && (phase_ == SwingPhase::Backswing || phase_ == SwingPhase::Downswing) &&
        h.height >= cfg_.finishMinHeight) {
        if (cur_.impactUs == 0) cur_.impactUs = cur_.topUs + (ts - cur_.topUs) * 45 / 100;
        cur_.finishUs = ts;
        estimatedImpact_ = true;
        completeSwing(ts, true);
        return;
    }

    stepSwing(h);
}

void SwingDetector::updatePresence(TimestampUs ts, bool visible) {
    if (visible) {
        lastVisibleUs_ = ts;
        if (visibleSinceUs_ < 0) visibleSinceUs_ = ts;
        if (!present_ && ts - visibleSinceUs_ >= cfg_.presenceEnterUs) {
            present_ = true;
            setPhase(SwingPhase::Idle, ts);
            stillSinceUs_ = -1;
            emit(SwingEventType::GolferEntered, ts);
        }
        return;
    }
    visibleSinceUs_ = -1;
    if (present_ && lastVisibleUs_ >= 0 && ts - lastVisibleUs_ >= cfg_.presenceExitUs) {
        present_ = false;
        if (swingInFlight(phase_) && reachedTop_) completeSwing(ts, false);
        cur_ = {};
        reachedTop_ = false;
        lastHand_.reset();
        setPhase(SwingPhase::NoGolfer, ts);
        emit(SwingEventType::GolferLeft, ts);
    }
}

void SwingDetector::handleDropout(TimestampUs ts) {
    if (!handsDropped_) {
        handsDropped_ = true;
        dropoutStartUs_ = ts;
    }
    if (!swingInFlight(phase_)) return;
    TimestampUs since = lastHandSeenUs_ >= 0 ? ts - lastHandSeenUs_ : 0;
    if (phase_ == SwingPhase::Backswing) {
        if (ts - cur_.takeawayUs > cfg_.maxBackswingUs) {
            emit(SwingEventType::SwingAborted, ts);
            setPhase(SwingPhase::Idle, ts);
        }
        return;
    }
    // Downswing / follow-through with hands lost for too long: close it out.
    if (since > cfg_.maxHandDropoutUs + cfg_.maxFollowThroughUs) {
        if (cur_.impactUs == 0) {
            cur_.impactUs = cur_.topUs + 250'000;
            estimatedImpact_ = true;
        }
        completeSwing(ts, false);
    }
}

void SwingDetector::stepSwing(const HandSample& h) {
    const TimestampUs ts = h.ts;
    const bool lowAndStill = smoothedSpeed_ < cfg_.stillSpeed && h.height < cfg_.addressMaxHandHeight;
    if (lowAndStill) {
        if (stillSinceUs_ < 0) stillSinceUs_ = ts;
        lastStillUs_ = ts;
    } else {
        stillSinceUs_ = -1;
    }

    switch (phase_) {
        case SwingPhase::NoGolfer:
            break;

        case SwingPhase::Idle:
            if (stillSinceUs_ >= 0 && ts - stillSinceUs_ >= cfg_.addressHoldUs) {
                cur_ = {};
                cur_.addressUs = ts;
                addressHeight_ = h.height;
                addressX_ = h.x;
                addressY_ = h.y;
                maxTravel_ = 0;
                setPhase(SwingPhase::Address, ts);
                emit(SwingEventType::AddressDetected, ts);
            }
            break;

        case SwingPhase::Address: {
            float travel = std::hypot(h.x - addressX_, h.y - addressY_);
            maxTravel_ = std::max(maxTravel_, travel);
            if (h.height >= cfg_.takeawayHeight) {
                cur_.takeawayUs = lastStillUs_ >= 0 ? lastStillUs_ : ts;
                cur_.topUs = ts;
                peakHeight_ = h.height;
                reachedTop_ = false;
                estimatedImpact_ = false;
                setPhase(SwingPhase::Backswing, ts);
                emit(SwingEventType::SwingStarted, ts);
            } else if (lowAndStill) {
                if (maxTravel_ >= cfg_.waggleMinTravel) emit(SwingEventType::WaggleIgnored, ts);
                maxTravel_ = 0;
                addressHeight_ = h.height;
                addressX_ = h.x;
                addressY_ = h.y;
            } else if (lastStillUs_ >= 0 && ts - lastStillUs_ > 4'000'000) {
                // Moved away from the ball without swinging.
                setPhase(SwingPhase::Idle, ts);
            }
            break;
        }

        case SwingPhase::Backswing:
            if (h.height > peakHeight_) {
                peakHeight_ = h.height;
                cur_.topUs = ts;
            }
            if (peakHeight_ >= cfg_.topMinHeight) reachedTop_ = true;
            if (reachedTop_ && h.height <= peakHeight_ - cfg_.topDropForDownswing) {
                troughHeight_ = h.height;
                troughUs_ = ts;
                impactSeen_ = h.height <= addressHeight_ + cfg_.impactBand;
                setPhase(SwingPhase::Downswing, ts);
            } else if (!reachedTop_ && h.height < cfg_.addressMaxHandHeight) {
                // Half swing / big waggle that never reached the top.
                emit(SwingEventType::WaggleIgnored, ts);
                setPhase(SwingPhase::Address, ts);
                maxTravel_ = 0;
            } else if (ts - cur_.takeawayUs > cfg_.maxBackswingUs) {
                emit(SwingEventType::SwingAborted, ts);
                setPhase(SwingPhase::Idle, ts);
            }
            break;

        case SwingPhase::Downswing:
            if (h.height < troughHeight_) {
                troughHeight_ = h.height;
                troughUs_ = ts;
            }
            if (h.height <= addressHeight_ + cfg_.impactBand) impactSeen_ = true;
            if (impactSeen_ && h.height > troughHeight_ + 0.2f) {
                cur_.impactUs = troughUs_;
                setPhase(SwingPhase::FollowThrough, ts);
                stepSwing(h);  // may already be in the finish
            } else if (ts - cur_.topUs > cfg_.maxDownswingUs) {
                if (impactSeen_) {
                    cur_.impactUs = troughUs_;
                    setPhase(SwingPhase::FollowThrough, ts);
                } else {
                    emit(SwingEventType::SwingAborted, ts);
                    setPhase(SwingPhase::Idle, ts);
                }
            }
            break;

        case SwingPhase::FollowThrough:
            if (h.height >= cfg_.finishMinHeight) {
                cur_.finishUs = ts;
                completeSwing(ts, true);
            } else if (ts - cur_.impactUs > cfg_.maxFollowThroughUs) {
                completeSwing(ts, false);
            }
            break;

        case SwingPhase::Cooldown:
            if (ts - phaseSinceUs_ >= cfg_.cooldownUs) {
                setPhase(SwingPhase::Idle, ts);
                stillSinceUs_ = lowAndStill ? ts : -1;
            }
            break;
    }
}

void SwingDetector::completeSwing(TimestampUs ts, bool sawFinish) {
    float confidence = sawFinish ? 0.85f : 0.65f;
    if (estimatedImpact_) confidence -= 0.15f;
    pendingSwings_.push_back({cur_, confidence, cur_.impactUs + cfg_.impactWindowUs});
    cur_ = {};
    reachedTop_ = false;
    impactSeen_ = false;
    estimatedImpact_ = false;
    setPhase(SwingPhase::Cooldown, ts);
    resolvePending(nowUs_);
}

SwingDetector::PendingImpact* SwingDetector::findImpactNear(TimestampUs ts, TimestampUs window) {
    PendingImpact* best = nullptr;
    TimestampUs bestDist = window + 1;
    for (auto& imp : impacts_) {
        if (imp.claimed) continue;
        TimestampUs d = std::llabs(imp.ts - ts);
        if (d <= window && d < bestDist) {
            best = &imp;
            bestDist = d;
        }
    }
    return best;
}

float SwingDetector::peakMotion(TimestampUs from, TimestampUs to) const {
    float peak = 0;
    for (const auto& [ts, e] : motion_)
        if (ts >= from && ts <= to) peak = std::max(peak, e);
    return peak;
}

void SwingDetector::resolvePending(TimestampUs now) {
    // 1. Pose swings waiting for their impact sound.
    while (!pendingSwings_.empty()) {
        PendingSwing& ps = pendingSwings_.front();
        if (cfg_.useAudio && now < ps.decideAtUs) break;
        SwingTiming t = ps.timing;
        PendingImpact* imp = cfg_.useAudio ? findImpactNear(t.impactUs, cfg_.impactWindowUs) : nullptr;
        if (imp) {
            imp->claimed = true;
            t.impactUs = imp->ts;  // audio is far more precise than pose
            emitCapture(t, ps.confidence + 0.15f, true, CaptureSource::Pose, now);
        } else if (cfg_.useAudio && cfg_.requireImpactSound) {
            SwingEvent e;
            e.type = SwingEventType::PracticeSwing;
            e.timestampUs = now;
            e.timing = t;
            e.confidence = ps.confidence;
            events_.push_back(e);
        } else {
            emitCapture(t, ps.confidence, false, CaptureSource::Pose, now);
        }
        pendingSwings_.pop_front();
    }

    // 2. Impact sounds no pose swing claimed (golfer half out of frame, pose
    //    model struggling, or pose disabled). Confirm with presence + motion.
    const TimestampUs settle = std::max(cfg_.motionAfterImpactUs, cfg_.impactWindowUs);
    for (auto& imp : impacts_) {
        if (imp.claimed) continue;
        if (now < imp.ts + settle) break;
        if (cfg_.usePose && (swingInFlight(phase_) || !pendingSwings_.empty()) && now < imp.ts + 3'000'000) break;
        imp.claimed = true;
        if (cfg_.usePose && !present_) continue;
        if (cfg_.useMotion &&
            peakMotion(imp.ts - cfg_.motionBeforeImpactUs, imp.ts + cfg_.motionAfterImpactUs) < cfg_.motionThreshold)
            continue;
        if (lastCaptureImpactUs_ >= 0 && imp.ts - lastCaptureImpactUs_ < 1'000'000) continue;
        SwingTiming t;
        t.impactUs = imp.ts;
        emitCapture(t, 0.6f, true, CaptureSource::AudioMotion, now);
    }

    while (!impacts_.empty() && impacts_.front().ts < now - 10'000'000) impacts_.pop_front();
    while (!motion_.empty() && motion_.front().first < now - 5'000'000) motion_.pop_front();
}

}  // namespace swingcore
