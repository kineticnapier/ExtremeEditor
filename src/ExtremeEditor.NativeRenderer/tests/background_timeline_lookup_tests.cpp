#include "background_timeline_lookup.h"

#include <cmath>
#include <iostream>
#include <limits>
#include <string>
#include <vector>

namespace
{
struct CountedTime
{
    double value;
    inline static int comparisons = 0;

    friend bool operator<(double time, const CountedTime& other) noexcept
    {
        ++comparisons;
        return time < other.value;
    }
};

struct Event
{
    CountedTime start_time;
    int source_index;
    int state;
};

const Event* LinearReference(const std::vector<Event>* events, double time)
{
    const Event* result = nullptr;
    if (events == nullptr)
        return result;
    for (const Event& event : *events)
    {
        if (event.start_time.value > time)
            break;
        result = &event;
    }
    return result;
}
}

int main()
{
    int failures = 0;
    const auto check = [&failures](bool condition, const std::string& name)
    {
        if (!condition)
        {
            std::cerr << "FAIL: " << name << '\n';
            ++failures;
        }
    };

    const std::vector<Event> empty;
    check(ee::FindLastBackgroundEventAtOrBefore<Event>(nullptr, 0.0) == nullptr,
        "null timeline uses initial background");
    check(ee::FindLastBackgroundEventAtOrBefore(&empty, 0.0) == nullptr,
        "empty timeline uses initial background");

    const std::vector<Event> events = {
        {{0.0}, 1, 10}, {{1.0}, 1, 11}, {{1.0}, 2, 12}, {{3.0}, 1, 13}
    };
    for (const double time : {
             -std::numeric_limits<double>::infinity(), -1.0, 0.0,
             0.999, 1.0, 1.5, 3.0, 5.0,
             std::numeric_limits<double>::infinity(),
             std::numeric_limits<double>::quiet_NaN() })
    {
        check(ee::FindLastBackgroundEventAtOrBefore(&events, time) ==
                  LinearReference(&events, time),
            "seek and equal-time ordering at " + std::to_string(time));
    }
    check(ee::FindLastBackgroundEventAtOrBefore(&events, 1.0)->state == 12,
        "last equal-time source index wins");

    std::vector<Event> large;
    large.reserve(50000);
    for (int i = 0; i < 50000; ++i)
        large.push_back({{static_cast<double>(i)}, i, i});
    for (const double time : {-1.0, 0.0, 1.5, 25000.0, 49999.0, 50000.0})
    {
        CountedTime::comparisons = 0;
        const Event* actual = ee::FindLastBackgroundEventAtOrBefore(&large, time);
        const int comparisons = CountedTime::comparisons;
        check(actual == LinearReference(&large, time),
            "50k timeline matches linear reference");
        check(comparisons <= 17,
            "50k timeline requires logarithmic comparisons");
    }

    if (failures == 0)
        std::cout << "PASS: background timeline lookup parity and logarithmic cost\n";
    return failures == 0 ? 0 : 1;
}
