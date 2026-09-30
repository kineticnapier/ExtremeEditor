#pragma once

#include "diagnostic_flags.h"

#include <windows.h>

#include <atomic>
#include <cstdint>
#include <cstdio>

namespace ee
{
enum class ShutdownDiagnosticPhase : std::uint64_t
{
    RendererDestroyEntry = 1ull << 0,
    RendererDestroyExit = 1ull << 1,
    RendererDestructorEntry = 1ull << 2,
    RendererDestructorExit = 1ull << 3,
    StopFlagSet = 1ull << 4,
    RenderThreadJoinEntry = 1ull << 5,
    RenderThreadJoinExit = 1ull << 6,
    ChildWindowDestroyEntry = 1ull << 7,
    ChildWindowDestroyExit = 1ull << 8,
    RenderLoopStopObserved = 1ull << 9,
    D2DBackendShutdownEntry = 1ull << 10,
    D2DBackendShutdownExit = 1ull << 11
};

inline std::atomic_uint64_t& ShutdownDiagnosticPhases() noexcept
{
    static std::atomic_uint64_t phases{0u};
    return phases;
}

inline void ShutdownDiagnosticLogOnce(
    ShutdownDiagnosticPhase phase,
    const char* name) noexcept
{
    if (!ShutdownDiagnosticsEnabled())
        return;

    const std::uint64_t bit = static_cast<std::uint64_t>(phase);
    if ((ShutdownDiagnosticPhases().fetch_or(bit, std::memory_order_relaxed) & bit) != 0u)
        return;

    std::fprintf(
        stderr,
        "[shutdown] monotonicMs=%llu nativeTid=%lu phase=%s\n",
        static_cast<unsigned long long>(GetTickCount64()),
        static_cast<unsigned long>(GetCurrentThreadId()),
        name);
    std::fflush(stderr);
}

inline thread_local bool ShutdownD2DDiagnosticsActive = false;
}
