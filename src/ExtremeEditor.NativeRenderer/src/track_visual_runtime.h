#pragma once

#include "extreme_editor_renderer.h"

#include <chrono>
#include <cstdint>
#include <mutex>
#include <vector>

namespace ee
{
class TrackVisualRuntime
{
public:
    void ResetBase(const std::vector<EeFloor>& floors) noexcept;
    bool SetTimeline(const EeTrackVisualEvent* events, std::uint32_t event_count) noexcept;
    void SetPlaybackAnchor(double chart_time, double chart_rate, std::uint32_t flags) noexcept;
    void Update(std::vector<EeFloor>& floors) noexcept;
    void EvaluateAt(double chart_time, std::vector<EeFloor>& floors) noexcept;

private:
    struct FloorEvent
    {
        EeTrackVisualEvent value{};
        std::uint64_t order = 0;
    };
    struct FloorTrack
    {
        std::int32_t floor = -1;
        std::vector<FloorEvent> events;
    };
    struct RuntimeState
    {
        EeFloor value{};
        std::uint32_t start_primary = 0;
        std::uint32_t start_secondary = 0;
        std::uint32_t target_primary = 0;
        std::uint32_t target_secondary = 0;
        EeTrackVisualEvent tween{};
        bool tween_active = false;
        bool active_listed = false;
    };
    struct ScheduleEntry
    {
        double start_time = 0.0;
        std::size_t track = 0;
        std::size_t event = 0;
        std::int32_t source_index = -1;
        std::uint64_t order = 0;
    };

    double CurrentChartTime() const noexcept;
    void EvaluateAtLocked(double chart_time, std::vector<EeFloor>& floors) const noexcept;
    void RebuildAt(double chart_time, std::vector<EeFloor>& floors) noexcept;
    void ApplyScheduledEvent(const ScheduleEntry& entry, std::vector<EeFloor>& floors) noexcept;
    void WriteState(std::size_t track_index, double chart_time, std::vector<EeFloor>& floors) noexcept;
    static float Ease(std::uint32_t ease, double progress) noexcept;
    static std::uint32_t LerpColor(std::uint32_t from, std::uint32_t to, float progress) noexcept;

    mutable std::mutex mutex_;
    std::vector<EeFloor> base_floors_;
    std::vector<FloorTrack> tracks_;
    std::vector<RuntimeState> runtime_states_;
    std::vector<ScheduleEntry> schedule_;
    std::vector<std::size_t> active_tracks_;
    std::size_t next_schedule_ = 0;
    bool has_last_chart_time_ = false;
    double last_chart_time_ = 0.0;
    bool playing_ = false;
    double anchor_chart_time_ = 0.0;
    double chart_rate_ = 1.0;
    std::chrono::steady_clock::time_point anchor_steady_{};
};
}
