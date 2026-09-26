#include "swingcore/signals.h"

#include <algorithm>
#include <cmath>
#include <cstdlib>

namespace swingcore {

namespace {
constexpr float kPi = 3.14159265358979f;
float toDb(double meanSquare) { return 10.0f * std::log10(static_cast<float>(meanSquare) + 1e-12f); }
}  // namespace

AudioImpactDetector::AudioImpactDetector(AudioImpactConfig cfg) : cfg_(cfg) {}

void AudioImpactDetector::reset() {
    sampleRate_ = 0;
    hpPrevIn_ = hpPrevOut_ = 0;
    blockSum_ = 0;
    blockCount_ = 0;
    recentDb_.clear();
    floorInit_ = false;
    lastOnsetUs_ = -1;
    out_.clear();
}

void AudioImpactDetector::process(TimestampUs firstSampleUs, const float* samples, size_t count, int sampleRate) {
    if (sampleRate <= 0 || !samples) return;
    if (sampleRate != sampleRate_) {
        reset();
        sampleRate_ = sampleRate;
        float rc = 1.0f / (2.0f * kPi * cfg_.highpassHz);
        float dt = 1.0f / static_cast<float>(sampleRate);
        hpAlpha_ = rc / (rc + dt);
        blockSize_ = std::max(1, sampleRate / 1000);  // 1 ms blocks
    }
    const double usPerSample = 1e6 / sampleRate;
    for (size_t i = 0; i < count; ++i) {
        float x = samples[i];
        float y = hpAlpha_ * (hpPrevOut_ + x - hpPrevIn_);
        hpPrevIn_ = x;
        hpPrevOut_ = y;
        if (blockCount_ == 0) blockStartUs_ = firstSampleUs + static_cast<TimestampUs>(static_cast<double>(i) * usPerSample);
        blockSum_ += static_cast<double>(y) * y;
        if (++blockCount_ == blockSize_) finishBlock(blockStartUs_, sampleRate);
    }
}

void AudioImpactDetector::finishBlock(TimestampUs blockUs, int sampleRate) {
    float level = toDb(blockSum_ / blockCount_);
    blockSum_ = 0;
    blockCount_ = 0;
    lastLevelDb_ = level;

    if (!floorInit_) {
        floorDb_ = level;
        floorInit_ = true;
    }

    const size_t attackBlocks = std::max<size_t>(2, static_cast<size_t>(cfg_.attackWindowMs));
    float before = level;
    if (recentDb_.size() >= attackBlocks) {
        // Quietest level a few ms ago: the transient must jump out of it.
        before = *std::min_element(recentDb_.end() - static_cast<long>(attackBlocks), recentDb_.end());
    }

    bool onset = level >= floorDb_ + cfg_.thresholdDb && level >= cfg_.minLevelDbfs &&
                 level - before >= cfg_.attackDb &&
                 (lastOnsetUs_ < 0 || blockUs - lastOnsetUs_ >= cfg_.refractoryUs);
    if (onset) {
        lastOnsetUs_ = blockUs;
        out_.emplace_back(blockUs, level - floorDb_);
    }

    // Adapt the noise floor, but not to the impacts themselves. Falls fast, rises slowly.
    const float blockSeconds = static_cast<float>(blockSize_) / static_cast<float>(sampleRate);
    const float k = std::min(1.0f, blockSeconds / cfg_.floorAdaptSeconds);
    if (level < floorDb_) floorDb_ += (level - floorDb_) * std::min(1.0f, k * 8.0f);
    else if (level < floorDb_ + 10.0f) floorDb_ += (level - floorDb_) * k;

    recentDb_.push_back(level);
    if (recentDb_.size() > 64) recentDb_.pop_front();
}

bool AudioImpactDetector::poll(TimestampUs& impactUs, float& strengthDb) {
    if (out_.empty()) return false;
    impactUs = out_.front().first;
    strengthDb = out_.front().second;
    out_.pop_front();
    return true;
}

MotionEnergy::MotionEnergy(int gridWidth, int gridHeight, int pixelThreshold)
    : gw_(gridWidth), gh_(gridHeight), threshold_(pixelThreshold) {}

float MotionEnergy::process(const uint8_t* luma, int width, int height, int stride, RectF roi) {
    if (!luma || width <= 0 || height <= 0) return 0;
    roi.x = std::clamp(roi.x, 0.0f, 1.0f);
    roi.y = std::clamp(roi.y, 0.0f, 1.0f);
    roi.w = std::clamp(roi.w, 0.01f, 1.0f - roi.x);
    roi.h = std::clamp(roi.h, 0.01f, 1.0f - roi.y);

    cur_.resize(static_cast<size_t>(gw_) * gh_);
    const float fw = static_cast<float>(width), fh = static_cast<float>(height);
    const float fgw = static_cast<float>(gw_), fgh = static_cast<float>(gh_);
    for (int gy = 0; gy < gh_; ++gy) {
        int sy = static_cast<int>((roi.y + roi.h * (static_cast<float>(gy) + 0.5f) / fgh) * fh);
        sy = std::min(sy, height - 1);
        const uint8_t* row = luma + static_cast<ptrdiff_t>(sy) * stride;
        for (int gx = 0; gx < gw_; ++gx) {
            int sx = std::min(static_cast<int>((roi.x + roi.w * (static_cast<float>(gx) + 0.5f) / fgw) * fw), width - 1);
            cur_[static_cast<size_t>(gy) * gw_ + gx] = row[sx];
        }
    }

    float energy = 0;
    if (prev_.size() == cur_.size()) {
        size_t changed = 0;
        for (size_t i = 0; i < cur_.size(); ++i)
            if (std::abs(static_cast<int>(cur_[i]) - static_cast<int>(prev_[i])) > threshold_) ++changed;
        energy = static_cast<float>(changed) / static_cast<float>(cur_.size());
    }
    prev_.swap(cur_);
    return energy;
}

}  // namespace swingcore
