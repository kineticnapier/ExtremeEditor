#include "track_visual_runtime.h"

#include <algorithm>
#include <cmath>
#include <unordered_map>

namespace ee
{
namespace
{
constexpr double Pi = 3.14159265358979323846;
}

void TrackVisualRuntime::ResetBase(const std::vector<EeFloor>& floors) noexcept
{
    std::lock_guard lock(mutex_);
    base_floors_ = floors;
    tracks_.clear();
    runtime_states_.clear();
    schedule_.clear();
    active_tracks_.clear();
    next_schedule_ = 0;
    has_last_chart_time_ = false;
    playing_ = false;
    anchor_chart_time_ = 0.0;
    chart_rate_ = 1.0;
    anchor_steady_ = std::chrono::steady_clock::now();
}

bool TrackVisualRuntime::SetTimeline(
    const EeTrackVisualEvent* events,
    std::uint32_t event_count) noexcept
{
    if (event_count > 0u && events == nullptr)
        return false;
    try
    {
        std::vector<FloorTrack> tracks;
        std::unordered_map<std::int32_t, std::size_t> by_floor;
        std::uint64_t order = 0u;
        for (std::uint32_t i = 0; i < event_count; ++i)
        {
            const EeTrackVisualEvent& item = events[i];
            const int start = std::max(0, std::min(item.start_floor, item.end_floor));
            const int end = std::min(
                static_cast<int>(base_floors_.size()) - 1,
                std::max(item.start_floor, item.end_floor));
            const std::uint64_t step = static_cast<std::uint64_t>(item.gap_length) + 1u;
            if (start > end || base_floors_.empty())
                continue;
            for (std::uint64_t floor = static_cast<std::uint64_t>(start);
                 floor <= static_cast<std::uint64_t>(end);
                 floor += step)
            {
                const std::int32_t resolved_floor = static_cast<std::int32_t>(floor);
                auto [found, inserted] = by_floor.emplace(resolved_floor, tracks.size());
                if (inserted)
                    tracks.push_back(FloorTrack{resolved_floor, {}});
                tracks[found->second].events.push_back(FloorEvent{item, order++});
            }
        }
        for (FloorTrack& track : tracks)
        {
            std::stable_sort(track.events.begin(), track.events.end(),
                [](const FloorEvent& a, const FloorEvent& b)
                {
                    if (a.value.start_time != b.value.start_time)
                        return a.value.start_time < b.value.start_time;
                    if (a.value.source_index != b.value.source_index)
                        return a.value.source_index < b.value.source_index;
                    return a.order < b.order;
                });
        }
        std::sort(tracks.begin(), tracks.end(),
            [](const FloorTrack& a, const FloorTrack& b) { return a.floor < b.floor; });
        std::vector<RuntimeState> states(tracks.size());
        std::vector<ScheduleEntry> schedule;
        for (std::size_t track_index = 0; track_index < tracks.size(); ++track_index)
        {
            const FloorTrack& track = tracks[track_index];
            states[track_index].value = base_floors_[static_cast<std::size_t>(track.floor)];
            for (std::size_t event_index = 0; event_index < track.events.size(); ++event_index)
            {
                const FloorEvent& item = track.events[event_index];
                schedule.push_back(ScheduleEntry{
                    item.value.start_time, track_index, event_index,
                    item.value.source_index, item.order});
            }
        }
        std::stable_sort(schedule.begin(), schedule.end(),
            [](const ScheduleEntry& a, const ScheduleEntry& b)
            {
                if (a.start_time != b.start_time) return a.start_time < b.start_time;
                if (a.source_index != b.source_index) return a.source_index < b.source_index;
                return a.order < b.order;
            });
        std::lock_guard lock(mutex_);
        tracks_ = std::move(tracks);
        runtime_states_ = std::move(states);
        schedule_ = std::move(schedule);
        active_tracks_.clear();
        next_schedule_ = 0;
        has_last_chart_time_ = false;
        return true;
    }
    catch (...)
    {
        return false;
    }
}

void TrackVisualRuntime::SetPlaybackAnchor(
    double chart_time,
    double chart_rate,
    std::uint32_t flags) noexcept
{
    std::lock_guard lock(mutex_);
    playing_ = (flags & EE_PLAYBACK_FLAG_ACTIVE) != 0u &&
        (flags & EE_PLAYBACK_FLAG_PLAYING) != 0u;
    anchor_chart_time_ = std::isfinite(chart_time) ? chart_time : 0.0;
    chart_rate_ = std::isfinite(chart_rate) && chart_rate > 0.0 ? chart_rate : 1.0;
    anchor_steady_ = std::chrono::steady_clock::now();
}

void TrackVisualRuntime::Update(std::vector<EeFloor>& floors) noexcept
{
    std::lock_guard lock(mutex_);
    const double chart_time = CurrentChartTime();
    if (!has_last_chart_time_ || chart_time < last_chart_time_)
    {
        RebuildAt(chart_time, floors);
    }
    else
    {
        while (next_schedule_ < schedule_.size() &&
               schedule_[next_schedule_].start_time <= chart_time)
        {
            ApplyScheduledEvent(schedule_[next_schedule_++], floors);
        }

        std::size_t write = 0;
        for (std::size_t track_index : active_tracks_)
        {
            if (track_index >= runtime_states_.size())
                continue;
            WriteState(track_index, chart_time, floors);
            RuntimeState& state = runtime_states_[track_index];
            if (state.tween_active)
                active_tracks_[write++] = track_index;
            else
                state.active_listed = false;
        }
        active_tracks_.resize(write);
    }
    last_chart_time_ = chart_time;
    has_last_chart_time_ = true;
}

void TrackVisualRuntime::EvaluateAt(double chart_time, std::vector<EeFloor>& floors) noexcept
{
    std::lock_guard lock(mutex_);
    EvaluateAtLocked(chart_time, floors);
}

void TrackVisualRuntime::RebuildAt(double chart_time, std::vector<EeFloor>& floors) noexcept
{
    active_tracks_.clear();
    next_schedule_ = 0;
    for (std::size_t i = 0; i < tracks_.size(); ++i)
    {
        RuntimeState& state = runtime_states_[i];
        state = RuntimeState{};
        state.value = base_floors_[static_cast<std::size_t>(tracks_[i].floor)];
        WriteState(i, chart_time, floors);
    }
    while (next_schedule_ < schedule_.size() && schedule_[next_schedule_].start_time <= chart_time)
        ApplyScheduledEvent(schedule_[next_schedule_++], floors);
    for (std::size_t track_index : active_tracks_)
        WriteState(track_index, chart_time, floors);
}

void TrackVisualRuntime::ApplyScheduledEvent(
    const ScheduleEntry& entry,
    std::vector<EeFloor>& floors) noexcept
{
    if (entry.track >= tracks_.size() || entry.track >= runtime_states_.size() ||
        entry.event >= tracks_[entry.track].events.size())
        return;
    RuntimeState& state = runtime_states_[entry.track];
    const EeTrackVisualEvent& item = tracks_[entry.track].events[entry.event].value;
    if (state.tween_active)
    {
        state.value.track_primary_color = state.target_primary;
        state.value.track_secondary_color = state.target_secondary;
    }
    state.start_primary = state.value.track_primary_color;
    state.start_secondary = state.value.track_secondary_color;
    const std::size_t floor = static_cast<std::size_t>(tracks_[entry.track].floor);
    if ((item.visual_flags & EE_TRACK_VISUAL_COLOR_TYPE_MASK) == 1u)
    {
        const bool secondary = ((static_cast<int>(floor) - item.start_floor) & 1) != 0;
        state.target_primary = secondary ? item.secondary_color : item.primary_color;
        state.target_secondary = state.target_primary;
    }
    else
    {
        state.target_primary = item.primary_color;
        state.target_secondary = item.secondary_color;
    }
    state.value.track_visual_flags = item.visual_flags;
    state.value.track_anim_duration = item.anim_duration;
    state.value.track_glow_intensity = item.glow_intensity;
    state.value.track_start_floor = item.start_floor;
    state.value.track_pulse_length = item.pulse_length;
    state.tween = item;
    state.tween_active = item.transition_duration > 0.0 && std::isfinite(item.transition_duration);
    if (!state.tween_active)
    {
        state.value.track_primary_color = state.target_primary;
        state.value.track_secondary_color = state.target_secondary;
    }
    else if (!state.active_listed)
    {
        state.active_listed = true;
        active_tracks_.push_back(entry.track);
    }
    WriteState(entry.track, item.start_time, floors);
}

void TrackVisualRuntime::WriteState(
    std::size_t track_index,
    double chart_time,
    std::vector<EeFloor>& floors) noexcept
{
    if (track_index >= tracks_.size() || track_index >= runtime_states_.size())
        return;
    const std::size_t floor = static_cast<std::size_t>(tracks_[track_index].floor);
    if (floor >= floors.size())
        return;
    RuntimeState& state = runtime_states_[track_index];
    if (state.tween_active)
    {
        const double raw = (chart_time - state.tween.start_time) / state.tween.transition_duration;
        const float progress = Ease(state.tween.ease, raw);
        state.value.track_primary_color = LerpColor(state.start_primary, state.target_primary, progress);
        state.value.track_secondary_color = LerpColor(state.start_secondary, state.target_secondary, progress);
        if (raw >= 1.0)
        {
            state.value.track_primary_color = state.target_primary;
            state.value.track_secondary_color = state.target_secondary;
            state.tween_active = false;
        }
    }
    floors[floor].track_primary_color = state.value.track_primary_color;
    floors[floor].track_secondary_color = state.value.track_secondary_color;
    floors[floor].track_visual_flags = state.value.track_visual_flags;
    floors[floor].track_anim_duration = state.value.track_anim_duration;
    floors[floor].track_glow_intensity = state.value.track_glow_intensity;
    floors[floor].track_start_floor = state.value.track_start_floor;
    floors[floor].track_pulse_length = state.value.track_pulse_length;
}

double TrackVisualRuntime::CurrentChartTime() const noexcept
{
    if (!playing_)
        return anchor_chart_time_;
    return anchor_chart_time_ +
        std::chrono::duration<double>(std::chrono::steady_clock::now() - anchor_steady_).count() * chart_rate_;
}

void TrackVisualRuntime::EvaluateAtLocked(double chart_time, std::vector<EeFloor>& floors) const noexcept
{
    if (base_floors_.size() != floors.size())
        return;

    for (const FloorTrack& track : tracks_)
    {
        if (track.floor < 0 || static_cast<std::size_t>(track.floor) >= floors.size())
            continue;
        const std::size_t floor = static_cast<std::size_t>(track.floor);
        const auto& events = track.events;

        EeFloor state = base_floors_[floor];
        std::uint32_t tween_start_primary = state.track_primary_color;
        std::uint32_t tween_start_secondary = state.track_secondary_color;
        std::uint32_t tween_target_primary = state.track_primary_color;
        std::uint32_t tween_target_secondary = state.track_secondary_color;
        const EeTrackVisualEvent* active_tween = nullptr;

        for (const FloorEvent& wrapped : events)
        {
            const EeTrackVisualEvent& item = wrapped.value;
            if (item.start_time > chart_time)
                break;

            if (active_tween != nullptr)
            {
                state.track_primary_color = tween_target_primary;
                state.track_secondary_color = tween_target_secondary;
            }

            tween_start_primary = state.track_primary_color;
            tween_start_secondary = state.track_secondary_color;
            const std::uint32_t color_type = item.visual_flags & EE_TRACK_VISUAL_COLOR_TYPE_MASK;
            if (color_type == 1u) // Stripes: resolved range start is the parity origin.
            {
                const bool secondary = ((static_cast<int>(floor) - item.start_floor) & 1) != 0;
                tween_target_primary = secondary ? item.secondary_color : item.primary_color;
                tween_target_secondary = tween_target_primary;
            }
            else
            {
                tween_target_primary = item.primary_color;
                tween_target_secondary = item.secondary_color;
            }

            state.track_visual_flags = item.visual_flags;
            state.track_anim_duration = item.anim_duration;
            state.track_glow_intensity = item.glow_intensity;
            state.track_start_floor = item.start_floor;
            state.track_pulse_length = item.pulse_length;

            if (!(item.transition_duration > 0.0) || !std::isfinite(item.transition_duration))
            {
                state.track_primary_color = tween_target_primary;
                state.track_secondary_color = tween_target_secondary;
                active_tween = nullptr;
            }
            else
            {
                active_tween = &item;
            }
        }

        if (active_tween != nullptr)
        {
            const double raw = (chart_time - active_tween->start_time) /
                active_tween->transition_duration;
            const float progress = Ease(active_tween->ease, raw);
            state.track_primary_color = LerpColor(tween_start_primary, tween_target_primary, progress);
            state.track_secondary_color = LerpColor(tween_start_secondary, tween_target_secondary, progress);
        }

        floors[floor].track_primary_color = state.track_primary_color;
        floors[floor].track_secondary_color = state.track_secondary_color;
        floors[floor].track_visual_flags = state.track_visual_flags;
        floors[floor].track_anim_duration = state.track_anim_duration;
        floors[floor].track_glow_intensity = state.track_glow_intensity;
        floors[floor].track_start_floor = state.track_start_floor;
        floors[floor].track_pulse_length = state.track_pulse_length;
    }
}

float TrackVisualRuntime::Ease(std::uint32_t ease, double progress) noexcept
{
    const double t = std::clamp(progress, 0.0, 1.0);
    switch (ease)
    {
        case 1u: return static_cast<float>(1.0 - std::cos(t * Pi * 0.5));
        case 2u: return static_cast<float>(std::sin(t * Pi * 0.5));
        case 3u: return static_cast<float>(-(std::cos(Pi * t) - 1.0) * 0.5);
        case 4u: return static_cast<float>(t * t);
        case 5u: return static_cast<float>(1.0 - (1.0 - t) * (1.0 - t));
        case 6u: return static_cast<float>(t < 0.5 ? 2.0 * t * t : 1.0 - std::pow(-2.0 * t + 2.0, 2.0) * 0.5);
        default: return static_cast<float>(t);
    }
}

std::uint32_t TrackVisualRuntime::LerpColor(
    std::uint32_t from,
    std::uint32_t to,
    float progress) noexcept
{
    std::uint32_t result = 0u;
    for (std::uint32_t shift = 0u; shift < 32u; shift += 8u)
    {
        const float a = static_cast<float>((from >> shift) & 0xffu);
        const float b = static_cast<float>((to >> shift) & 0xffu);
        const auto value = static_cast<std::uint32_t>(std::clamp(std::lround(a + (b - a) * progress), 0l, 255l));
        result |= value << shift;
    }
    return result;
}
}
