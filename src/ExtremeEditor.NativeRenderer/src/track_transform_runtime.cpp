#include "track_transform_runtime.h"

#include <algorithm>
#include <cmath>

namespace ee
{
namespace
{
double OutBounce(double t) noexcept
{
    constexpr double n1 = 7.5625;
    constexpr double d1 = 2.75;
    if (t < 1.0 / d1) return n1 * t * t;
    if (t < 2.0 / d1)
    {
        t -= 1.5 / d1;
        return n1 * t * t + 0.75;
    }
    if (t < 2.5 / d1)
    {
        t -= 2.25 / d1;
        return n1 * t * t + 0.9375;
    }
    t -= 2.625 / d1;
    return n1 * t * t + 0.984375;
}
}

void TrackTransformRuntime::ResetBase(const std::vector<EeFloor>& floors) noexcept
{
    std::lock_guard lock(mutex_);
    base_floors_ = floors;
    tracks_.clear();
    active_ = false;
    playing_ = false;
    has_last_update_chart_time_ = false;
    last_update_chart_time_ = 0.0;
    anchor_chart_time_ = 0.0;
    chart_rate_ = 1.0;
    anchor_steady_ = std::chrono::steady_clock::now();
}

bool TrackTransformRuntime::SetTimeline(
    const EeTrackTransformEvent* events,
    std::uint32_t event_count) noexcept
{
    if (event_count > 0 && events == nullptr)
        return false;

    try
    {
        std::vector<EeTrackTransformEvent> ordered;
        ordered.reserve(event_count);
        for (std::uint32_t i = 0; i < event_count; ++i)
        {
            const EeTrackTransformEvent& item = events[i];
            if (item.floor < 0 || static_cast<std::size_t>(item.floor) >= base_floors_.size())
                continue;
            ordered.push_back(item);
        }

        std::stable_sort(
            ordered.begin(), ordered.end(),
            [](const EeTrackTransformEvent& a, const EeTrackTransformEvent& b)
            {
                if (a.floor != b.floor)
                    return a.floor < b.floor;
                return a.start_time < b.start_time;
            });

        std::vector<FloorTrack> next;
        next.reserve(ordered.size());
        for (const EeTrackTransformEvent& item : ordered)
        {
            if (next.empty() || next.back().floor != item.floor)
                next.push_back(FloorTrack{item.floor, {}});
            next.back().events.push_back(item);
        }

        std::stable_sort(
            next.begin(), next.end(),
            [](const FloorTrack& a, const FloorTrack& b)
            {
                const double a_start = a.events.empty() ? 0.0 : a.events.front().start_time;
                const double b_start = b.events.empty() ? 0.0 : b.events.front().start_time;
                return a_start < b_start;
            });

        std::lock_guard lock(mutex_);
        tracks_ = std::move(next);
        has_last_update_chart_time_ = false;
        last_update_chart_time_ = 0.0;
        return true;
    }
    catch (...)
    {
        return false;
    }
}

void TrackTransformRuntime::SetPlaybackAnchor(
    double chart_time,
    double chart_rate,
    std::uint32_t flags) noexcept
{
    std::lock_guard lock(mutex_);
    active_ = (flags & EE_PLAYBACK_FLAG_ACTIVE) != 0u;
    playing_ = active_ && (flags & EE_PLAYBACK_FLAG_PLAYING) != 0u;
    anchor_chart_time_ = std::isfinite(chart_time) ? chart_time : 0.0;
    chart_rate_ = std::isfinite(chart_rate) && chart_rate > 0.0 ? chart_rate : 1.0;
    anchor_steady_ = std::chrono::steady_clock::now();
}

void TrackTransformRuntime::Restore(std::vector<EeFloor>& floors, CellMap& cells) noexcept
{
    std::lock_guard lock(mutex_);
    if (base_floors_.size() != floors.size())
        return;

    for (const FloorTrack& track : tracks_)
    {
        if (track.floor < 0 || static_cast<std::size_t>(track.floor) >= floors.size())
            continue;
        const std::uint32_t floor = static_cast<std::uint32_t>(track.floor);
        const EeFloor& old = floors[floor];
        const EeFloor& base = base_floors_[floor];
        ReindexFloor(floor, old.x, old.y, base.x, base.y, cells);
        floors[floor] = base;
    }
    has_last_update_chart_time_ = false;
}

void TrackTransformRuntime::Update(std::vector<EeFloor>& floors, CellMap& cells) noexcept
{
    std::lock_guard lock(mutex_);
    if (base_floors_.size() != floors.size())
        return;

    if (!active_ || tracks_.empty())
    {
        for (const FloorTrack& track : tracks_)
        {
            if (track.floor < 0 || static_cast<std::size_t>(track.floor) >= floors.size())
                continue;
            const std::uint32_t floor = static_cast<std::uint32_t>(track.floor);
            const EeFloor& old = floors[floor];
            const EeFloor& base = base_floors_[floor];
            ReindexFloor(floor, old.x, old.y, base.x, base.y, cells);
            floors[floor] = base;
        }
        has_last_update_chart_time_ = false;
        return;
    }

    const double chart_time = CurrentChartTime();
    const bool rewound = has_last_update_chart_time_ && chart_time < last_update_chart_time_;

    for (const FloorTrack& track : tracks_)
    {
        if (track.floor < 0 || static_cast<std::size_t>(track.floor) >= floors.size())
            continue;

        const bool started = !track.events.empty() && track.events.front().start_time <= chart_time;
        if (!started)
        {
            if (!rewound)
                break;

            const std::uint32_t floor_index = static_cast<std::uint32_t>(track.floor);
            const EeFloor old = floors[floor_index];
            const EeFloor& base = base_floors_[floor_index];
            ReindexFloor(floor_index, old.x, old.y, base.x, base.y, cells);
            floors[floor_index] = base;
            continue;
        }

        const std::uint32_t floor_index = static_cast<std::uint32_t>(track.floor);
        const EeFloor& base = base_floors_[floor_index];
        const EeFloor old = floors[floor_index];
        EeFloor resolved = base;

        const auto upper = std::upper_bound(
            track.events.begin(), track.events.end(), chart_time,
            [](double value, const EeTrackTransformEvent& item)
            {
                return value < item.start_time;
            });

        const EeTrackTransformEvent* x_event = nullptr;
        const EeTrackTransformEvent* y_event = nullptr;
        const EeTrackTransformEvent* rotation_event = nullptr;
        const EeTrackTransformEvent* scale_x_event = nullptr;
        const EeTrackTransformEvent* scale_y_event = nullptr;
        const EeTrackTransformEvent* opacity_event = nullptr;

        auto it = upper;
        while (it != track.events.begin() &&
               (x_event == nullptr || y_event == nullptr || rotation_event == nullptr ||
                scale_x_event == nullptr || scale_y_event == nullptr || opacity_event == nullptr))
        {
            --it;
            const EeTrackTransformEvent& item = *it;
            if (x_event == nullptr && (item.flags & EE_TRACK_TRANSFORM_X) != 0u) x_event = &item;
            if (y_event == nullptr && (item.flags & EE_TRACK_TRANSFORM_Y) != 0u) y_event = &item;
            if (rotation_event == nullptr && (item.flags & EE_TRACK_TRANSFORM_ROTATION) != 0u) rotation_event = &item;
            if (scale_x_event == nullptr && (item.flags & EE_TRACK_TRANSFORM_SCALE_X) != 0u) scale_x_event = &item;
            if (scale_y_event == nullptr && (item.flags & EE_TRACK_TRANSFORM_SCALE_Y) != 0u) scale_y_event = &item;
            if (opacity_event == nullptr && (item.flags & EE_TRACK_TRANSFORM_OPACITY) != 0u) opacity_event = &item;
        }

        if (x_event != nullptr)
            resolved.x = Evaluate(x_event->start_x, x_event->target_x, *x_event, chart_time);
        if (y_event != nullptr)
            resolved.y = Evaluate(y_event->start_y, y_event->target_y, *y_event, chart_time);
        if (rotation_event != nullptr)
        {
            const float rotation_offset = Evaluate(
                rotation_event->start_rotation,
                rotation_event->target_rotation,
                *rotation_event,
                chart_time);
            resolved.entry_angle = base.entry_angle + rotation_offset;
            resolved.icon_angle = base.icon_angle + rotation_offset;
        }
        if (scale_x_event != nullptr)
            resolved.transform_scale_x = Evaluate(
                scale_x_event->start_scale_x,
                scale_x_event->target_scale_x,
                *scale_x_event,
                chart_time);
        if (scale_y_event != nullptr)
            resolved.transform_scale_y = Evaluate(
                scale_y_event->start_scale_y,
                scale_y_event->target_scale_y,
                *scale_y_event,
                chart_time);
        if (opacity_event != nullptr)
            resolved.transform_opacity = Evaluate(
                opacity_event->start_opacity,
                opacity_event->target_opacity,
                *opacity_event,
                chart_time);

        resolved.track_transform_flags |= EE_TRACK_TRANSFORM_ENABLED;
        ReindexFloor(floor_index, old.x, old.y, resolved.x, resolved.y, cells);
        floors[floor_index] = resolved;
    }

    last_update_chart_time_ = chart_time;
    has_last_update_chart_time_ = true;
}

double TrackTransformRuntime::CurrentChartTime() const noexcept
{
    double result = anchor_chart_time_;
    if (active_ && playing_)
    {
        result += std::chrono::duration<double>(
            std::chrono::steady_clock::now() - anchor_steady_).count() * chart_rate_;
    }
    return result;
}

float TrackTransformRuntime::Evaluate(
    float start,
    float target,
    const EeTrackTransformEvent& item,
    double chart_time) noexcept
{
    const double progress = item.duration_seconds <= 1e-9
        ? 1.0
        : std::clamp((chart_time - item.start_time) / item.duration_seconds, 0.0, 1.0);
    const float t = ApplyEase(item.ease, progress);
    return start + (target - start) * t;
}

int TrackTransformRuntime::FastFloor(float value) noexcept
{
    const int truncated = static_cast<int>(value);
    return value < static_cast<float>(truncated) ? truncated - 1 : truncated;
}

std::int64_t TrackTransformRuntime::CellKey(float x, float y) noexcept
{
    const int cell_x = FastFloor(x / CellSize);
    const int cell_y = FastFloor(y / CellSize);
    const std::uint64_t high = static_cast<std::uint64_t>(static_cast<std::uint32_t>(cell_x)) << 32;
    const std::uint64_t low = static_cast<std::uint32_t>(cell_y);
    return static_cast<std::int64_t>(high | low);
}

void TrackTransformRuntime::ReindexFloor(
    std::uint32_t floor,
    float old_x,
    float old_y,
    float new_x,
    float new_y,
    CellMap& cells) noexcept
{
    const std::int64_t old_key = CellKey(old_x, old_y);
    const std::int64_t new_key = CellKey(new_x, new_y);
    if (old_key == new_key)
        return;

    const auto old_found = cells.find(old_key);
    if (old_found != cells.end())
    {
        auto& values = old_found->second;
        values.erase(std::remove(values.begin(), values.end(), floor), values.end());
        if (values.empty())
            cells.erase(old_found);
    }
    cells[new_key].push_back(floor);
}

float TrackTransformRuntime::ApplyEase(std::uint32_t ease, double t) noexcept
{
    t = std::clamp(t, 0.0, 1.0);
    constexpr double pi = 3.14159265358979323846;
    constexpr double c1 = 1.70158;
    constexpr double c2 = c1 * 1.525;
    constexpr double c3 = c1 + 1.0;
    constexpr double c4 = 2.0 * pi / 3.0;
    constexpr double c5 = 2.0 * pi / 4.5;

    double result = t;
    switch (ease)
    {
    case EE_CAMERA_EASE_IN_SINE: result = 1.0 - std::cos(t * pi / 2.0); break;
    case EE_CAMERA_EASE_OUT_SINE: result = std::sin(t * pi / 2.0); break;
    case EE_CAMERA_EASE_IN_OUT_SINE: result = -(std::cos(pi * t) - 1.0) / 2.0; break;
    case EE_CAMERA_EASE_IN_QUAD: result = t * t; break;
    case EE_CAMERA_EASE_OUT_QUAD: result = 1.0 - (1.0 - t) * (1.0 - t); break;
    case EE_CAMERA_EASE_IN_OUT_QUAD: result = t < 0.5 ? 2.0 * t * t : 1.0 - std::pow(-2.0 * t + 2.0, 2.0) / 2.0; break;
    case EE_CAMERA_EASE_IN_CUBIC: result = t * t * t; break;
    case EE_CAMERA_EASE_OUT_CUBIC: result = 1.0 - std::pow(1.0 - t, 3.0); break;
    case EE_CAMERA_EASE_IN_OUT_CUBIC: result = t < 0.5 ? 4.0 * t * t * t : 1.0 - std::pow(-2.0 * t + 2.0, 3.0) / 2.0; break;
    case EE_CAMERA_EASE_IN_QUART: result = std::pow(t, 4.0); break;
    case EE_CAMERA_EASE_OUT_QUART: result = 1.0 - std::pow(1.0 - t, 4.0); break;
    case EE_CAMERA_EASE_IN_OUT_QUART: result = t < 0.5 ? 8.0 * std::pow(t, 4.0) : 1.0 - std::pow(-2.0 * t + 2.0, 4.0) / 2.0; break;
    case EE_CAMERA_EASE_IN_QUINT: result = std::pow(t, 5.0); break;
    case EE_CAMERA_EASE_OUT_QUINT: result = 1.0 - std::pow(1.0 - t, 5.0); break;
    case EE_CAMERA_EASE_IN_OUT_QUINT: result = t < 0.5 ? 16.0 * std::pow(t, 5.0) : 1.0 - std::pow(-2.0 * t + 2.0, 5.0) / 2.0; break;
    case EE_CAMERA_EASE_IN_EXPO: result = t <= 0.0 ? 0.0 : std::pow(2.0, 10.0 * t - 10.0); break;
    case EE_CAMERA_EASE_OUT_EXPO: result = t >= 1.0 ? 1.0 : 1.0 - std::pow(2.0, -10.0 * t); break;
    case EE_CAMERA_EASE_IN_OUT_EXPO: result = t <= 0.0 ? 0.0 : t >= 1.0 ? 1.0 : t < 0.5 ? std::pow(2.0, 20.0 * t - 10.0) / 2.0 : (2.0 - std::pow(2.0, -20.0 * t + 10.0)) / 2.0; break;
    case EE_CAMERA_EASE_IN_CIRC: result = 1.0 - std::sqrt(std::max(0.0, 1.0 - t * t)); break;
    case EE_CAMERA_EASE_OUT_CIRC: result = std::sqrt(std::max(0.0, 1.0 - std::pow(t - 1.0, 2.0))); break;
    case EE_CAMERA_EASE_IN_OUT_CIRC: result = t < 0.5 ? (1.0 - std::sqrt(std::max(0.0, 1.0 - std::pow(2.0 * t, 2.0)))) / 2.0 : (std::sqrt(std::max(0.0, 1.0 - std::pow(-2.0 * t + 2.0, 2.0))) + 1.0) / 2.0; break;
    case EE_CAMERA_EASE_IN_BACK: result = c3 * t * t * t - c1 * t * t; break;
    case EE_CAMERA_EASE_OUT_BACK: result = 1.0 + c3 * std::pow(t - 1.0, 3.0) + c1 * std::pow(t - 1.0, 2.0); break;
    case EE_CAMERA_EASE_IN_OUT_BACK: result = t < 0.5 ? std::pow(2.0 * t, 2.0) * ((c2 + 1.0) * 2.0 * t - c2) / 2.0 : (std::pow(2.0 * t - 2.0, 2.0) * ((c2 + 1.0) * (2.0 * t - 2.0) + c2) + 2.0) / 2.0; break;
    case EE_CAMERA_EASE_IN_ELASTIC: result = t <= 0.0 ? 0.0 : t >= 1.0 ? 1.0 : -std::pow(2.0, 10.0 * t - 10.0) * std::sin((t * 10.0 - 10.75) * c4); break;
    case EE_CAMERA_EASE_OUT_ELASTIC: result = t <= 0.0 ? 0.0 : t >= 1.0 ? 1.0 : std::pow(2.0, -10.0 * t) * std::sin((t * 10.0 - 0.75) * c4) + 1.0; break;
    case EE_CAMERA_EASE_IN_OUT_ELASTIC: result = t <= 0.0 ? 0.0 : t >= 1.0 ? 1.0 : t < 0.5 ? -(std::pow(2.0, 20.0 * t - 10.0) * std::sin((20.0 * t - 11.125) * c5)) / 2.0 : std::pow(2.0, -20.0 * t + 10.0) * std::sin((20.0 * t - 11.125) * c5) / 2.0 + 1.0; break;
    case EE_CAMERA_EASE_IN_BOUNCE: result = 1.0 - OutBounce(1.0 - t); break;
    case EE_CAMERA_EASE_OUT_BOUNCE: result = OutBounce(t); break;
    case EE_CAMERA_EASE_IN_OUT_BOUNCE: result = t < 0.5 ? (1.0 - OutBounce(1.0 - 2.0 * t)) / 2.0 : (1.0 + OutBounce(2.0 * t - 1.0)) / 2.0; break;
    default: break;
    }
    return static_cast<float>(result);
}
}
