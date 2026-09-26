// On-disk session format shared by Windows, iOS and Android.
//
//   <root>/2026-09-25_153012_Range Day/
//       session.json
//       shots/0001/cam-face-on.mp4
//       shots/0001/cam-face-on.jpg      (thumbnail at impact)
//       shots/0001/cam-dtl.mp4
//       shots/0002/...
//
// session.json is rewritten atomically (temp file + rename) on every change,
// so a crash never leaves a half-written session behind.
#pragma once

#include <filesystem>
#include <string>
#include <vector>

#include "swingcore/annotations.h"
#include "swingcore/json.h"
#include "swingcore/swing_detector.h"

namespace swingcore {

constexpr int kSessionSchemaVersion = 1;

struct CameraInfo {
    std::string id;
    std::string name;
    std::string kind;      // "local" | "phone"
    std::string position;  // "face-on" | "down-the-line" | "rear" | "other"
};

struct ClipInfo {
    std::string cameraId;
    std::string file;       // relative to the shot folder
    std::string thumbnail;  // relative to the shot folder
    int width = 0;
    int height = 0;
    double fps = 0;
    TimestampUs durationUs = 0;
    TimestampUs impactOffsetUs = 0;  // impact position inside the clip
    int rotationDeg = 0;             // display rotation to apply on playback
};

struct Shot {
    std::string id;
    int number = 0;
    std::string createdAt;  // ISO-8601 UTC
    std::string folder;     // relative to the session folder, e.g. "shots/0001"
    std::vector<ClipInfo> clips;
    bool starred = false;
    std::string comment;
    std::string club;
    std::vector<std::string> tags;
    SwingTiming timing;
    float confidence = 0;
    bool impactConfirmed = false;
    CaptureSource source = CaptureSource::Pose;
    std::vector<Shape> annotations;  // this shot only
    json::Value extra;               // launch monitor numbers, etc.
};

struct SessionSummary {
    std::string id;
    std::string name;
    std::string startedAt;
    std::string endedAt;
    std::filesystem::path folder;
    int shotCount = 0;
    int starredCount = 0;
};

class Session {
public:
    static Session create(const std::filesystem::path& root, const std::string& name);
    static Session open(const std::filesystem::path& folder);
    // Newest first. Folders that fail to parse are skipped.
    static std::vector<SessionSummary> list(const std::filesystem::path& root);

    const std::string& id() const { return id_; }
    const std::string& name() const { return name_; }
    const std::filesystem::path& folder() const { return folder_; }
    const std::string& startedAt() const { return startedAt_; }
    const std::string& endedAt() const { return endedAt_; }
    bool ended() const { return !endedAt_.empty(); }

    const std::vector<Shot>& shots() const { return shots_; }
    const Shot* findShot(const std::string& id) const;
    std::filesystem::path shotFolder(const Shot& s) const { return folder_ / std::filesystem::u8path(s.folder); }

    // Assigns id, number, createdAt and folder; creates the folder; saves.
    const Shot& addShot(Shot shot);
    // Replaces the stored shot with the same id. Identity fields are kept.
    bool updateShot(const Shot& shot);
    bool removeShot(const std::string& id, bool deleteFiles);

    const std::vector<Shape>& persistentAnnotations() const { return persistent_; }
    void setPersistentAnnotations(std::vector<Shape> shapes);
    const std::vector<CameraInfo>& cameras() const { return cameras_; }
    void setCameras(std::vector<CameraInfo> cameras);
    void rename(const std::string& name);
    void end();
    void save() const;

    json::Value toJson() const;

private:
    Session() = default;
    void load(const json::Value& v);

    std::string id_, name_, startedAt_, endedAt_;
    std::filesystem::path folder_;
    std::vector<Shot> shots_;
    std::vector<Shape> persistent_;
    std::vector<CameraInfo> cameras_;
    int nextNumber_ = 1;
};

json::Value toJson(const Shot& s);
Shot shotFromJson(const json::Value& v);

std::string isoTimestampUtc();
std::string sanitizeFileName(const std::string& s);

}  // namespace swingcore
