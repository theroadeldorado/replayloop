#include "swingcore/session.h"

#include <algorithm>
#include <chrono>
#include <ctime>
#include <fstream>
#include <sstream>
#include <stdexcept>

namespace fs = std::filesystem;

namespace swingcore {

namespace {

std::tm toTm(std::time_t t, bool utc) {
    std::tm tm{};
#ifdef _WIN32
    utc ? gmtime_s(&tm, &t) : localtime_s(&tm, &t);
#else
    utc ? gmtime_r(&t, &tm) : localtime_r(&t, &tm);
#endif
    return tm;
}

std::string formatNow(const char* fmt, bool utc) {
    std::time_t t = std::chrono::system_clock::to_time_t(std::chrono::system_clock::now());
    std::tm tm = toTm(t, utc);
    char buf[64];
    std::strftime(buf, sizeof buf, fmt, &tm);
    return buf;
}

std::string readFile(const fs::path& p) {
    std::ifstream in(p, std::ios::binary);
    if (!in) throw std::runtime_error("cannot read " + p.u8string());
    std::ostringstream ss;
    ss << in.rdbuf();
    return ss.str();
}

void writeFileAtomic(const fs::path& p, const std::string& data) {
    fs::path tmp = p;
    tmp += ".tmp";
    {
        std::ofstream out(tmp, std::ios::binary | std::ios::trunc);
        if (!out) throw std::runtime_error("cannot write " + tmp.u8string());
        out << data;
        out.flush();
        if (!out) throw std::runtime_error("write failed " + tmp.u8string());
    }
    fs::rename(tmp, p);
}

const char* captureSourceName(CaptureSource s) {
    switch (s) {
        case CaptureSource::AudioMotion: return "audio";
        case CaptureSource::Manual: return "manual";
        default: return "pose";
    }
}

CaptureSource captureSourceFromName(const std::string& s) {
    if (s == "audio") return CaptureSource::AudioMotion;
    if (s == "manual") return CaptureSource::Manual;
    return CaptureSource::Pose;
}

json::Value clipToJson(const ClipInfo& c) {
    json::Value v = json::Value::object();
    v["cameraId"] = c.cameraId;
    v["file"] = c.file;
    v["thumbnail"] = c.thumbnail;
    v["width"] = c.width;
    v["height"] = c.height;
    v["fps"] = c.fps;
    v["durationUs"] = c.durationUs;
    v["impactOffsetUs"] = c.impactOffsetUs;
    v["rotationDeg"] = c.rotationDeg;
    return v;
}

ClipInfo clipFromJson(const json::Value& v) {
    ClipInfo c;
    c.cameraId = v["cameraId"].asString();
    c.file = v["file"].asString();
    c.thumbnail = v["thumbnail"].asString();
    c.width = static_cast<int>(v["width"].asInt());
    c.height = static_cast<int>(v["height"].asInt());
    c.fps = v["fps"].asNumber();
    c.durationUs = v["durationUs"].asInt();
    c.impactOffsetUs = v["impactOffsetUs"].asInt();
    c.rotationDeg = static_cast<int>(v["rotationDeg"].asInt());
    return c;
}

json::Value timingToJson(const SwingTiming& t) {
    json::Value v = json::Value::object();
    v["addressUs"] = t.addressUs;
    v["takeawayUs"] = t.takeawayUs;
    v["topUs"] = t.topUs;
    v["impactUs"] = t.impactUs;
    v["finishUs"] = t.finishUs;
    v["tempoRatio"] = t.tempoRatio();
    return v;
}

SwingTiming timingFromJson(const json::Value& v) {
    SwingTiming t;
    t.addressUs = v["addressUs"].asInt();
    t.takeawayUs = v["takeawayUs"].asInt();
    t.topUs = v["topUs"].asInt();
    t.impactUs = v["impactUs"].asInt();
    t.finishUs = v["finishUs"].asInt();
    return t;
}

json::Value cameraToJson(const CameraInfo& c) {
    json::Value v = json::Value::object();
    v["id"] = c.id;
    v["name"] = c.name;
    v["kind"] = c.kind;
    v["position"] = c.position;
    return v;
}

CameraInfo cameraFromJson(const json::Value& v) {
    return {v["id"].asString(), v["name"].asString(), v["kind"].asString("local"), v["position"].asString("other")};
}

}  // namespace

std::string isoTimestampUtc() { return formatNow("%Y-%m-%dT%H:%M:%SZ", true); }

std::string sanitizeFileName(const std::string& s) {
    std::string out;
    for (unsigned char c : s) {
        if (c < 0x20 || std::string("<>:\"/\\|?*").find(static_cast<char>(c)) != std::string::npos) out += '_';
        else out += static_cast<char>(c);
    }
    while (!out.empty() && (out.back() == ' ' || out.back() == '.')) out.pop_back();
    if (out.size() > 60) out.resize(60);
    return out;
}

json::Value toJson(const Shot& s) {
    json::Value v = json::Value::object();
    v["id"] = s.id;
    v["number"] = s.number;
    v["createdAt"] = s.createdAt;
    v["folder"] = s.folder;
    json::Value clips = json::Value::array();
    for (const auto& c : s.clips) clips.push_back(clipToJson(c));
    v["clips"] = std::move(clips);
    v["starred"] = s.starred;
    v["comment"] = s.comment;
    v["club"] = s.club;
    json::Value tags = json::Value::array();
    for (const auto& t : s.tags) tags.push_back(t);
    v["tags"] = std::move(tags);
    v["timing"] = timingToJson(s.timing);
    v["confidence"] = s.confidence;
    v["impactConfirmed"] = s.impactConfirmed;
    v["source"] = captureSourceName(s.source);
    v["annotations"] = toJson(s.annotations);
    if (!s.extra.isNull()) v["extra"] = s.extra;
    return v;
}

Shot shotFromJson(const json::Value& v) {
    Shot s;
    s.id = v["id"].asString();
    s.number = static_cast<int>(v["number"].asInt());
    s.createdAt = v["createdAt"].asString();
    s.folder = v["folder"].asString();
    for (const auto& c : v["clips"].items()) s.clips.push_back(clipFromJson(c));
    s.starred = v["starred"].asBool();
    s.comment = v["comment"].asString();
    s.club = v["club"].asString();
    for (const auto& t : v["tags"].items()) s.tags.push_back(t.asString());
    s.timing = timingFromJson(v["timing"]);
    s.confidence = static_cast<float>(v["confidence"].asNumber());
    s.impactConfirmed = v["impactConfirmed"].asBool();
    s.source = captureSourceFromName(v["source"].asString());
    s.annotations = shapesFromJson(v["annotations"]);
    s.extra = v["extra"];
    return s;
}

Session Session::create(const fs::path& root, const std::string& name) {
    Session s;
    s.startedAt_ = isoTimestampUtc();
    s.name_ = name.empty() ? formatNow("Session %b %d, %H:%M", false) : name;
    std::string base = formatNow("%Y-%m-%d_%H%M%S", false);
    std::string clean = sanitizeFileName(name);
    if (!clean.empty()) base += "_" + clean;

    fs::create_directories(root);
    fs::path folder = root / fs::u8path(base);
    for (int i = 2; fs::exists(folder); ++i) folder = root / fs::u8path(base + "-" + std::to_string(i));
    fs::create_directories(folder / "shots");
    s.folder_ = folder;
    s.id_ = folder.filename().u8string();
    s.save();
    return s;
}

Session Session::open(const fs::path& folder) {
    Session s;
    s.folder_ = folder;
    s.load(json::Value::parse(readFile(folder / "session.json")));
    if (s.id_.empty()) s.id_ = folder.filename().u8string();
    return s;
}

std::vector<SessionSummary> Session::list(const fs::path& root) {
    std::vector<SessionSummary> out;
    std::error_code ec;
    if (!fs::is_directory(root, ec)) return out;
    for (const auto& entry : fs::directory_iterator(root, ec)) {
        if (!entry.is_directory()) continue;
        fs::path meta = entry.path() / "session.json";
        if (!fs::exists(meta)) continue;
        try {
            json::Value v = json::Value::parse(readFile(meta));
            SessionSummary sum;
            sum.id = v["id"].asString(entry.path().filename().u8string());
            sum.name = v["name"].asString();
            sum.startedAt = v["startedAt"].asString();
            sum.endedAt = v["endedAt"].asString();
            sum.folder = entry.path();
            for (const auto& shot : v["shots"].items()) {
                ++sum.shotCount;
                if (shot["starred"].asBool()) ++sum.starredCount;
            }
            out.push_back(std::move(sum));
        } catch (const std::exception&) {
            // Corrupt or foreign folder: leave it alone.
        }
    }
    std::sort(out.begin(), out.end(), [](const auto& a, const auto& b) { return a.startedAt > b.startedAt; });
    return out;
}

const Shot* Session::findShot(const std::string& id) const {
    for (const auto& s : shots_)
        if (s.id == id) return &s;
    return nullptr;
}

const Shot& Session::addShot(Shot shot) {
    char num[16];
    std::snprintf(num, sizeof num, "%04d", nextNumber_);
    shot.number = nextNumber_++;
    shot.id = id_ + "/" + num;
    shot.folder = std::string("shots/") + num;
    if (shot.createdAt.empty()) shot.createdAt = isoTimestampUtc();
    fs::create_directories(folder_ / fs::u8path(shot.folder));
    shots_.push_back(std::move(shot));
    save();
    return shots_.back();
}

bool Session::updateShot(const Shot& shot) {
    for (auto& s : shots_) {
        if (s.id != shot.id) continue;
        Shot updated = shot;
        updated.number = s.number;
        updated.folder = s.folder;
        updated.createdAt = s.createdAt;
        s = std::move(updated);
        save();
        return true;
    }
    return false;
}

bool Session::removeShot(const std::string& id, bool deleteFiles) {
    auto it = std::find_if(shots_.begin(), shots_.end(), [&](const Shot& s) { return s.id == id; });
    if (it == shots_.end()) return false;
    if (deleteFiles && !it->folder.empty()) {
        std::error_code ec;
        fs::remove_all(folder_ / fs::u8path(it->folder), ec);
    }
    shots_.erase(it);
    save();
    return true;
}

void Session::setPersistentAnnotations(std::vector<Shape> shapes) {
    for (auto& s : shapes) s.persistent = true;
    persistent_ = std::move(shapes);
    save();
}

void Session::setCameras(std::vector<CameraInfo> cameras) {
    cameras_ = std::move(cameras);
    save();
}

void Session::rename(const std::string& name) {
    name_ = name;
    save();
}

void Session::end() {
    if (endedAt_.empty()) endedAt_ = isoTimestampUtc();
    save();
}

json::Value Session::toJson() const {
    json::Value v = json::Value::object();
    v["schemaVersion"] = kSessionSchemaVersion;
    v["id"] = id_;
    v["name"] = name_;
    v["startedAt"] = startedAt_;
    v["endedAt"] = endedAt_.empty() ? json::Value() : json::Value(endedAt_);
    v["nextNumber"] = nextNumber_;
    json::Value cams = json::Value::array();
    for (const auto& c : cameras_) cams.push_back(cameraToJson(c));
    v["cameras"] = std::move(cams);
    v["persistentAnnotations"] = swingcore::toJson(persistent_);
    json::Value shots = json::Value::array();
    for (const auto& s : shots_) shots.push_back(swingcore::toJson(s));
    v["shots"] = std::move(shots);
    return v;
}

void Session::load(const json::Value& v) {
    if (v["schemaVersion"].asInt(1) > kSessionSchemaVersion)
        throw std::runtime_error("session was written by a newer version of SwingLoop");
    id_ = v["id"].asString();
    name_ = v["name"].asString();
    startedAt_ = v["startedAt"].asString();
    endedAt_ = v["endedAt"].asString();
    cameras_.clear();
    for (const auto& c : v["cameras"].items()) cameras_.push_back(cameraFromJson(c));
    persistent_ = shapesFromJson(v["persistentAnnotations"]);
    shots_.clear();
    for (const auto& s : v["shots"].items()) shots_.push_back(shotFromJson(s));
    int maxNumber = 0;
    for (const auto& s : shots_) maxNumber = std::max(maxNumber, s.number);
    nextNumber_ = std::max(static_cast<int>(v["nextNumber"].asInt(1)), maxNumber + 1);
}

void Session::save() const { writeFileAtomic(folder_ / "session.json", toJson().dump(2)); }

}  // namespace swingcore
