// Low-level signal extractors that feed SwingDetector.
#pragma once

#include <cstddef>
#include <cstdint>
#include <deque>
#include <vector>

#include "swingcore/types.h"

namespace swingcore {

struct AudioImpactConfig {
    float highpassHz = 1500.0f;      // club/ball contact is a broadband click
    float thresholdDb = 18.0f;       // above the adaptive noise floor
    float minLevelDbfs = -42.0f;     // ignore quiet transients entirely
    float attackDb = 12.0f;          // rise needed within attackWindowMs
    float attackWindowMs = 6.0f;
    float floorAdaptSeconds = 0.6f;
    TimestampUs refractoryUs = 350'000;  // ball hitting the sim screen follows the strike
};

// Detects the sharp transient of a golf ball being struck.
class AudioImpactDetector {
public:
    explicit AudioImpactDetector(AudioImpactConfig cfg = {});

    // Mono float samples in [-1, 1]. firstSampleUs is the capture time of samples[0].
    void process(TimestampUs firstSampleUs, const float* samples, size_t count, int sampleRate);
    bool poll(TimestampUs& impactUs, float& strengthDb);

    float noiseFloorDb() const { return floorDb_; }
    float levelDb() const { return lastLevelDb_; }  // for a UI input meter
    void reset();

private:
    void finishBlock(TimestampUs blockUs, int sampleRate);

    AudioImpactConfig cfg_;
    int sampleRate_ = 0;
    float hpPrevIn_ = 0, hpPrevOut_ = 0, hpAlpha_ = 0;
    double blockSum_ = 0;
    int blockCount_ = 0;
    int blockSize_ = 48;
    TimestampUs blockStartUs_ = 0;
    std::deque<float> recentDb_;
    float floorDb_ = 0;
    bool floorInit_ = false;
    float lastLevelDb_ = -120.0f;
    TimestampUs lastOnsetUs_ = -1;
    std::deque<std::pair<TimestampUs, float>> out_;
};

// Fraction of pixels (0..1) that changed noticeably since the previous frame.
class MotionEnergy {
public:
    MotionEnergy(int gridWidth = 160, int gridHeight = 90, int pixelThreshold = 14);

    // 8-bit luma (e.g. the Y plane of NV12). roi is normalized.
    float process(const uint8_t* luma, int width, int height, int stride, RectF roi = {0, 0, 1, 1});
    void reset() { prev_.clear(); }

private:
    int gw_, gh_, threshold_;
    std::vector<uint8_t> prev_, cur_;
};

}  // namespace swingcore
