#pragma once

#include <cstdlib>
#include <cstring>

namespace ee
{
inline bool EnvironmentFlagEnabled(const char* name) noexcept
{
#if defined(_MSC_VER)
    char* value = nullptr;
    std::size_t length = 0u;
    if (_dupenv_s(&value, &length, name) != 0 || value == nullptr)
        return false;
    const bool enabled = std::strcmp(value, "1") == 0;
    std::free(value);
    return enabled;
#else
    const char* value = std::getenv(name);
    return value != nullptr && std::strcmp(value, "1") == 0;
#endif
}

inline bool DecorationDiagnosticsEnabled() noexcept
{
    static const bool enabled = EnvironmentFlagEnabled("EXTREMEEDITOR_DECORATION_DIAGNOSTICS");
    return enabled;
}

inline bool DecorationVerboseDiagnosticsEnabled() noexcept
{
    static const bool enabled = EnvironmentFlagEnabled("EXTREMEEDITOR_DECORATION_VERBOSE_DIAGNOSTICS");
    return enabled;
}

inline bool NativeUploadDiagnosticsEnabled() noexcept
{
    static const bool enabled = EnvironmentFlagEnabled("EXTREMEEDITOR_NATIVE_UPLOAD_DIAGNOSTICS");
    return enabled;
}
}
