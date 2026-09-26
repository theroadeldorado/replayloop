// View geometry shared by every platform so multi-camera grids, rotation
// crops and digital zoom behave identically on Windows, iOS and Android.
#pragma once

#include <vector>

#include "swingcore/types.h"

namespace swingcore {

struct GridLayout {
    int rows = 0;
    int cols = 0;
    std::vector<RectF> cells;    // the slot each source was given
    std::vector<RectF> content;  // where the video actually draws inside it
};

// Arrange sources with the given width/height aspects inside a width x height
// area, choosing the row count that shows the most video. When uniformAspect
// is > 0 every tile uses that aspect and sources are cover-cropped to it.
GridLayout layoutGrid(float width, float height, const std::vector<float>& aspects, float gap,
                      float uniformAspect = 0);

// Largest rect of the given aspect centered in cell.
RectF fitContain(float aspect, const RectF& cell);

// Normalized region of a source (aspect srcAspect) that fills a destination
// of dstAspect without letterboxing.
RectF coverCrop(float srcAspect, float dstAspect);

struct RotationCrop {
    float width = 0;   // crop size in source pixels, axis-aligned to the rotated frame
    float height = 0;
    float zoom = 1;    // how much the rotated source must be scaled so the crop fills the view
};

// Rotating a W x H frame by `degrees` exposes empty corners. Returns the
// largest centered crop of targetAspect that stays fully inside the picture,
// so "rotate a little to level the horizon" never shows black wedges.
RotationCrop rotationCrop(float srcWidth, float srcHeight, float degrees, float targetAspect);

// Normalized source window for a digital zoom factor (>= 1) centered on
// (cx, cy), clamped so it never leaves the frame.
RectF zoomWindow(float zoom, float cx, float cy);

}  // namespace swingcore
