#include "floor_hit_test.h"
#include "floor_draw_depth.h"
#include "position_track_drag.h"

#include <array>
#include <iostream>

int main()
{
    // Both floors occupy the same screen point. D3D uses LESS_EQUAL depth, so
    // the smaller depth is the tile actually visible at that pixel.
    constexpr std::array<ee::FloorHitCandidate, 2> overlap{{
        {4u, 0.0f, 0.25f},
        {9u, 0.0f, 0.75f}
    }};
    const std::int32_t selected = ee::SelectNearestFloorHit(overlap, 1.0f);
    if (selected != 4)
    {
        std::cerr << "overlap hit selected back floor " << selected
                  << " instead of front floor 4.\n";
        return 1;
    }

    // Distance still wins when candidates do not overlap within hit tolerance.
    constexpr std::array<ee::FloorHitCandidate, 2> separated{{
        {2u, 0.01f, 0.8f},
        {7u, 0.25f, 0.1f}
    }};
    if (ee::SelectNearestFloorHit(separated, 1.0f) != 2)
    {
        std::cerr << "draw depth incorrectly overrode a nearer tile.\n";
        return 1;
    }

    // Runtime sorting can reverse the usual index order. Exercise the exact
    // shared D3D rule, not hard-coded index ordering in the picker.
    const std::array<ee::FloorHitCandidate, 2> sorted{{
        {4u, 0.0f, ee::FloorDrawDepth(4, -8, 20)},
        {9u, 0.0f, ee::FloorDrawDepth(9, 0, 20)}
    }};
    if (ee::SelectNearestFloorHit(sorted, 1.0f) != 9 ||
        ee::SelectNearestFloorHit({}, 1.0f) != -1 ||
        ee::FloorDrawDepth(4, 99, 20) != 0.0f || ee::FloorDrawDepth(4, -99, 20) != 1.0f)
    {
        std::cerr << "sorting offset/depth clipping mismatch.\n";
        return 1;
    }

    ee::PositionTrackDrag drag;
    ee::ViewportInputTransform view{12, -7, 40, 0, 800, 600};
    drag.Begin(5, 400, 300, 4, 4, view);
    auto result = drag.End(402, 301);
    if (result.floor != 5 || result.moved || drag.Active()) return 1;
    drag.Begin(5, 400, 300, 4, 4, view);
    drag.Move(420, 320);
    result = drag.End(460, 340);
    if (result.floor != 5 || !result.moved || result.delta.x != 1.5 || result.delta.y != -1.0) return 1;
    if (drag.End(460, 340).floor != -1) return 1; // no duplicate on second up
    drag.Begin(5, 400, 300, 4, 4, view);
    drag.Move(460, 340);
    drag.Cancel(); // capture lost / Escape / cancel must all discard pending edit
    if (drag.End(460, 340).floor != -1) return 1;
    view.rotation = 1.5707963267948966;
    drag.Begin(8, 400, 300, 4, 4, view);
    result = drag.End(440, 300);
    if (result.floor != 8 || std::abs(result.delta.x) > 1e-12 || std::abs(result.delta.y - 1) > 1e-12) return 1;
    const auto point = view.Point(440, 300);
    if (std::abs(point.x - 12) > 1e-12 || std::abs(point.y + 6) > 1e-12) return 1;
    return 0;
}
