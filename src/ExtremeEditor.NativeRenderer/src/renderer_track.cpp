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
