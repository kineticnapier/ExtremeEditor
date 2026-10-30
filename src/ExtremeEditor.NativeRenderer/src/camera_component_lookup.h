#pragma once

#include <array>
#include <cstddef>
#include <cstdint>
#include <limits>
#include <stdexcept>
#include <vector>

namespace ee
{
// Built once on upload. Each entry records the most recent event affecting
// X, Y, rotation and zoom, respectively.
class CameraComponentLookup
{
public:
    // The native upload API caps the event count at uint32_t. Store four
    // 32-bit indices rather than four platform-sized indices per event.
    using Index = std::uint32_t;
    using Indices = std::array<Index, 4>;
    static constexpr Index Missing = std::numeric_limits<Index>::max();

    template <typename Event>
    static CameraComponentLookup Build(
        const std::vector<Event>& events,
        const std::array<unsigned int, 4>& apply_flags)
    {
        // Missing is reserved as a sentinel, so the last valid index must
        // remain strictly below it. Direct callers are checked as well.
        if (events.size() > static_cast<std::size_t>(Missing))
            throw std::length_error("Camera timeline exceeds 32-bit index range");

        CameraComponentLookup lookup;
        lookup.last_.reserve(events.size());
        Indices last{Missing, Missing, Missing, Missing};
        for (std::size_t i = 0; i < events.size(); ++i)
        {
            for (std::size_t component = 0; component < last.size(); ++component)
            {
                if ((events[i].flags & apply_flags[component]) != 0u)
                    last[component] = static_cast<Index>(i);
            }
            lookup.last_.push_back(last);
        }
        return lookup;
    }

    [[nodiscard]] Indices At(std::size_t upper_count) const noexcept
    {
        return upper_count > 0 && upper_count <= last_.size()
            ? last_[upper_count - 1]
            : Indices{Missing, Missing, Missing, Missing};
    }

    [[nodiscard]] std::size_t Count() const noexcept { return last_.size(); }

private:
    std::vector<Indices> last_;
};
}
