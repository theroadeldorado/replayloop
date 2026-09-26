#include "swingcore/layout.h"

#include <algorithm>
#include <cmath>

namespace swingcore {

RectF fitContain(float aspect, const RectF& cell) {
    if (aspect <= 0 || cell.w <= 0 || cell.h <= 0) return cell;
    float w = cell.w, h = cell.w / aspect;
    if (h > cell.h) {
        h = cell.h;
        w = cell.h * aspect;
    }
    return {cell.x + (cell.w - w) * 0.5f, cell.y + (cell.h - h) * 0.5f, w, h};
}

RectF coverCrop(float srcAspect, float dstAspect) {
    if (srcAspect <= 0 || dstAspect <= 0) return {0, 0, 1, 1};
    if (srcAspect > dstAspect) {
        float w = dstAspect / srcAspect;
        return {(1 - w) * 0.5f, 0, w, 1};
    }
    float h = srcAspect / dstAspect;
    return {0, (1 - h) * 0.5f, 1, h};
}

GridLayout layoutGrid(float width, float height, const std::vector<float>& aspects, float gap, float uniformAspect) {
    GridLayout best;
    const int n = static_cast<int>(aspects.size());
    if (n == 0 || width <= 0 || height <= 0) return best;

    float bestArea = -1;
    for (int rows = 1; rows <= n; ++rows) {
        int cols = (n + rows - 1) / rows;
        if ((rows - 1) * cols >= n) continue;  // an empty row: a smaller row count is equivalent
        const float fr = static_cast<float>(rows), fc = static_cast<float>(cols);
        float cellW = (width - gap * (fc - 1)) / fc;
        float cellH = (height - gap * (fr - 1)) / fr;
        if (cellW <= 0 || cellH <= 0) continue;

        GridLayout g;
        g.rows = rows;
        g.cols = cols;
        float area = 0;
        for (int i = 0; i < n; ++i) {
            int r = i / cols, c = i % cols;
            int inRow = std::min(cols, n - r * cols);
            // Center a partially filled last row.
            float rowOffset = static_cast<float>(cols - inRow) * (cellW + gap) * 0.5f;
            RectF cell{rowOffset + static_cast<float>(c) * (cellW + gap), static_cast<float>(r) * (cellH + gap), cellW,
                       cellH};
            float aspect = uniformAspect > 0 ? uniformAspect : aspects[static_cast<size_t>(i)];
            RectF content = fitContain(aspect, cell);
            g.cells.push_back(cell);
            g.content.push_back(content);
            area += content.w * content.h;
        }
        if (area > bestArea + 1e-3f) {
            bestArea = area;
            best = std::move(g);
        }
    }
    return best;
}

RotationCrop rotationCrop(float srcWidth, float srcHeight, float degrees, float targetAspect) {
    RotationCrop out;
    if (srcWidth <= 0 || srcHeight <= 0) return out;
    if (targetAspect <= 0) targetAspect = srcWidth / srcHeight;

    // A crop of height k and width k*a, rotated by theta, stays inside the
    // W x H frame iff its rotated bounding box does:
    //   k (a|cos| + |sin|) <= W   and   k (a|sin| + |cos|) <= H
    const float theta = degrees * 3.14159265358979f / 180.0f;
    const float c = std::fabs(std::cos(theta)), s = std::fabs(std::sin(theta));
    const float a = targetAspect;
    float k = std::min(srcWidth / (a * c + s), srcHeight / (a * s + c));
    out.width = k * a;
    out.height = k;

    // The view shows `out` at the size an unrotated, contain-fitted source
    // would have had, so zoom compares against that baseline.
    float baselineH = std::min(srcHeight, srcWidth / a);
    out.zoom = baselineH / k;
    return out;
}

RectF zoomWindow(float zoom, float cx, float cy) {
    zoom = std::max(1.0f, zoom);
    float size = 1.0f / zoom;
    float x = std::clamp(cx - size * 0.5f, 0.0f, 1.0f - size);
    float y = std::clamp(cy - size * 0.5f, 0.0f, 1.0f - size);
    return {x, y, size, size};
}

}  // namespace swingcore
