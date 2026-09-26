#include "swingcore/annotations.h"

#include <algorithm>
#include <cmath>
#include <cstdio>
#include <limits>
#include <random>

namespace swingcore {

namespace {

struct KindName {
    ShapeKind kind;
    const char* name;
};

constexpr KindName kKinds[] = {
    {ShapeKind::Line, "line"},           {ShapeKind::Arrow, "arrow"},   {ShapeKind::Circle, "circle"},
    {ShapeKind::Rectangle, "rectangle"}, {ShapeKind::Angle, "angle"},   {ShapeKind::Freehand, "freehand"},
    {ShapeKind::Text, "text"},
};

PointF toPixels(PointF p, float aspect) { return {p.x * aspect, p.y}; }

float segmentDistance(PointF p, PointF a, PointF b) {
    float dx = b.x - a.x, dy = b.y - a.y;
    float len2 = dx * dx + dy * dy;
    float t = len2 > 0 ? std::clamp(((p.x - a.x) * dx + (p.y - a.y) * dy) / len2, 0.0f, 1.0f) : 0.0f;
    return std::hypot(p.x - (a.x + t * dx), p.y - (a.y + t * dy));
}

int hexNibble(char c) {
    if (c >= '0' && c <= '9') return c - '0';
    if (c >= 'a' && c <= 'f') return c - 'a' + 10;
    if (c >= 'A' && c <= 'F') return c - 'A' + 10;
    return -1;
}

}  // namespace

std::string newShapeId() {
    static thread_local std::mt19937_64 rng{std::random_device{}()};
    char buf[17];
    std::snprintf(buf, sizeof buf, "%016llx", static_cast<unsigned long long>(rng()));
    return buf;
}

std::string colorToHex(uint32_t argb) {
    char buf[10];
    std::snprintf(buf, sizeof buf, "#%02X%02X%02X%02X", (argb >> 16) & 0xFF, (argb >> 8) & 0xFF, argb & 0xFF,
                  (argb >> 24) & 0xFF);
    return buf;
}

uint32_t colorFromHex(const std::string& s, uint32_t fallback) {
    if (s.empty() || s[0] != '#' || (s.size() != 7 && s.size() != 9)) return fallback;
    uint32_t v[4] = {0, 0, 0, 0xFF};
    for (size_t i = 0; i < (s.size() - 1) / 2; ++i) {
        int hi = hexNibble(s[1 + i * 2]), lo = hexNibble(s[2 + i * 2]);
        if (hi < 0 || lo < 0) return fallback;
        v[i] = static_cast<uint32_t>(hi * 16 + lo);
    }
    return (v[3] << 24) | (v[0] << 16) | (v[1] << 8) | v[2];
}

const char* shapeKindName(ShapeKind k) {
    for (const auto& kn : kKinds)
        if (kn.kind == k) return kn.name;
    return "line";
}

ShapeKind shapeKindFromName(const std::string& s) {
    for (const auto& kn : kKinds)
        if (s == kn.name) return kn.kind;
    return ShapeKind::Line;
}

json::Value toJson(const Shape& s) {
    json::Value v = json::Value::object();
    v["id"] = s.id;
    v["kind"] = shapeKindName(s.kind);
    json::Value pts = json::Value::array();
    for (const auto& p : s.points) pts.push_back(json::Value(json::Value::Array{p.x, p.y}));
    v["points"] = std::move(pts);
    v["color"] = colorToHex(s.argb);
    v["strokeWidth"] = s.strokeWidth;
    if (s.dashed) v["dashed"] = true;
    v["persistent"] = s.persistent;
    if (!s.cameraId.empty()) v["cameraId"] = s.cameraId;
    if (!s.text.empty()) v["text"] = s.text;
    return v;
}

Shape shapeFromJson(const json::Value& v) {
    Shape s;
    s.id = v["id"].asString();
    if (s.id.empty()) s.id = newShapeId();
    s.kind = shapeKindFromName(v["kind"].asString());
    for (const auto& p : v["points"].items()) {
        const auto& xy = p.items();
        if (xy.size() >= 2) s.points.push_back({static_cast<float>(xy[0].asNumber()), static_cast<float>(xy[1].asNumber())});
    }
    s.argb = colorFromHex(v["color"].asString());
    s.strokeWidth = static_cast<float>(v["strokeWidth"].asNumber(4.0));
    s.dashed = v["dashed"].asBool();
    s.persistent = v["persistent"].asBool();
    s.cameraId = v["cameraId"].asString();
    s.text = v["text"].asString();
    return s;
}

json::Value toJson(const std::vector<Shape>& shapes) {
    json::Value arr = json::Value::array();
    for (const auto& s : shapes) arr.push_back(toJson(s));
    return arr;
}

std::vector<Shape> shapesFromJson(const json::Value& v) {
    std::vector<Shape> out;
    for (const auto& item : v.items()) out.push_back(shapeFromJson(item));
    return out;
}

float angleDegrees(const Shape& s, float aspect) {
    if (s.points.size() < 3) return 0;
    PointF a = toPixels(s.points[0], aspect), o = toPixels(s.points[1], aspect), b = toPixels(s.points[2], aspect);
    float a1 = std::atan2(a.y - o.y, a.x - o.x);
    float a2 = std::atan2(b.y - o.y, b.x - o.x);
    float d = std::fabs(a1 - a2) * 180.0f / 3.14159265f;
    return d > 180.0f ? 360.0f - d : d;
}

float distanceTo(const Shape& s, PointF p, float aspect) {
    const float inf = std::numeric_limits<float>::max();
    if (s.points.empty()) return inf;
    PointF q = toPixels(p, aspect);
    std::vector<PointF> px;
    px.reserve(s.points.size());
    for (const auto& pt : s.points) px.push_back(toPixels(pt, aspect));

    switch (s.kind) {
        case ShapeKind::Circle: {
            if (px.size() < 2) return std::hypot(q.x - px[0].x, q.y - px[0].y);
            float r = std::hypot(px[1].x - px[0].x, px[1].y - px[0].y);
            return std::fabs(std::hypot(q.x - px[0].x, q.y - px[0].y) - r);
        }
        case ShapeKind::Rectangle: {
            if (px.size() < 2) return inf;
            PointF a = px[0], c = px[1], b{c.x, a.y}, d{a.x, c.y};
            return std::min({segmentDistance(q, a, b), segmentDistance(q, b, c), segmentDistance(q, c, d),
                             segmentDistance(q, d, a)});
        }
        case ShapeKind::Text:
            return std::hypot(q.x - px[0].x, q.y - px[0].y);
        default: {
            if (px.size() == 1) return std::hypot(q.x - px[0].x, q.y - px[0].y);
            float best = inf;
            for (size_t i = 1; i < px.size(); ++i) best = std::min(best, segmentDistance(q, px[i - 1], px[i]));
            return best;
        }
    }
}

int hitTest(const std::vector<Shape>& shapes, PointF p, float tolerance, float aspect) {
    for (int i = static_cast<int>(shapes.size()) - 1; i >= 0; --i)
        if (distanceTo(shapes[static_cast<size_t>(i)], p, aspect) <= tolerance) return i;
    return -1;
}

}  // namespace swingcore
