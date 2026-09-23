#include "level_scene.h"

#include <algorithm>
#include <cmath>
#include <limits>

namespace ee
{
thread_local const LevelScene* LevelScene::current_runtime_scene_ = nullptr;

std::shared_ptr<LevelScene> LevelScene::Create(
    const EeFloor* floor_data,
    std::uint32_t floor_count,
    const EeGeometry* geometry_data,
    std::uint32_t geometry_count,
    const EePoint* point_data,
    std::uint32_t point_count,
    float bounds_left,
    float bounds_top,
    float bounds_right,
    float bounds_bottom)
{
    if (floor_count == 0 || floor_data == nullptr ||
        geometry_count == 0 || geometry_data == nullptr ||
        point_count == 0 || point_data == nullptr)
        return nullptr;

    auto scene = std::make_shared<LevelScene>();
    scene->floors.assign(floor_data, floor_data + floor_count);
    scene->geometries.assign(geometry_data, geometry_data + geometry_count);
    scene->points.assign(point_data, point_data + point_count);
    scene->bounds_left = bounds_left;
    scene->bounds_top = bounds_top;
    scene->bounds_right = bounds_right;
    scene->bounds_bottom = bounds_bottom;

    for (const EeGeometry& geometry : scene->geometries)
    {
        const std::uint64_t end = static_cast<std::uint64_t>(geometry.point_offset) + geometry.point_count;
        if (geometry.point_count < 3 || end > scene->points.size())
            return nullptr;
    }

    for (const EeFloor& floor : scene->floors)
    {
        if (floor.geometry_id >= geometry_count || !std::isfinite(floor.x) || !std::isfinite(floor.y))
            return nullptr;
    }

    scene->RebuildCells();
    scene->track_transforms_.ResetBase(scene->floors);
    return scene;
}

const LevelScene* LevelScene::CurrentRuntimeScene() noexcept
{
    return current_runtime_scene_;
}

void LevelScene::Query(
    float left,
    float top,
    float right,
    float bottom,
    std::vector<std::uint32_t>& output) const
{
    output.clear();
    {
        std::lock_guard lock(transform_mutex_);

        const int min_x = FastFloor(left / CellSize);
        const int max_x = FastFloor(right / CellSize);
        const int min_y = FastFloor(top / CellSize);
        const int max_y = FastFloor(bottom / CellSize);

        const std::int64_t cells_wide = static_cast<std::int64_t>(max_x) - min_x + 1;
        const std::int64_t cells_high = static_cast<std::int64_t>(max_y) - min_y + 1;
        if (cells_wide > 0 && cells_high > 0 && cells_wide * cells_high <= 2000000)
        {
            for (int y = min_y; y <= max_y; ++y)
            {
                for (int x = min_x; x <= max_x; ++x)
                {
                    const auto found = cells.find(Key(x, y));
                    if (found == cells.end())
                        continue;

                    output.insert(output.end(), found->second.begin(), found->second.end());
                }
            }
        }
    }

    // Center-based spatial culling is insufficient for a lazy visual-only scale:
    // the floor center can remain outside the viewport while its transformed
    // geometry reaches into it. Supplement with the conservative scale index.
    AppendVisualTransformCandidates(left, top, right, bottom, output);
    std::sort(output.begin(), output.end());
    output.erase(std::unique(output.begin(), output.end()), output.end());
}

void LevelScene::AppendVisualTransformCandidates(
    float left,
    float top,
    float right,
    float bottom,
    std::vector<std::uint32_t>& output) const
{
    std::lock_guard lock(transform_mutex_);
    if (visual_transform_floors_.empty() || max_visual_transform_cull_radius_ <= BaseCullMargin)
        return;

    const auto append_if_intersects = [this, left, top, right, bottom, &output](std::uint32_t floor)
    {
        if (floor >= floors.size() || floor >= visual_transform_cull_radius_.size())
            return;

        const float radius = visual_transform_cull_radius_[floor];
        if (!(radius > BaseCullMargin) || !std::isfinite(radius))
            return;

        const EeFloor& item = floors[floor];
        if (item.x + radius < left || item.x - radius > right ||
            item.y + radius < top || item.y - radius > bottom)
            return;

        output.push_back(floor);
    };

    const float expanded_left = left - max_visual_transform_cull_radius_;
    const float expanded_top = top - max_visual_transform_cull_radius_;
    const float expanded_right = right + max_visual_transform_cull_radius_;
    const float expanded_bottom = bottom + max_visual_transform_cull_radius_;

    const int min_x = FastFloor(expanded_left / CellSize);
    const int max_x = FastFloor(expanded_right / CellSize);
    const int min_y = FastFloor(expanded_top / CellSize);
    const int max_y = FastFloor(expanded_bottom / CellSize);
    const std::int64_t cells_wide = static_cast<std::int64_t>(max_x) - min_x + 1;
    const std::int64_t cells_high = static_cast<std::int64_t>(max_y) - min_y + 1;

    // A deliberately enormous scale can make the expanded cell rectangle huge.
    // Falling back to the compact list of scale-sensitive floors preserves
    // correctness without walking millions of empty cells.
    if (cells_wide <= 0 || cells_high <= 0 || cells_wide * cells_high > 2000000)
    {
        for (const std::uint32_t floor : visual_transform_floors_)
            append_if_intersects(floor);
        return;
    }

    for (int y = min_y; y <= max_y; ++y)
    {
        for (int x = min_x; x <= max_x; ++x)
        {
            const auto found = visual_transform_cells_.find(Key(x, y));
            if (found == visual_transform_cells_.end())
                continue;

            for (const std::uint32_t floor : found->second)
                append_if_intersects(floor);
        }
    }
}

bool LevelScene::SetTrackTransformTimeline(
    const EeTrackTransformEvent* events,
    std::uint32_t event_count) noexcept
{
    std::lock_guard lock(transform_mutex_);
    track_transforms_.Restore(floors, cells);
    has_track_playback_anchor_ = false;
    track_playback_anchor_playing_ = false;
    track_playback_anchor_chart_time_ = 0.0;
    track_playback_anchor_rate_ = 1.0;
    track_playback_anchor_steady_ = {};

    const bool accepted = track_transforms_.SetTimeline(events, event_count);
    if (accepted)
        RebuildVisualTransformCullIndex(events, event_count);
    else
        RebuildVisualTransformCullIndex(nullptr, 0u);
    return accepted;
}

void LevelScene::SetTrackPlaybackAnchor(
    double chart_time,
    double chart_rate,
    std::uint32_t flags) noexcept
{
    std::lock_guard lock(transform_mutex_);

    const auto now = std::chrono::steady_clock::now();
    const bool active = (flags & EE_PLAYBACK_FLAG_ACTIVE) != 0u;
    const bool playing = active && (flags & EE_PLAYBACK_FLAG_PLAYING) != 0u;
    const double safe_rate = std::isfinite(chart_rate) && chart_rate > 0.0 ? chart_rate : 1.0;
    double safe_chart_time = std::isfinite(chart_time) ? chart_time : 0.0;

    if (active && has_track_playback_anchor_)
    {
        double previous_clock_now = track_playback_anchor_chart_time_;
        if (track_playback_anchor_playing_)
        {
            previous_clock_now += std::chrono::duration<double>(
                now - track_playback_anchor_steady_).count() * track_playback_anchor_rate_;
        }

        const double regression = previous_clock_now - safe_chart_time;
        if (regression > 0.0 && regression <= PlaybackAnchorJitterToleranceSeconds)
            safe_chart_time = previous_clock_now;
    }

    track_transforms_.SetPlaybackAnchor(safe_chart_time, safe_rate, flags);

    if (active)
    {
        has_track_playback_anchor_ = true;
        track_playback_anchor_playing_ = playing;
        track_playback_anchor_chart_time_ = safe_chart_time;
        track_playback_anchor_rate_ = safe_rate;
        track_playback_anchor_steady_ = now;
    }
    else
    {
        has_track_playback_anchor_ = false;
        track_playback_anchor_playing_ = false;
    }
}

void LevelScene::UpdateTrackTransforms() noexcept
{
    current_runtime_scene_ = this;
    const auto total_started = std::chrono::steady_clock::now();
    TrackTransformUpdateMetrics metrics{};
    {
        std::lock_guard lock(transform_mutex_);
        metrics = track_transforms_.Update(floors, cells);
    }
    metrics.total_ms = std::chrono::duration<double, std::milli>(
        std::chrono::steady_clock::now() - total_started).count();
    {
        std::lock_guard lock(transform_metrics_mutex_);
        last_track_transform_metrics_ = metrics;
    }
}

void LevelScene::EvaluateVisibleTrackVisuals(
    const std::vector<std::uint32_t>& visible_floors) noexcept
{
    std::lock_guard lock(transform_mutex_);
    for (const std::uint32_t floor : visible_floors)
    {
        if (floor >= floors.size())
            continue;
        track_transforms_.EvaluateVisualForFloor(floor, floors[floor]);
    }
}

TrackTransformUpdateMetrics LevelScene::TrackTransformMetrics() const noexcept
{
    TrackTransformUpdateMetrics metrics{};
    {
        std::lock_guard lock(transform_metrics_mutex_);
        metrics = last_track_transform_metrics_;
    }

    const TrackTransformActiveClassification classification = track_transforms_.ActiveClassification();
    metrics.active_position_count = classification.position_count;
    metrics.active_visual_only_count = classification.visual_only_count;
    metrics.active_single_event_count = classification.single_event_count;
    return metrics;
}

void LevelScene::RebuildCells() noexcept
{
    cells.clear();
    cells.reserve(std::min<std::size_t>(floors.size(), 262144u));
    for (std::uint32_t i = 0; i < floors.size(); ++i)
    {
        const EeFloor& floor = floors[i];
        const int x = FastFloor(floor.x / CellSize);
        const int y = FastFloor(floor.y / CellSize);
        cells[Key(x, y)].push_back(i);
    }
}

void LevelScene::RebuildVisualTransformCullIndex(
    const EeTrackTransformEvent* events,
    std::uint32_t event_count) noexcept
{
    visual_transform_cells_.clear();
    visual_transform_floors_.clear();
    visual_transform_cull_radius_.assign(floors.size(), 0.0f);
    max_visual_transform_cull_radius_ = 0.0f;

    if (events == nullptr || event_count == 0 || floors.empty())
        return;

    constexpr std::uint32_t position_mask = EE_TRACK_TRANSFORM_X | EE_TRACK_TRANSFORM_Y;
    constexpr std::uint32_t scale_mask = EE_TRACK_TRANSFORM_SCALE_X | EE_TRACK_TRANSFORM_SCALE_Y;

    std::vector<std::uint32_t> combined_flags(floors.size(), 0u);
    std::vector<float> max_scale(floors.size(), 1.0f);
    std::vector<bool> has_scale(floors.size(), false);

    for (std::uint32_t i = 0; i < event_count; ++i)
    {
        const EeTrackTransformEvent& item = events[i];
        if (item.floor < 0 || static_cast<std::size_t>(item.floor) >= floors.size())
            continue;

        const std::size_t floor = static_cast<std::size_t>(item.floor);
        combined_flags[floor] |= item.flags;

        const auto include_scale = [&max_scale, &has_scale, floor](float value)
        {
            if (!std::isfinite(value))
                return;
            has_scale[floor] = true;
            max_scale[floor] = std::max(max_scale[floor], std::abs(value));
        };

        if ((item.flags & EE_TRACK_TRANSFORM_SCALE_X) != 0u)
        {
            include_scale(item.start_scale_x);
            include_scale(item.target_scale_x);
        }
        if ((item.flags & EE_TRACK_TRANSFORM_SCALE_Y) != 0u)
        {
            include_scale(item.start_scale_y);
            include_scale(item.target_scale_y);
        }
    }

    visual_transform_cells_.reserve(std::min<std::size_t>(floors.size(), 262144u));
    visual_transform_floors_.reserve(floors.size() / 8u + 1u);

    for (std::uint32_t floor = 0; floor < floors.size(); ++floor)
    {
        // Position-bearing tracks stay eager and are kept in the main spatial
        // index. This supplemental index is only needed by lazy visual-only tracks.
        if ((combined_flags[floor] & position_mask) != 0u ||
            (combined_flags[floor] & scale_mask) == 0u ||
            !has_scale[floor])
            continue;

        const float geometry_radius = FloorGeometryRadius(floor);
        const float radius = geometry_radius * max_scale[floor];
        if (!std::isfinite(radius) || radius <= BaseCullMargin)
            continue;

        visual_transform_cull_radius_[floor] = radius;
        max_visual_transform_cull_radius_ = std::max(max_visual_transform_cull_radius_, radius);
        visual_transform_floors_.push_back(floor);

        const EeFloor& item = floors[floor];
        const int cell_x = FastFloor(item.x / CellSize);
        const int cell_y = FastFloor(item.y / CellSize);
        visual_transform_cells_[Key(cell_x, cell_y)].push_back(floor);
    }
}

float LevelScene::FloorGeometryRadius(std::uint32_t floor) const noexcept
{
    if (floor >= floors.size())
        return 0.0f;

    const std::uint32_t geometry_id = floors[floor].geometry_id;
    if (geometry_id >= geometries.size())
        return 0.0f;

    const EeGeometry& geometry = geometries[geometry_id];
    const std::uint64_t end = static_cast<std::uint64_t>(geometry.point_offset) + geometry.point_count;
    if (end > points.size())
        return 0.0f;

    float radius_squared = 0.0f;
    for (std::uint32_t i = 0; i < geometry.point_count; ++i)
    {
        const EePoint& point = points[geometry.point_offset + i];
        if (!std::isfinite(point.x) || !std::isfinite(point.y))
            continue;
        radius_squared = std::max(radius_squared, point.x * point.x + point.y * point.y);
    }
    return std::sqrt(radius_squared);
}

int LevelScene::FastFloor(float value) noexcept
{
    const int truncated = static_cast<int>(value);
    return value < static_cast<float>(truncated) ? truncated - 1 : truncated;
}

std::int64_t LevelScene::Key(int x, int y) noexcept
{
    const std::uint64_t high = static_cast<std::uint64_t>(static_cast<std::uint32_t>(x)) << 32;
    const std::uint64_t low = static_cast<std::uint32_t>(y);
    return static_cast<std::int64_t>(high | low);
}
}
