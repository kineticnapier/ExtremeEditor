#pragma once

#include <array>
#include <cstddef>
#include <limits>
#include <vector>

namespace ee
{
// Built once on upload. Each entry records the most recent event affecting
// X, Y, rotation and zoom, respectively.
class CameraComponentLookup
{
public:
    using Indices = std::array<std::size_t, 4>;
    static constexpr std::size_t Missing = std::numeric_limits<std::size_t>::max();

    template <typename Event>
    static CameraComponentLookup Build(
        const std::vector<Event>& events,
        const std::array<unsigned int, 4>& apply_flags)
    {
        CameraComponentLookup lookup;
        lookup.last_.reserve(events.size());
        Indices last{Missing, Missing, Missing, Missing};
        for (std::size_t i = 0; i < events.size(); ++i)
        {
            for (std::size_t component = 0; component < last.size(); ++component)
            {
                if ((events[i].flags & apply_flags[component]) != 0u)
                    last[component] = i;
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
