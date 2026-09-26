// Self-contained tests: build with `make test` (no framework needed).
#include <cmath>
#include <cstdio>
#include <cstdlib>
#include <filesystem>
#include <functional>
#include <random>
#include <string>
#include <vector>

#include "swingcore/annotations.h"
#include "swingcore/json.h"
#include "swingcore/layout.h"
#include "swingcore/session.h"
#include "swingcore/signals.h"
#include "swingcore/swing_detector.h"
#include "swingcore/swingcore_c.h"

using namespace swingcore;
namespace fs = std::filesystem;

static int g_failures = 0;
static int g_checks = 0;

#define CHECK(cond)                                                                   \
    do {                                                                              \
        ++g_checks;                                                                   \
        if (!(cond)) {                                                                \
            ++g_failures;                                                             \
            std::fprintf(stderr, "  FAIL %s:%d: %s\n", __FILE__, __LINE__, #cond);    \
        }                                                                             \
    } while (0)

#define CHECK_NEAR(a, b, eps) CHECK(std::fabs((a) - (b)) <= (eps))

struct TestCase {
    const char* name;
    std::function<void()> fn;
};
static std::vector<TestCase>& tests() {
    static std::vector<TestCase> t;
    return t;
}
struct Register {
    Register(const char* n, std::function<void()> f) { tests().push_back({n, std::move(f)}); }
};
#define TEST(name)                                     \
    static void name();                                \
    static Register reg_##name(#name, name);           \
    static void name()

// ------------------------------------------------------------------ helpers

// A synthetic golfer standing face-on. handHeight is in torso lengths above the hips.
static Pose golferPose(float handHeight, float handDx = 0) {
    Pose p;
    const float hipY = 0.62f, shoulderY = 0.37f, torso = hipY - shoulderY;
    auto set = [&](Joint j, float x, float y) { p[j] = {x, y, 0.9f}; };
    set(Joint::Nose, 0.5f, 0.28f);
    set(Joint::LeftShoulder, 0.54f, shoulderY);
    set(Joint::RightShoulder, 0.46f, shoulderY);
    set(Joint::LeftHip, 0.53f, hipY);
    set(Joint::RightHip, 0.47f, hipY);
    set(Joint::LeftKnee, 0.53f, 0.77f);
    set(Joint::RightKnee, 0.47f, 0.77f);
    set(Joint::LeftAnkle, 0.53f, 0.92f);
    set(Joint::RightAnkle, 0.47f, 0.92f);
    float handY = hipY - handHeight * torso;
    set(Joint::LeftWrist, 0.505f + handDx, handY);
    set(Joint::RightWrist, 0.495f + handDx, handY);
    return p;
}

static float smoothstep(float t) {
    t = std::fmin(1.f, std::fmax(0.f, t));
    return t * t * (3 - 2 * t);
}

struct Sim {
    SwingDetector det;
    TimestampUs t = 1'000'000;
    static constexpr TimestampUs kFrame = 33'333;  // 30 fps pose
    std::vector<SwingEvent> events;
    std::vector<TimestampUs> scheduledImpacts;  // delivered when the clock passes them

    explicit Sim(SwingDetectorConfig cfg = {}) : det(cfg) {}

    void drain() {
        SwingEvent e;
        while (det.poll(e)) events.push_back(e);
    }
    void frame(const Pose* p) {
        for (auto it = scheduledImpacts.begin(); it != scheduledImpacts.end();) {
            if (*it <= t) {
                det.pushImpact(*it, 30.0f);
                it = scheduledImpacts.erase(it);
            } else {
                ++it;
            }
        }
        det.pushPose(t, p);
        det.pushMotion(t, 0.05f);
        t += kFrame;
        drain();
    }
    void hold(float h, TimestampUs dur, float dx = 0) {
        for (TimestampUs end = t + dur; t < end;) {
            Pose p = golferPose(h, dx);
            frame(&p);
        }
    }
    void move(float from, float to, TimestampUs dur, float dxFrom = 0, float dxTo = 0) {
        TimestampUs start = t;
        while (t < start + dur) {
            float k = smoothstep(static_cast<float>(t - start) / static_cast<float>(dur));
            Pose p = golferPose(from + (to - from) * k, dxFrom + (dxTo - dxFrom) * k);
            frame(&p);
        }
    }
    // Full swing; with `impactSound` the strike is heard 20 ms after the hands bottom out.
    TimestampUs swing(bool impactSound, bool dropHandsInDownswing = false) {
        move(-0.1f, 1.4f, 900'000);  // backswing
        TimestampUs downStart = t;
        if (impactSound) scheduledImpacts.push_back(downStart + 300'000 + 20'000);
        if (dropHandsInDownswing) {
            // Motion blur: the pose engine loses the wrists for the whole downswing.
            for (TimestampUs end = t + 300'000; t < end;) {
                Pose p = golferPose(0.5f);
                p[Joint::LeftWrist].score = p[Joint::RightWrist].score = 0.05f;
                frame(&p);
            }
        } else {
            move(1.4f, -0.1f, 300'000);
        }
        TimestampUs impact = downStart + 300'000;
        move(-0.1f, 1.3f, 350'000);  // follow-through
        return impact;
    }
    int count(SwingEventType type) const {
        int n = 0;
        for (const auto& e : events) n += e.type == type;
        return n;
    }
    const SwingEvent* first(SwingEventType type) const {
        for (const auto& e : events)
            if (e.type == type) return &e;
        return nullptr;
    }
};

// ------------------------------------------------------------------ detector

TEST(golfer_presence_enter_and_leave) {
    Sim s;
    s.hold(-0.1f, 600'000);
    CHECK(s.det.golferPresent());
    CHECK(s.count(SwingEventType::GolferEntered) == 1);
    for (int i = 0; i < 60; ++i) s.frame(nullptr);
    CHECK(!s.det.golferPresent());
    CHECK(s.count(SwingEventType::GolferLeft) == 1);
    CHECK(s.det.phase() == SwingPhase::NoGolfer);
}

TEST(full_swing_with_impact_sound_is_captured) {
    Sim s;
    s.hold(-0.1f, 1'200'000);
    CHECK(s.det.phase() == SwingPhase::Address);
    TimestampUs impact = s.swing(true);
    s.hold(1.3f, 1'000'000);

    CHECK(s.count(SwingEventType::SwingCaptured) == 1);
    CHECK(s.count(SwingEventType::PracticeSwing) == 0);
    const SwingEvent* e = s.first(SwingEventType::SwingCaptured);
    if (!e) return;
    CHECK(e->impactConfirmed);
    CHECK(e->source == CaptureSource::Pose);
    CHECK(e->timing.impactUs == impact + 20'000);  // snapped to the audio
    CHECK(e->clipStartUs <= e->timing.takeawayUs);
    CHECK(e->clipEndUs >= e->timing.impactUs + 1'500'000);
    float tempo = e->timing.tempoRatio();
    CHECK(tempo > 2.0f && tempo < 4.5f);
}

TEST(swing_without_impact_sound_is_practice) {
    Sim s;
    s.hold(-0.1f, 1'200'000);
    s.swing(false);
    s.hold(1.3f, 1'000'000);
    CHECK(s.count(SwingEventType::SwingCaptured) == 0);
    CHECK(s.count(SwingEventType::PracticeSwing) == 1);
}

TEST(swing_without_audio_counts_when_audio_disabled) {
    SwingDetectorConfig cfg;
    cfg.useAudio = false;
    Sim s(cfg);
    s.hold(-0.1f, 1'200'000);
    s.swing(false);
    s.hold(1.3f, 1'000'000);
    CHECK(s.count(SwingEventType::SwingCaptured) == 1);
    const SwingEvent* e = s.first(SwingEventType::SwingCaptured);
    if (e) CHECK(!e->impactConfirmed);
}

TEST(waggles_are_ignored) {
    Sim s;
    s.hold(-0.1f, 1'200'000);
    for (int i = 0; i < 3; ++i) {
        s.move(-0.1f, 0.3f, 300'000, 0, -0.06f);
        s.move(0.3f, -0.1f, 300'000, -0.06f, 0);
        s.hold(-0.1f, 500'000);
    }
    s.hold(-0.1f, 1'000'000);
    CHECK(s.count(SwingEventType::SwingCaptured) == 0);
    CHECK(s.count(SwingEventType::SwingStarted) == 0);
    CHECK(s.count(SwingEventType::WaggleIgnored) >= 2);
}

TEST(half_swing_that_never_reaches_the_top_is_ignored) {
    Sim s;
    s.hold(-0.1f, 1'200'000);
    s.move(-0.1f, 0.7f, 500'000);
    s.move(0.7f, -0.1f, 500'000);
    s.hold(-0.1f, 800'000);
    CHECK(s.count(SwingEventType::SwingStarted) == 1);
    CHECK(s.count(SwingEventType::WaggleIgnored) >= 1);
    CHECK(s.count(SwingEventType::SwingCaptured) == 0);
    CHECK(s.count(SwingEventType::PracticeSwing) == 0);
}

TEST(hands_lost_to_motion_blur_still_captured) {
    Sim s;
    s.hold(-0.1f, 1'200'000);
    TimestampUs impact = s.swing(true, /*dropHandsInDownswing=*/true);
    s.hold(1.3f, 1'200'000);
    CHECK(s.count(SwingEventType::SwingCaptured) == 1);
    const SwingEvent* e = s.first(SwingEventType::SwingCaptured);
    if (e) CHECK(e->timing.impactUs == impact + 20'000);
}

TEST(two_swings_back_to_back_are_two_shots) {
    Sim s;
    s.hold(-0.1f, 1'200'000);
    s.swing(true);
    s.hold(1.3f, 1'000'000);
    s.move(1.3f, -0.1f, 800'000);
    s.hold(-0.1f, 1'500'000);
    s.swing(true);
    s.hold(1.3f, 1'000'000);
    CHECK(s.count(SwingEventType::SwingCaptured) == 2);
}

TEST(audio_impact_without_pose_swing_needs_motion) {
    SwingDetectorConfig cfg;
    cfg.usePose = false;
    SwingDetector det(cfg);
    TimestampUs t = 1'000'000;
    for (int i = 0; i < 60; ++i, t += 16'667) det.pushMotion(t, i > 20 && i < 40 ? 0.2f : 0.0f);
    det.pushImpact(1'000'000 + 35 * 16'667, 25.0f);
    for (int i = 0; i < 60; ++i, t += 16'667) det.pushMotion(t, 0.0f);
    SwingEvent e;
    int captures = 0;
    while (det.poll(e)) captures += e.type == SwingEventType::SwingCaptured;
    CHECK(captures == 1);

    // A loud noise with nobody moving (dropped club in the next bay) is ignored.
    det.pushImpact(t, 25.0f);
    for (int i = 0; i < 60; ++i, t += 16'667) det.pushMotion(t, 0.0f);
    captures = 0;
    while (det.poll(e)) captures += e.type == SwingEventType::SwingCaptured;
    CHECK(captures == 0);
}

TEST(audio_fallback_when_pose_misses_the_swing) {
    Sim s;
    s.hold(-0.1f, 1'200'000);
    s.det.pushImpact(s.t, 30.0f);  // golfer present, but pose never saw a swing
    s.hold(-0.1f, 3'500'000);
    CHECK(s.count(SwingEventType::SwingCaptured) == 1);
    const SwingEvent* e = s.first(SwingEventType::SwingCaptured);
    if (e) CHECK(e->source == CaptureSource::AudioMotion);
}

TEST(manual_trigger_captures_window) {
    SwingDetector det;
    det.triggerManual(10'000'000);
    SwingEvent e;
    CHECK(det.poll(e));
    CHECK(e.type == SwingEventType::SwingCaptured);
    CHECK(e.source == CaptureSource::Manual);
    CHECK(e.clipEndUs == 10'000'000);
}

// ------------------------------------------------------------------ signals

TEST(audio_impact_detects_click_over_noise) {
    AudioImpactDetector a;
    const int sr = 48000;
    std::mt19937 rng(42);
    std::normal_distribution<float> noise(0, 0.003f);
    std::vector<float> buf(sr * 2);
    for (auto& x : buf) x = noise(rng);
    auto click = [&](size_t at) {
        for (size_t i = 0; i < 480; ++i) buf[at + i] += 0.6f * std::exp(-static_cast<float>(i) / 60.0f) * ((i % 2) ? 1.f : -1.f);
    };
    click(48000);          // t = 1.0 s
    click(48000 + 4800);   // 100 ms later: ball hits the screen, inside refractory
    click(48000 + 38400);  // 800 ms later: a separate event
    // Feed in 10 ms chunks like a real audio callback.
    for (size_t off = 0; off < buf.size(); off += 480)
        a.process(static_cast<TimestampUs>(static_cast<double>(off) * 1e6 / sr), buf.data() + off, 480, sr);
    std::vector<TimestampUs> hits;
    TimestampUs ts;
    float db;
    while (a.poll(ts, db)) hits.push_back(ts);
    CHECK(hits.size() == 2);
    if (hits.size() == 2) {
        CHECK(std::llabs(hits[0] - 1'000'000) <= 2'000);
        CHECK(std::llabs(hits[1] - 1'800'000) <= 2'000);
    }
}

TEST(audio_ignores_steady_loud_noise) {
    AudioImpactDetector a;
    const int sr = 44100;
    std::mt19937 rng(7);
    std::normal_distribution<float> noise(0, 0.2f);
    std::vector<float> buf(sr);
    for (auto& x : buf) x = noise(rng);
    a.process(0, buf.data(), buf.size(), sr);
    TimestampUs ts;
    float db;
    int n = 0;
    while (a.poll(ts, db)) ++n;
    CHECK(n <= 1);  // at most the very first block before the floor settles
}

TEST(motion_energy) {
    MotionEnergy m(32, 18);
    std::vector<uint8_t> a(320 * 180, 50), b(320 * 180, 50);
    CHECK(m.process(a.data(), 320, 180, 320) == 0.0f);
    CHECK(m.process(a.data(), 320, 180, 320) == 0.0f);
    for (int y = 0; y < 90; ++y)
        for (int x = 0; x < 320; ++x) b[y * 320 + x] = 200;
    CHECK_NEAR(m.process(b.data(), 320, 180, 320), 0.5f, 0.05f);
}

// ------------------------------------------------------------------ json

TEST(json_round_trip) {
    std::string src = R"({"a":1,"b":[true,false,null],"c":{"d":"x\"yé\n"},"e":-2.5e-3})";
    json::Value v = json::Value::parse(src);
    CHECK(v["a"].asInt() == 1);
    CHECK(v["b"].size() == 3);
    CHECK(v["c"]["d"].asString() == "x\"y\xc3\xa9\n");
    CHECK_NEAR(v["e"].asNumber(), -0.0025, 1e-9);
    json::Value again = json::Value::parse(v.dump(2));
    CHECK(again.dump() == v.dump());
    CHECK(v["missing"]["deep"].isNull());
    bool threw = false;
    try {
        json::Value::parse("{\"a\":}");
    } catch (const json::ParseError&) {
        threw = true;
    }
    CHECK(threw);
}

// ------------------------------------------------------------------ annotations

TEST(annotation_serialization_and_geometry) {
    Shape line;
    line.id = "l1";
    line.kind = ShapeKind::Line;
    line.points = {{0.1f, 0.1f}, {0.9f, 0.1f}};
    line.argb = 0xFF00C8FF;
    Shape angle;
    angle.kind = ShapeKind::Angle;
    angle.points = {{0.5f, 0.2f}, {0.5f, 0.5f}, {0.5f + 0.3f / (16.f / 9.f), 0.5f}};
    Shape circle;
    circle.kind = ShapeKind::Circle;
    circle.points = {{0.5f, 0.5f}, {0.5f, 0.6f}};

    CHECK(colorToHex(0xFF00C8FF) == "#00C8FFFF");
    CHECK(colorFromHex("#00C8FFFF") == 0xFF00C8FF);
    CHECK(colorFromHex("#FF0000") == 0xFFFF0000);

    std::vector<Shape> shapes{line, angle, circle};
    auto back = shapesFromJson(json::Value::parse(toJson(shapes).dump()));
    CHECK(back.size() == 3);
    CHECK(back[0].id == "l1" && back[0].argb == 0xFF00C8FF && back[0].points.size() == 2);
    CHECK(back[1].kind == ShapeKind::Angle);
    CHECK(!back[1].id.empty());

    CHECK_NEAR(angleDegrees(angle, 16.f / 9.f), 90.0f, 0.1f);
    CHECK(hitTest(shapes, {0.5f, 0.105f}, 0.01f, 16.f / 9.f) == 0);
    CHECK(hitTest(shapes, {0.5f, 0.6f}, 0.01f, 16.f / 9.f) == 2);
    CHECK(hitTest(shapes, {0.05f, 0.9f}, 0.01f, 16.f / 9.f) == -1);
}

// ------------------------------------------------------------------ layout

TEST(layout_grid_prefers_most_visible_video) {
    auto one = layoutGrid(1600, 900, {16.f / 9.f}, 8);
    CHECK(one.rows == 1 && one.cols == 1);
    CHECK_NEAR(one.content[0].w, 1600, 0.5f);

    auto two = layoutGrid(1600, 900, {16.f / 9.f, 16.f / 9.f}, 8);
    CHECK(two.content.size() == 2);
    auto twoPortrait = layoutGrid(1600, 900, {9.f / 16.f, 9.f / 16.f}, 8);
    CHECK(twoPortrait.rows == 1 && twoPortrait.cols == 2);

    auto three = layoutGrid(1600, 900, {16.f / 9.f, 16.f / 9.f, 16.f / 9.f}, 8);
    CHECK(three.rows == 2 && three.cols == 2);
    // Last row is centered.
    CHECK_NEAR(three.cells[2].x + three.cells[2].w / 2, 800, 1.0f);

    auto uniform = layoutGrid(1600, 900, {16.f / 9.f, 9.f / 16.f}, 0, 1.0f);
    CHECK_NEAR(uniform.content[0].w, uniform.content[0].h, 0.5f);
    CHECK_NEAR(uniform.content[1].w, uniform.content[1].h, 0.5f);
}

TEST(rotation_crop_and_zoom) {
    auto r0 = rotationCrop(1920, 1080, 0, 16.f / 9.f);
    CHECK_NEAR(r0.width, 1920, 0.5f);
    CHECK_NEAR(r0.zoom, 1.0f, 1e-3f);

    auto r5 = rotationCrop(1920, 1080, 5, 16.f / 9.f);
    CHECK(r5.width < 1920 && r5.height < 1080);
    CHECK_NEAR(r5.width / r5.height, 16.f / 9.f, 1e-3f);
    CHECK(r5.zoom > 1.0f);
    // Every corner of the rotated crop is inside the frame.
    float th = 5 * 3.14159265f / 180;
    float hw = r5.width / 2, hh = r5.height / 2;
    CHECK(hw * std::cos(th) + hh * std::sin(th) <= 960.5f);
    CHECK(hw * std::sin(th) + hh * std::cos(th) <= 540.5f);

    // Landscape camera shown in a portrait view at 90 degrees: no crop needed beyond aspect.
    auto r90 = rotationCrop(1920, 1080, 90, 9.f / 16.f);
    CHECK_NEAR(r90.width, 1080, 0.5f);
    CHECK_NEAR(r90.height, 1920, 0.5f);

    RectF z = zoomWindow(2, 0.95f, 0.5f);
    CHECK_NEAR(z.w, 0.5f, 1e-6f);
    CHECK_NEAR(z.x, 0.5f, 1e-6f);  // clamped to the right edge
    RectF c = coverCrop(16.f / 9.f, 1.0f);
    CHECK_NEAR(c.w, 9.f / 16.f, 1e-5f);
}

// ------------------------------------------------------------------ sessions

TEST(session_lifecycle) {
    fs::path root = fs::temp_directory_path() / ("swingcore-test-" + newShapeId());
    {
        Session s = Session::create(root, "Range: Day/1");
        CHECK(fs::exists(s.folder() / "session.json"));
        CHECK(s.folder().filename().u8string().find("Range_ Day_1") != std::string::npos);

        Shot shot;
        shot.clips.push_back({"cam-a", "cam-a.mp4", "cam-a.jpg", 1920, 1080, 120, 3'500'000, 2'000'000, 0});
        shot.timing.takeawayUs = 1;
        shot.timing.topUs = 901;
        shot.timing.impactUs = 1201;
        const Shot& stored = s.addShot(shot);
        CHECK(stored.number == 1);
        CHECK(stored.folder == "shots/0001");
        CHECK(fs::is_directory(s.folder() / "shots/0001"));
        std::string id = stored.id;

        s.addShot(Shot{});
        Shot edit = *s.findShot(id);
        edit.starred = true;
        edit.comment = "Great tempo";
        edit.number = 99;  // identity fields are not editable
        Shape circle;
        circle.kind = ShapeKind::Circle;
        circle.points = {{0.5f, 0.3f}, {0.55f, 0.3f}};
        edit.annotations.push_back(circle);
        CHECK(s.updateShot(edit));
        s.setPersistentAnnotations({circle});
        s.setCameras({{"cam-a", "Logitech Brio", "local", "face-on"}});
        s.end();
    }
    auto list = Session::list(root);
    CHECK(list.size() == 1);
    if (list.size() == 1) {
        CHECK(list[0].shotCount == 2);
        CHECK(list[0].starredCount == 1);
        CHECK(!list[0].endedAt.empty());

        Session s = Session::open(list[0].folder);
        CHECK(s.shots().size() == 2);
        const Shot& first = s.shots()[0];
        CHECK(first.number == 1 && first.starred && first.comment == "Great tempo");
        CHECK(first.annotations.size() == 1);
        CHECK(first.clips.size() == 1 && first.clips[0].fps == 120);
        CHECK_NEAR(first.timing.tempoRatio(), 3.0f, 1e-4f);
        CHECK(s.persistentAnnotations().size() == 1 && s.persistentAnnotations()[0].persistent);
        CHECK(s.cameras().size() == 1);

        // Numbering continues after reopening, even after deletes.
        std::string secondId = s.shots()[1].id;
        CHECK(s.removeShot(secondId, true));
        const Shot& third = s.addShot(Shot{});
        CHECK(third.number == 3);
    }
    fs::remove_all(root);
}

// ------------------------------------------------------------------ C ABI

TEST(c_abi_detector_and_session) {
    sc_detector* d = sc_detector_create(R"({"usePose":false,"useMotion":false})");
    CHECK(d != nullptr);
    std::vector<float> silence(480, 0.0f), click(480, 0.0f);
    for (size_t i = 0; i < 200; ++i) click[i] = (i % 2 ? 0.7f : -0.7f) * std::exp(-static_cast<float>(i) / 50.0f);
    int64_t t = 0;
    for (int i = 0; i < 50; ++i, t += 10'000) sc_detector_push_audio(d, t, silence.data(), 480, 48000);
    sc_detector_push_audio(d, t, click.data(), 480, 48000);
    t += 10'000;
    for (int i = 0; i < 50; ++i, t += 10'000) sc_detector_push_audio(d, t, silence.data(), 480, 48000);
    sc_swing_event e{};
    int captured = 0;
    while (sc_detector_poll(d, &e)) captured += e.type == SC_EVENT_SWING_CAPTURED;
    CHECK(captured == 1);
    CHECK(e.clip_end_us - e.clip_start_us == 3'500'000);
    sc_detector_destroy(d);

    CHECK(sc_detector_create("{not json") == nullptr);
    CHECK(std::string(sc_last_error()).find("offset") != std::string::npos);

    fs::path root = fs::temp_directory_path() / ("swingcore-capi-" + newShapeId());
    sc_session* s = sc_session_create(root.u8string().c_str(), "");
    CHECK(s != nullptr);
    char* shot = sc_session_add_shot(s, R"({"clips":[{"cameraId":"c1","file":"c1.mp4"}]})");
    CHECK(shot != nullptr);
    if (shot) {
        json::Value v = json::Value::parse(shot);
        CHECK(v["number"].asInt() == 1);
        v["starred"] = true;
        CHECK(sc_session_update_shot(s, v.dump().c_str()) == 1);
        sc_free(shot);
    }
    CHECK(sc_session_end(s) == 1);
    sc_session_close(s);
    char* list = sc_sessions_list(root.u8string().c_str());
    CHECK(list != nullptr);
    if (list) {
        json::Value v = json::Value::parse(list);
        CHECK(v.size() == 1 && v.items()[0]["starredCount"].asInt() == 1);
        sc_free(list);
    }
    fs::remove_all(root);

    float aspects[3] = {16.f / 9.f, 16.f / 9.f, 9.f / 16.f};
    float content[12], cells[12];
    int32_t rows = 0, cols = 0;
    CHECK(sc_layout_grid(1920, 1080, aspects, 3, 4, 0, content, cells, &rows, &cols) == 1);
    CHECK(rows * cols >= 3);
}

int main(int argc, char** argv) {
    const char* filter = argc > 1 ? argv[1] : nullptr;
    int ran = 0, failedTests = 0;
    for (const auto& t : tests()) {
        if (filter && std::string(t.name).find(filter) == std::string::npos) continue;
        int before = g_failures;
        t.fn();
        ++ran;
        bool ok = g_failures == before;
        failedTests += !ok;
        std::printf("%s %s\n", ok ? "  ok  " : "  FAIL", t.name);
    }
    std::printf("\n%d tests, %d checks, %d failed checks\n", ran, g_checks, g_failures);
    return failedTests ? 1 : 0;
}
