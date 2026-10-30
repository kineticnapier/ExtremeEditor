#pragma once

#include <algorithm>
#include <vector>

namespace ee
{
// The timeline is sorted by (start_time, source_index). upper_bound returns
// the first event after chart_time, so the preceding event is the last
// applicable one, including the final event among equal timestamps.
template <typename Event>
const Event* FindLastBackgroundEventAtOrBefore(
    const std::vector<Event>* events, double chart_time) noexcept
{
    if (events == nullptr || events->empty())
        return nullptr;

    const auto next = std::upper_bound(
        events->begin(), events->end(), chart_time,
        [](double time, const Event& event) {
            return time < event.start_time;
        });
    return next == events->begin() ? nullptr : &*(next - 1);
}
}
