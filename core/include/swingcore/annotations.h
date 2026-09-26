// Vector drawings over a video. Stored, never rasterized, so they stay
// editable, survive new shots, and can be exported with or without them.
//
// Points are normalized to the video frame (0..1, origin top-left), so a
// drawing lands on the same spot of the picture at any display size.
#pragma once

#include <cstdint>
#include <string>
#include <vector>

#include "swingcore/json.h"
#include "swingcore/types.h"

namespace swingcore {

enum class ShapeKind : int {
    Line = 0,       // 2 points
    Arrow = 1,      // 2 points, head at points[1]
    Circle = 2,     // center, point on rim
    Rectangle = 3,  // two opposite corners
    Angle = 4,      // 3 points, vertex is points[1]
    Freehand = 5,   // polyline
    Text = 6,       // 1 point, `text`
};

struct Shape {
    std::string id;
    ShapeKind kind = ShapeKind::Line;
    std::vector<PointF> points;
    uint32_t argb = 0xFFFF3B30;
    float strokeWidth = 4.0f;  // in pixels of a 1080-tall frame
    bool dashed = false;
    // Persistent shapes (e.g. a swing plane or head circle) stay on screen
    // for every shot from that camera; others belong to one shot.
    bool persistent = false;
    std::string cameraId;
    std::string text;
};

std::string newShapeId();

std::string colorToHex(uint32_t argb);       // "#RRGGBBAA"
uint32_t colorFromHex(const std::string& s, uint32_t fallback = 0xFFFF3B30);
const char* shapeKindName(ShapeKind k);
ShapeKind shapeKindFromName(const std::string& s);

json::Value toJson(const Shape& s);
Shape shapeFromJson(const json::Value& v);
json::Value toJson(const std::vector<Shape>& shapes);
std::vector<Shape> shapesFromJson(const json::Value& v);

// Interior angle in degrees for ShapeKind::Angle, measured in pixel space.
float angleDegrees(const Shape& s, float frameAspect);

// Distance in frame-height units from p to the shape's stroke; used for
// selection with a touch/mouse tolerance on every platform.
float distanceTo(const Shape& s, PointF p, float frameAspect);

// Index of the topmost shape within tolerance of p, or -1.
int hitTest(const std::vector<Shape>& shapes, PointF p, float tolerance, float frameAspect);

}  // namespace swingcore
