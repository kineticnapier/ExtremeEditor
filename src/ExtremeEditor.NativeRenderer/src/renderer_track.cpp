#include "renderer.h"

namespace ee
{
bool Renderer::SetTrackTransformTimeline(
    const EeTrackTransformEvent* events,
    std::uint32_t event_count) noexcept
{
    if (event_count > 0 && events == nullptr)
        return false;

    std::lock_guard lock(scene_mutex_);
    if (!scene_)
        return event_count == 0;
    return scene_->SetTrackTransformTimeline(events, event_count);
}

bool Renderer::SetTrackVisualTimeline(
    const EeTrackVisualEvent* events,
    std::uint32_t event_count) noexcept
{
    std::lock_guard lock(scene_mutex_);
    return scene_ != nullptr && scene_->SetTrackVisualTimeline(events, event_count);
}

bool Renderer::SetTrackAnimationTimeline(
    const EeTrackAnimationSegment* segments,
    std::uint32_t segment_count,
    const EeTrackAnimationTiming* timings,
    std::uint32_t timing_count) noexcept
{
    if ((segment_count > 0u && segments == nullptr) ||
        (timing_count > 0u && timings == nullptr))
        return false;
    std::lock_guard lock(scene_mutex_);
    return scene_ != nullptr && scene_->SetTrackAnimationTimeline(
        segments, segment_count, timings, timing_count);
}

void Renderer::SetTrackPlaybackAnchor(
    double chart_time,
    double chart_rate,
    std::uint32_t flags) noexcept
{
    std::lock_guard lock(scene_mutex_);
    if (scene_)
        scene_->SetTrackPlaybackAnchor(chart_time, chart_rate, flags);
}
}
