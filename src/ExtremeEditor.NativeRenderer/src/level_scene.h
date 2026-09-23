#pragma once

#include "extreme_editor_renderer.h"
#include "track_transform_runtime.h"

#include <chrono>
#include <cstdint>
#include <memory>
#include <mutex>
#include <unordered_map>
#include <vector>

namespace ee
{
struct LevelScene
{
    static std::shared_ptr<LevelScene> Create(
        const EeFloor* floors,
        std::uint32_t floor_count,
        const EeGeometry* geometries,
        std::uint32_t geometry_count,
        const EePoint* points,
        std::uint32_t point_count,
        float bounds_left,
        float bounds_top,
        float bounds_right,
        float bounds_bottom);

    static const LevelScene* CurrentRuntimeScene() noexcept;

    void Query(float left, float top, float right, float bottom, std::vector<std::uint32_t>& output) const;
    void AppendVisualTransformCandidates(
        float left,
        float top,
        float right,
        float bottom,
        std::vector<std::uint32_t>& output) const;
    bool SetTrackTransformTimeline(const EeTrackTransformEvent* events, std::uint32_t event_count) noexcept;
    void SetTrackPlaybackAnchor(double chart_time, double chart_rate, std::uint32_t flags) noexcept;
    void UpdateTrackTransforms() noexcept;
    void EvaluateVisibleTrackVisuals(const std::vector<std::uint32_t>& visible_floors) noexcept;
    TrackTransformUpdateMetrics TrackTransformMetrics() const noexcept;

    std::vector<EeFloor> floors;
    std::vector<EeGeometry> geometries;
    std::vector<EePoint> points;
    std::unordered_map<std::int64_t, std::vector<std::uint32_t>> cells;

    float bounds_left = 0.0f;
    float bounds_top = 0.0f;
    float bounds_right = 0.0f;
    float bounds_bottom = 0.0f;

private:
    static constexpr float CellSize = 32.0f;
    static constexpr float BaseCullMargin = 2.5f;
    static constexpr double PlaybackAnchorJitterToleranceSeconds = 0.050;
    static thread_local const LevelScene* current_runtime_scene_;

    void RebuildCells() noexcept;
    void RebuildVisualTransformCullIndex(
        const EeTrackTransformEvent* events,
        std::uint32_t event_count) noexcept;
    float FloorGeometryRadius(std::uint32_t floor) const noexcept;
    static int FastFloor(float value) noexcept;
    static std::int64_t Key(int x, int y) noexcept;

    TrackTransformRuntime track_transforms_;
    mutable std::mutex transform_mutex_;
    mutable std::mutex transform_metrics_mutex_;
    TrackTransformUpdateMetrics last_track_transform_metrics_{};
    std::unordered_map<std::int64_t, std::vector<std::uint32_t>> visual_transform_cells_;
    std::vector<std::uint32_t> visual_transform_floors_;
    std::vector<float> visual_transform_cull_radius_;
    float max_visual_transform_cull_radius_ = 0.0f;
    bool has_track_playback_anchor_ = false;
    bool track_playback_anchor_playing_ = false;
    double track_playback_anchor_chart_time_ = 0.0;
    double track_playback_anchor_rate_ = 1.0;
    std::chrono::steady_clock::time_point track_playback_anchor_steady_{};
};
}
