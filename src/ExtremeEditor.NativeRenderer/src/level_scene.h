#pragma once

#include "decoration_assets.h"
#include "extreme_editor_renderer.h"
#include "track_transform_runtime.h"
#include "track_visual_runtime.h"

#include <chrono>
#include <atomic>
#include <cstdint>
#include <memory>
#include <mutex>
#include <limits>
#include <unordered_map>
#include <vector>

namespace ee
{
struct DecorationRenderState
{
    std::shared_ptr<const std::vector<EeStaticDecoration>> decorations;
    std::shared_ptr<const DecorationAssetTable> assets;
    std::uint64_t decorations_version = 0;
    std::uint64_t assets_version = 0;
};

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

    std::uint64_t generation = 0;

    void Query(float left, float top, float right, float bottom, std::vector<std::uint32_t>& output) const;
    void AppendVisualTransformCandidates(
        float left,
        float top,
        float right,
        float bottom,
        std::vector<std::uint32_t>& output) const;
    bool SetTrackTransformTimeline(const EeTrackTransformEvent* events, std::uint32_t event_count) noexcept;
    bool SetTrackVisualTimeline(const EeTrackVisualEvent* events, std::uint32_t event_count) noexcept;
    void SetTrackPlaybackAnchor(double chart_time, double chart_rate, std::uint32_t flags) noexcept;
    void UpdateTrackTransforms() noexcept;
    void UpdateTrackVisuals() noexcept;
    void EvaluateVisibleTrackVisuals(const std::vector<std::uint32_t>& visible_floors) noexcept;
    TrackTransformUpdateMetrics TrackTransformMetrics() const noexcept;

    bool SetStaticDecorations(const EeStaticDecoration* decorations, std::uint32_t decoration_count) noexcept;
    void ClearDecorationAssets() noexcept;
    bool SetDecorationAsset(std::uint32_t asset_id, const wchar_t* image_path) noexcept;
    DecorationRenderState GetDecorationRenderState() const noexcept;

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
    static std::atomic<std::uint64_t> next_generation_;

    void RebuildCells() noexcept;
    void RebuildVisualTransformCullIndex(
        const EeTrackTransformEvent* events,
        std::uint32_t event_count) noexcept;
    float FloorGeometryRadius(std::uint32_t floor) const noexcept;
    static int FastFloor(float value) noexcept;
    static std::int64_t Key(int x, int y) noexcept;

    TrackTransformRuntime track_transforms_;
    TrackVisualRuntime track_visuals_;
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

    mutable std::mutex decoration_mutex_;
    std::shared_ptr<const std::vector<EeStaticDecoration>> static_decorations_ =
        std::make_shared<std::vector<EeStaticDecoration>>();
    std::shared_ptr<const DecorationAssetTable> decoration_assets_ =
        std::make_shared<DecorationAssetTable>();
    std::uint64_t static_decorations_version_ = 0;
    std::uint64_t decoration_assets_version_ = 0;
    mutable std::uint64_t logged_decoration_handoff_version_ =
        std::numeric_limits<std::uint64_t>::max();
};
}
