#pragma once

#include "extreme_editor_renderer.h"

#include <chrono>
#include <cstdint>
#include <mutex>
#include <vector>

namespace ee
{
class TrackTransformRuntime
{
public:
    void ResetBase(const std::vector<EeFloor>& floors) noexcept;
    bool SetTimeline(const EeTrackTransformEvent* events, std::uint32_t event_count) noexcept;
    void SetPlaybackAnchor(double chart_time, double chart_rate, std::uint32_t flags) noexcept;
    void Restore(std::vector<EeFloor>& floors) noexcept;
    void Update(std::vector<EeFloor>& floors) noexcept;

private:
    struct FloorTrack
    {
        std::int32_t floor = -1;
        std::vector<EeTrackTransformEvent> events;
    };

    double CurrentChartTime() const noexcept;
    static float ApplyEase(std::uint32_t ease, double progress) noexcept;
    static float Evaluate(float start, float target, const EeTrackTransformEvent& item, double chart_time) noexcept;

    mutable std::mutex mutex_;
    std::vector<EeFloor> base_floors_;
    std::vector<FloorTrack> tracks_;
    bool active_ = false;
    bool playing_ = false;
    double anchor_chart_time_ = 0.0;
    double chart_rate_ = 1.0;
    std::chrono::steady_clock::time_point anchor_steady_{};
};
}
