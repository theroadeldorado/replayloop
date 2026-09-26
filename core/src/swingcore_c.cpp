#define SWINGCORE_BUILD
#include "swingcore/swingcore_c.h"

#include <cstdlib>
#include <cstring>
#include <exception>
#include <memory>
#include <mutex>
#include <string>

#include "swingcore/annotations.h"
#include "swingcore/layout.h"
#include "swingcore/session.h"
#include "swingcore/signals.h"
#include "swingcore/swing_detector.h"

using namespace swingcore;
namespace fs = std::filesystem;

struct sc_detector {
    std::mutex mu;
    SwingDetector detector;
    AudioImpactDetector audio;
    MotionEnergy motion;
};

struct sc_session {
    std::mutex mu;
    Session session;
};

namespace {

thread_local std::string g_lastError;

char* dupString(const std::string& s) {
    char* p = static_cast<char*>(std::malloc(s.size() + 1));
    if (p) std::memcpy(p, s.c_str(), s.size() + 1);
    return p;
}

template <typename F>
auto guarded(F&& f, decltype(f()) onError) -> decltype(f()) {
    try {
        g_lastError.clear();
        return f();
    } catch (const std::exception& e) {
        g_lastError = e.what();
    } catch (...) {
        g_lastError = "unknown error";
    }
    return onError;
}

template <typename T>
void setIf(const json::Value& v, const char* key, T& field) {
    const json::Value& x = v[key];
    if constexpr (std::is_same_v<T, bool>) {
        if (x.isBool()) field = x.asBool();
    } else {
        if (x.isNumber()) field = static_cast<T>(x.asNumber());
    }
}

void applyConfig(const json::Value& v, SwingDetectorConfig& c) {
#define SC_FIELD(name) setIf(v, #name, c.name)
    SC_FIELD(usePose); SC_FIELD(useAudio); SC_FIELD(useMotion); SC_FIELD(requireImpactSound);
    SC_FIELD(frameAspect); SC_FIELD(minJointScore); SC_FIELD(minTorsoFraction); SC_FIELD(maxTorsoFraction);
    SC_FIELD(presenceEnterUs); SC_FIELD(presenceExitUs); SC_FIELD(addressMaxHandHeight); SC_FIELD(stillSpeed);
    SC_FIELD(addressHoldUs); SC_FIELD(takeawayHeight); SC_FIELD(topMinHeight); SC_FIELD(topDropForDownswing);
    SC_FIELD(impactBand); SC_FIELD(finishMinHeight); SC_FIELD(waggleMinTravel); SC_FIELD(maxBackswingUs);
    SC_FIELD(maxDownswingUs); SC_FIELD(maxFollowThroughUs); SC_FIELD(maxHandDropoutUs); SC_FIELD(cooldownUs);
    SC_FIELD(impactWindowUs); SC_FIELD(motionThreshold); SC_FIELD(motionBeforeImpactUs);
    SC_FIELD(motionAfterImpactUs); SC_FIELD(preRollUs); SC_FIELD(postRollUs);
#undef SC_FIELD
}

void writeRect(const RectF& r, float* out) {
    out[0] = r.x;
    out[1] = r.y;
    out[2] = r.w;
    out[3] = r.h;
}

fs::path pathFromUtf8(const char* s) { return fs::u8path(s ? s : ""); }

}  // namespace

extern "C" {

const char* sc_version(void) { return "0.1.0"; }
const char* sc_last_error(void) { return g_lastError.c_str(); }
void sc_free(char* p) { std::free(p); }

sc_detector* sc_detector_create(const char* config_json) {
    return guarded(
        [&]() -> sc_detector* {
            auto d = std::make_unique<sc_detector>();
            if (config_json && *config_json) {
                SwingDetectorConfig cfg;
                applyConfig(json::Value::parse(config_json), cfg);
                d->detector.setConfig(cfg);
            }
            return d.release();
        },
        nullptr);
}

void sc_detector_destroy(sc_detector* d) { delete d; }

int32_t sc_detector_configure(sc_detector* d, const char* config_json) {
    if (!d || !config_json) return 0;
    return guarded(
        [&]() -> int32_t {
            std::lock_guard lock(d->mu);
            SwingDetectorConfig cfg = d->detector.config();
            applyConfig(json::Value::parse(config_json), cfg);
            d->detector.setConfig(cfg);
            return 1;
        },
        0);
}

void sc_detector_reset(sc_detector* d) {
    if (!d) return;
    std::lock_guard lock(d->mu);
    d->detector.reset();
    d->audio.reset();
    d->motion.reset();
}

void sc_detector_push_pose(sc_detector* d, int64_t ts_us, const float* kp) {
    if (!d) return;
    std::lock_guard lock(d->mu);
    if (!kp) {
        d->detector.pushPose(ts_us, nullptr);
        return;
    }
    Pose pose;
    for (int i = 0; i < kJointCount; ++i) pose.kp[static_cast<size_t>(i)] = {kp[i * 3], kp[i * 3 + 1], kp[i * 3 + 2]};
    d->detector.pushPose(ts_us, &pose);
}

void sc_detector_push_audio(sc_detector* d, int64_t first_sample_us, const float* samples, int32_t count,
                            int32_t sample_rate) {
    if (!d || !samples || count <= 0) return;
    std::lock_guard lock(d->mu);
    d->audio.process(first_sample_us, samples, static_cast<size_t>(count), sample_rate);
    TimestampUs ts;
    float strength;
    while (d->audio.poll(ts, strength)) d->detector.pushImpact(ts, strength);
    d->detector.tick(first_sample_us + static_cast<int64_t>(count) * 1'000'000 / sample_rate);
}

float sc_detector_push_luma(sc_detector* d, int64_t ts_us, const uint8_t* luma, int32_t width, int32_t height,
                            int32_t stride) {
    if (!d) return 0;
    std::lock_guard lock(d->mu);
    float e = d->motion.process(luma, width, height, stride);
    d->detector.pushMotion(ts_us, e);
    return e;
}

void sc_detector_tick(sc_detector* d, int64_t now_us) {
    if (!d) return;
    std::lock_guard lock(d->mu);
    d->detector.tick(now_us);
}

void sc_detector_trigger_manual(sc_detector* d, int64_t now_us) {
    if (!d) return;
    std::lock_guard lock(d->mu);
    d->detector.triggerManual(now_us);
}

int32_t sc_detector_poll(sc_detector* d, sc_swing_event* out) {
    if (!d || !out) return 0;
    std::lock_guard lock(d->mu);
    SwingEvent e;
    if (!d->detector.poll(e)) return 0;
    std::memset(out, 0, sizeof *out);
    out->type = static_cast<int32_t>(e.type);
    out->source = static_cast<int32_t>(e.source);
    out->timestamp_us = e.timestampUs;
    out->address_us = e.timing.addressUs;
    out->takeaway_us = e.timing.takeawayUs;
    out->top_us = e.timing.topUs;
    out->impact_us = e.timing.impactUs;
    out->finish_us = e.timing.finishUs;
    out->clip_start_us = e.clipStartUs;
    out->clip_end_us = e.clipEndUs;
    out->confidence = e.confidence;
    out->tempo_ratio = e.timing.tempoRatio();
    out->impact_confirmed = e.impactConfirmed ? 1 : 0;
    return 1;
}

int32_t sc_detector_phase(sc_detector* d) {
    if (!d) return 0;
    std::lock_guard lock(d->mu);
    return static_cast<int32_t>(d->detector.phase());
}

int32_t sc_detector_golfer_present(sc_detector* d) {
    if (!d) return 0;
    std::lock_guard lock(d->mu);
    return d->detector.golferPresent() ? 1 : 0;
}

void sc_detector_audio_levels(sc_detector* d, float* level_db, float* noise_floor_db) {
    if (!d) return;
    std::lock_guard lock(d->mu);
    if (level_db) *level_db = d->audio.levelDb();
    if (noise_floor_db) *noise_floor_db = d->audio.noiseFloorDb();
}

sc_session* sc_session_create(const char* root_dir, const char* name) {
    return guarded([&]() -> sc_session* {
        return new sc_session{{}, Session::create(pathFromUtf8(root_dir), name ? name : "")};
    }, nullptr);
}

sc_session* sc_session_open(const char* folder) {
    return guarded([&]() -> sc_session* { return new sc_session{{}, Session::open(pathFromUtf8(folder))}; },
                   nullptr);
}

void sc_session_close(sc_session* s) { delete s; }

char* sc_session_json(sc_session* s) {
    if (!s) return nullptr;
    return guarded([&]() -> char* {
        std::lock_guard lock(s->mu);
        return dupString(s->session.toJson().dump());
    }, nullptr);
}

char* sc_session_folder(sc_session* s) {
    if (!s) return nullptr;
    std::lock_guard lock(s->mu);
    return dupString(s->session.folder().u8string());
}

char* sc_session_add_shot(sc_session* s, const char* shot_json) {
    if (!s || !shot_json) return nullptr;
    return guarded([&]() -> char* {
        std::lock_guard lock(s->mu);
        const Shot& shot = s->session.addShot(shotFromJson(json::Value::parse(shot_json)));
        return dupString(toJson(shot).dump());
    }, nullptr);
}

int32_t sc_session_update_shot(sc_session* s, const char* shot_json) {
    if (!s || !shot_json) return 0;
    return guarded([&]() -> int32_t {
        std::lock_guard lock(s->mu);
        return s->session.updateShot(shotFromJson(json::Value::parse(shot_json))) ? 1 : 0;
    }, 0);
}

int32_t sc_session_remove_shot(sc_session* s, const char* shot_id, int32_t delete_files) {
    if (!s || !shot_id) return 0;
    return guarded([&]() -> int32_t {
        std::lock_guard lock(s->mu);
        return s->session.removeShot(shot_id, delete_files != 0) ? 1 : 0;
    }, 0);
}

int32_t sc_session_set_persistent_annotations(sc_session* s, const char* shapes_json) {
    if (!s || !shapes_json) return 0;
    return guarded([&]() -> int32_t {
        std::lock_guard lock(s->mu);
        s->session.setPersistentAnnotations(shapesFromJson(json::Value::parse(shapes_json)));
        return 1;
    }, 0);
}

int32_t sc_session_set_cameras(sc_session* s, const char* cameras_json) {
    if (!s || !cameras_json) return 0;
    return guarded([&]() -> int32_t {
        std::vector<CameraInfo> cams;
        for (const auto& c : json::Value::parse(cameras_json).items())
            cams.push_back({c["id"].asString(), c["name"].asString(), c["kind"].asString("local"),
                            c["position"].asString("other")});
        std::lock_guard lock(s->mu);
        s->session.setCameras(std::move(cams));
        return 1;
    }, 0);
}

int32_t sc_session_rename(sc_session* s, const char* name) {
    if (!s || !name) return 0;
    return guarded([&]() -> int32_t {
        std::lock_guard lock(s->mu);
        s->session.rename(name);
        return 1;
    }, 0);
}

int32_t sc_session_end(sc_session* s) {
    if (!s) return 0;
    return guarded([&]() -> int32_t {
        std::lock_guard lock(s->mu);
        s->session.end();
        return 1;
    }, 0);
}

char* sc_sessions_list(const char* root_dir) {
    return guarded([&]() -> char* {
        json::Value arr = json::Value::array();
        for (const auto& sum : Session::list(pathFromUtf8(root_dir))) {
            json::Value v = json::Value::object();
            v["id"] = sum.id;
            v["name"] = sum.name;
            v["startedAt"] = sum.startedAt;
            v["endedAt"] = sum.endedAt;
            v["folder"] = sum.folder.u8string();
            v["shotCount"] = sum.shotCount;
            v["starredCount"] = sum.starredCount;
            arr.push_back(std::move(v));
        }
        return dupString(arr.dump());
    }, nullptr);
}

int32_t sc_layout_grid(float width, float height, const float* aspects, int32_t n, float gap, float uniform_aspect,
                       float* out_content, float* out_cells, int32_t* out_rows, int32_t* out_cols) {
    if (!aspects || n <= 0 || !out_content) return 0;
    std::vector<float> a(aspects, aspects + n);
    GridLayout g = layoutGrid(width, height, a, gap, uniform_aspect);
    if (g.content.size() != static_cast<size_t>(n)) return 0;
    for (int32_t i = 0; i < n; ++i) {
        writeRect(g.content[static_cast<size_t>(i)], out_content + i * 4);
        if (out_cells) writeRect(g.cells[static_cast<size_t>(i)], out_cells + i * 4);
    }
    if (out_rows) *out_rows = g.rows;
    if (out_cols) *out_cols = g.cols;
    return 1;
}

void sc_rotation_crop(float src_w, float src_h, float degrees, float target_aspect, float* out3) {
    if (!out3) return;
    RotationCrop r = rotationCrop(src_w, src_h, degrees, target_aspect);
    out3[0] = r.width;
    out3[1] = r.height;
    out3[2] = r.zoom;
}

void sc_zoom_window(float zoom, float cx, float cy, float* out4) {
    if (out4) writeRect(zoomWindow(zoom, cx, cy), out4);
}

void sc_cover_crop(float src_aspect, float dst_aspect, float* out4) {
    if (out4) writeRect(coverCrop(src_aspect, dst_aspect), out4);
}

char* sc_new_id(void) { return dupString(newShapeId()); }

int32_t sc_annotation_hit_test(const char* shapes_json, float x, float y, float tolerance, float frame_aspect) {
    if (!shapes_json) return -1;
    return guarded([&]() -> int32_t {
        return hitTest(shapesFromJson(json::Value::parse(shapes_json)), {x, y}, tolerance, frame_aspect);
    }, -1);
}

float sc_annotation_angle(const char* shape_json, float frame_aspect) {
    if (!shape_json) return 0;
    return guarded([&]() -> float { return angleDegrees(shapeFromJson(json::Value::parse(shape_json)), frame_aspect); },
                   0.0f);
}

}  // extern "C"
