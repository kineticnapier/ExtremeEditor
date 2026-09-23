#pragma once

#include "extreme_editor_renderer.h"

#include <chrono>
#include <cstddef>
#include <cstdint>
#include <mutex>
#include <unordered_map>
#include <vector>

namespace ee
{
struct TrackTransformUpdateMetrics
{
    double total_ms = 0.0;
    double clock_ms = 0.0;
    double evaluate_ms = 0.0;
    double apply_ms = 0.0;
    double spatial_remove_ms = 0.0;
    double spatial_insert_ms = 0.0;
    std::uint32_t total_track_count = 0;
    std::uint32_t active_track_count = 0;
    std::uint32_t admitted_count = 0;
    std::uint32_t finished_count = 0;
    std::uint64_t event_scan_count = 0;
    std::uint64_t max_event_scan_count = 0;
    std::uint32_t active_position_count = 0;
    std::uint32_t active_visual_only_count = 0;
    std::uint32_t active_single_event_count = 0;
};

struct TrackTransformActiveClassification
{
    std::uint32_t position_count = 0;
    std::uint32_t visual_only_count = 0;
    std::uint32_t single_event_count = 0;
};

class TrackTransformRuntime
{
public:
    using CellMap = std::unordered_map<std::int64_t, std::vector<std::uint32_t>>;

    void ResetBase(const std::vector<EeFloor>& floors) noexcept;
    bool SetTimeline(const EeTrackTransformEvent* events, std::uint32_t event_count) noexcept;
    void SetPlaybackAnchor(double chart_time, double chart_rate, std::uint32_t flags) noexcept;
    void Restore(std::vector<EeFloor>& floors, CellMap& cells) noexcept;
    TrackTransformUpdateMetrics Update(std::vector<EeFloor>& floors, CellMap& cells) noexcept;
    TrackTransformActiveClassification ActiveClassification() const noexcept;

private:
    struct FloorTrack
    {
        std::int32_t floor = -1;
        std::vector<EeTrackTransformEvent> events;
        double end_time = 0.0;
    };

    static constexpr float CellSize = 32.0f;

    double CurrentChartTime() const noexcept;
    void ResolveTrackAtTime(
        const FloorTrack& track,
        double chart_time,
        std::vector<EeFloor>& floors,
        CellMap& cells,
        TrackTransformUpdateMetrics* metrics) noexcept;
    void RebuildRuntimeState(
        double chart_time,
        std::vector<EeFloor>& floors,
        CellMap& cells,
        TrackTransformUpdateMetrics* metrics) noexcept;
    static float ApplyEase(std::uint32_t ease, double progress) noexcept;
    static float Evaluate(float start, float target, const EeTrackTransformEvent& item, double chart_time) noexcept;
    static int FastFloor(float value) noexcept;
    static std::int64_t CellKey(float x, float y) noexcept;
    static void ReindexFloor(
        std::uint32_t floor,
        float old_x,
        float old_y,
        float new_x,
        float new_y,
        CellMap& cells,
        TrackTransformUpdateMetrics* metrics = nullptr) noexcept;

    mutable std::mutex mutex_;
    std::vector<EeFloor> base_floors_;
    std::vector<FloorTrack> tracks_;
    std::vector<std::size_t> active_track_indices_;
    std::size_t next_track_index_ = 0;
    bool active_ = false;
    bool playing_ = false;
    bool has_last_update_chart_time_ = false;
    double last_update_chart_time_ = 0.0;
    double anchor_chart_time_ = 0.0;
    double chart_rate_ = 1.0;
    std::chrono::steady_clock::time_point anchor_steady_{};
};

inline TrackTransformActiveClassification TrackTransformRuntime::ActiveClassification() const noexcept
{
    std::lock_guard lock(mutex_);
    TrackTransformActiveClassification result{};
    constexpr std::uint32_t position_mask = EE_TRACK_TRANSFORM_X | EE_TRACK_TRANSFORM_Y;
    constexpr std::uint32_t visual_mask =
        EE_TRACK_TRANSFORM_ROTATION |
        EE_TRACK_TRANSFORM_SCALE_X |
        EE_TRACK_TRANSFORM_SCALE_Y |
        EE_TRACK_TRANSFORM_OPACITY;

    for (const std::size_t track_index : active_track_indices_)
    {
        if (track_index >= tracks_.size())
            continue;

        const FloorTrack& track = tracks_[track_index];
        std::uint32_t combined_flags = 0u;
        for (const EeTrackTransformEvent& item : track.events)
            combined_flags |= item.flags;

        if ((combined_flags & position_mask) != 0u)
            ++result.position_count;
        else if ((combined_flags & visual_mask) != 0u)
            ++result.visual_only_count;

        if (track.events.size() == 1u)
            ++result.single_event_count;
    }

    return result;
}
}
