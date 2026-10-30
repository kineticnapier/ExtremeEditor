#include "camera_component_lookup.h"

#include <algorithm>
#include <array>
#include <cstdlib>
#include <cstddef>
#include <iostream>
#include <vector>

namespace
{
constexpr std::array<unsigned int, 4> Flags{0x100u, 0x200u, 0x400u, 0x800u};
struct Event
{
    double start_time;
    unsigned int flags;
};

void Require(bool condition, const char* message)
{
    if (condition)
        return;
    std::cerr << "FAIL: " << message << '\n';
    std::exit(1);
}

void Verify(const std::vector<Event>& events)
{
    const auto lookup = ee::CameraComponentLookup::Build(events, Flags);
    Require(lookup.Count() == events.size(), "lookup size");
    for (std::size_t upper = 0; upper <= events.size(); ++upper)
    {
        auto expected = ee::CameraComponentLookup::Indices{
            ee::CameraComponentLookup::Missing, ee::CameraComponentLookup::Missing,
            ee::CameraComponentLookup::Missing, ee::CameraComponentLookup::Missing};
        for (std::size_t i = 0; i < upper; ++i)
            for (std::size_t c = 0; c < Flags.size(); ++c)
                if ((events[i].flags & Flags[c]) != 0u)
                    expected[c] = i;
        Require(lookup.At(upper) == expected, "indexed result differs from reference scan");
    }
    Require(lookup.At(events.size() + 1) == lookup.At(0), "out-of-range lookup");
}
}

int main()
{
    Verify({});
    Verify({{0.0, 0u}});
    Verify({{0.0, 0xf00u}, {1.0, 0x100u}, {1.0, 0x200u},
            {2.0, 0x400u}, {3.0, 0x800u}, {4.0, 0u}});
    std::vector<Event> events;
    events.reserve(10000);
    for (unsigned int i = 0; i < 10000; ++i)
    {
        const unsigned int flags = (i == 0) ? 0xf00u
            : (i % 97 == 0) ? 0x800u
            : (i % 43 == 0) ? 0x400u
            : (i % 3 == 0) ? 0x200u : 0x100u;
        events.push_back({static_cast<double>(i / 3), flags});
    }
    // Compare with a linear reference at every boundary, including ties.
    Verify(events);
    const auto lookup = ee::CameraComponentLookup::Build(events, Flags);
    for (double time : {-1.0, 0.0, 1.0, 20.0, 500.0, 3333.0, 99999.0})
    {
        const auto upper = std::upper_bound(events.begin(), events.end(), time,
            [](double value, const Event& item) { return value < item.start_time; });
        const auto last = lookup.At(static_cast<std::size_t>(upper - events.begin()));
        for (std::size_t c = 0; c < Flags.size(); ++c)
            if (last[c] != ee::CameraComponentLookup::Missing)
                Require(events[last[c]].start_time <= time && (events[last[c]].flags & Flags[c]), "seek/tie lookup");
    }
    std::cout << "PASS: camera component lookup matches backwards scan.\n";
}
