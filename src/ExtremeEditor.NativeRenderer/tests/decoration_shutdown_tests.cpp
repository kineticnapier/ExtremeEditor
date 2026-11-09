#include "decoration_blend.h"
#include "extreme_editor_headless.h"
#include "extreme_editor_renderer.h"

#include <windows.h>

#include <array>
#include <chrono>
#include <cstdint>
#include <cstdlib>
#include <filesystem>
#include <fstream>
#include <iostream>
#include <string>
#include <string_view>
#include <thread>
#include <vector>

namespace
{
constexpr std::uint32_t AssetId = 0xdec0u;
constexpr std::size_t PipeStressBytes = 128u * 1024u;

std::filesystem::path WriteTestBitmap()
{
    wchar_t directory[MAX_PATH]{};
    if (GetTempPathW(MAX_PATH, directory) == 0u)
        return {};
    wchar_t name[MAX_PATH]{};
    if (GetTempFileNameW(directory, L"ees", 0u, name) == 0u)
        return {};
    std::filesystem::path path(name);
    path.replace_extension(L".bmp");
    MoveFileExW(name, path.c_str(), MOVEFILE_REPLACE_EXISTING);

    BITMAPFILEHEADER file{};
    BITMAPINFOHEADER info{};
    constexpr std::array<std::uint8_t, 16> pixels =
    {
        0x20, 0x40, 0xff, 0xff, 0x20, 0x40, 0xff, 0xff,
        0x20, 0x40, 0xff, 0xff, 0x20, 0x40, 0xff, 0xff
    };
    file.bfType = 0x4d42u;
    file.bfOffBits = sizeof(file) + sizeof(info);
    file.bfSize = file.bfOffBits + static_cast<DWORD>(pixels.size());
    info.biSize = sizeof(info);
    info.biWidth = 2;
    info.biHeight = 2;
    info.biPlanes = 1;
    info.biBitCount = 32;
    info.biCompression = BI_RGB;
    info.biSizeImage = static_cast<DWORD>(pixels.size());

    std::ofstream output(path, std::ios::binary | std::ios::trunc);
    output.write(reinterpret_cast<const char*>(&file), sizeof(file));
    output.write(reinterpret_cast<const char*>(&info), sizeof(info));
    output.write(reinterpret_cast<const char*>(pixels.data()), pixels.size());
    return output ? path : std::filesystem::path{};
}

bool UploadScene(EeRendererHandle renderer)
{
    constexpr EePoint points[] =
    {
        {-0.5f, -0.5f}, {0.5f, -0.5f}, {0.5f, 0.5f}, {-0.5f, 0.5f}
    };
    EeGeometry geometry{};
    geometry.point_count = 4u;
    EeFloor floor{};
    floor.geometry_id = 0u;
    floor.icon_id = EE_ICON_NONE;
    floor.track_primary_color = 0xffffffffu;
    floor.track_secondary_color = 0xffffffffu;
    floor.transform_scale_x = 1.0f;
    floor.transform_scale_y = 1.0f;
    floor.transform_opacity = 1.0f;
    floor.track_extend_anim = 1.0f;
    return ee_renderer_set_level(
        renderer, &floor, 1u, &geometry, 1u, points, 4u,
        -1.0f, -1.0f, 1.0f, 1.0f) == EE_OK;
}

EeStaticDecoration MakeDecoration(std::uint32_t blend, int index)
{
    EeStaticDecoration result{};
    result.source_index = index;
    result.floor = 0;
    result.asset_id = AssetId;
    result.relative_mode = EE_DECORATION_RELATIVE_GLOBAL;
    result.position_x = static_cast<float>(index % 8) * 0.03f;
    result.position_y = static_cast<float>(index / 8) * 0.03f;
    result.scale_x = 1.0f;
    result.scale_y = 1.0f;
    result.scale_multiplier = 1.0f;
    result.color = 0xffffffffu;
    result.opacity = 1.0f;
    result.flags = EE_DECORATION_VISIBLE | blend;
    return result;
}

bool RunSession(const std::string& scenario, const std::filesystem::path& bitmap)
{
    HWND parent = CreateWindowExW(
        0, L"STATIC", L"DecorationShutdownTest", WS_OVERLAPPED,
        0, 0, 320, 180, nullptr, nullptr, GetModuleHandleW(nullptr), nullptr);
    if (!parent)
        return false;

    EeRendererCreateInfo info{};
    info.struct_size = sizeof(info);
    info.width = 320u;
    info.height = 180u;
    EeRendererHandle renderer = nullptr;
    bool ok = ee_renderer_create(parent, &info, &renderer) == EE_OK && renderer != nullptr;
    if (ok)
        ok = UploadScene(renderer);
    if (ok)
        ok = ee_renderer_set_decoration_asset(renderer, AssetId, bitmap.c_str()) == EE_OK;

    std::vector<EeStaticDecoration> decorations;
    if (scenario == "none")
        decorations.push_back(MakeDecoration(ee::kDecorationBlendNone, 0));
    else if (scenario == "many")
    {
        decorations.reserve(128u);
        for (int i = 0; i < 128; ++i)
            decorations.push_back(MakeDecoration(ee::kDecorationBlendLinearDodge, i));
    }
    else
        decorations.push_back(MakeDecoration(ee::kDecorationBlendLinearDodge, 0));

    if (ok)
        ok = ee_renderer_set_static_decorations(
            renderer, decorations.data(), static_cast<std::uint32_t>(decorations.size())) == EE_OK;
    if (ok && scenario == "stop")
        ee_renderer_set_playback_anchor(renderer, 0.0, 1.0, EE_PLAYBACK_FLAG_ACTIVE | EE_PLAYBACK_FLAG_PLAYING);

    std::this_thread::sleep_for(std::chrono::milliseconds(300));

    if (ok && scenario == "resize")
    {
        ee_renderer_resize(renderer, 640u, 360u);
        std::this_thread::sleep_for(std::chrono::milliseconds(200));
        ee_renderer_resize(renderer, 320u, 180u);
        std::this_thread::sleep_for(std::chrono::milliseconds(200));
    }
    if (ok && scenario == "switch")
    {
        decorations[0] = MakeDecoration(ee::kDecorationBlendNone, 0);
        ok = ee_renderer_set_static_decorations(renderer, decorations.data(), 1u) == EE_OK;
        std::this_thread::sleep_for(std::chrono::milliseconds(200));
        decorations[0] = MakeDecoration(ee::kDecorationBlendLinearDodge, 0);
        ok = ok && ee_renderer_set_static_decorations(renderer, decorations.data(), 1u) == EE_OK;
        std::this_thread::sleep_for(std::chrono::milliseconds(200));
    }
    if (ok && scenario == "stop")
    {
        ee_renderer_set_playback_anchor(renderer, 0.5, 1.0, EE_PLAYBACK_FLAG_ACTIVE);
        std::this_thread::sleep_for(std::chrono::milliseconds(100));
    }

    if (renderer)
        ee_renderer_destroy(renderer);
    DestroyWindow(parent);
    return ok;
}

bool VerifyHeadlessPixels()
{
    const std::filesystem::path bitmap = WriteTestBitmap();
    if (bitmap.empty())
        return false;

    EeRendererCreateInfo info{};
    info.struct_size = sizeof(info);
    info.width = 64u;
    info.height = 64u;
    EeRendererHandle renderer = nullptr;
    bool ok = ee_renderer_create_headless(&info, &renderer) == EE_OK && renderer != nullptr;
    if (ok)
        ok = UploadScene(renderer);
    if (ok)
        ok = ee_renderer_set_decoration_asset(renderer, AssetId, bitmap.c_str()) == EE_OK;
    if (ok)
        ok = ee_renderer_set_editor_camera(renderer, 0.0f, 0.0f, 32.0f) == EE_OK;

    EeStaticDecoration decoration = MakeDecoration(ee::kDecorationBlendNone, 0);
    decoration.scale_x = 20.0f;
    decoration.scale_y = 20.0f;
    std::vector<std::uint8_t> normal(64u * 64u * 3u);
    std::vector<std::uint8_t> blended(normal.size());
    std::vector<std::uint8_t> normal_again(normal.size());
    const auto render = [&](std::vector<std::uint8_t>& output)
    {
        return ee_renderer_render_rgb(
            renderer, 0.0, 0.0, output.data(),
            static_cast<std::uint32_t>(output.size()), 64u * 3u) == EE_OK;
    };

    if (ok)
        ok = ee_renderer_set_static_decorations(renderer, &decoration, 1u) == EE_OK && render(normal);
    decoration.flags = EE_DECORATION_VISIBLE | ee::kDecorationBlendLinearDodge;
    if (ok)
        ok = ee_renderer_set_static_decorations(renderer, &decoration, 1u) == EE_OK && render(blended);
    decoration.flags = EE_DECORATION_VISIBLE | ee::kDecorationBlendNone;
    if (ok)
        ok = ee_renderer_set_static_decorations(renderer, &decoration, 1u) == EE_OK && render(normal_again);

    if (ok && (normal == blended || normal_again != normal))
    {
        std::cerr << "Headless decoration blend pixels or cache reuse regressed\n";
        ok = false;
    }

    if (renderer)
        ee_renderer_destroy(renderer);
    std::error_code error;
    std::filesystem::remove(bitmap, error);
    return ok;
}

int ChildMain(const std::string& scenario)
{
    // Exercise the parent pipe reader with output larger than a Windows pipe
    // buffer. Waiting for process exit before draining the pipe deadlocks.
    if (scenario == "pipe-stress")
    {
        std::cerr << std::string(PipeStressBytes, 'x') << '\n';
        return 0;
    }

    _putenv_s("EXTREMEEDITOR_SHUTDOWN_DIAGNOSTICS", "1");
    const std::filesystem::path bitmap = WriteTestBitmap();
    if (bitmap.empty())
        return 3;

    const int repetitions = scenario == "repeat" ? 4 : 1;
    bool ok = true;
    for (int i = 0; i < repetitions && ok; ++i)
    {
        ok = RunSession(scenario == "repeat" ? "blend" : scenario, bitmap);
        if (ok)
            std::cerr << "[shutdown-test] session.complete index=" << i + 1 << '\n';
    }

    std::error_code error;
    std::filesystem::remove(bitmap, error);
    return ok ? 0 : 4;
}

bool HasValidShutdownPhaseOrder(std::string_view session)
{
    const auto phase = [&](std::string_view name) { return session.find(name); };
    const auto release_begin = phase("phase=D2DBackend.decorationResources.release.begin");
    const auto release_end = phase("phase=D2DBackend.decorationResources.release.end");
    const auto com_end = phase("phase=RenderLoop.coUninitialize.end");
    const auto join_begin = phase("phase=StopRenderThread.join.begin");
    const auto join_end = phase("phase=StopRenderThread.join.end");
    if (release_begin == std::string_view::npos ||
        release_end == std::string_view::npos ||
        com_end == std::string_view::npos ||
        join_begin == std::string_view::npos ||
        join_end == std::string_view::npos)
        return false;

    // The render thread may begin resource cleanup before the caller logs
    // join.begin. Only ordering within each thread and join completion is fixed.
    return release_begin < release_end &&
           release_end < com_end &&
           com_end < join_end &&
           join_begin < join_end;
}

bool VerifyShutdownPhaseOrderContract()
{
    constexpr std::string_view join_first =
        "phase=StopRenderThread.join.begin\n"
        "phase=D2DBackend.decorationResources.release.begin\n"
        "phase=D2DBackend.decorationResources.release.end\n"
        "phase=RenderLoop.coUninitialize.end\n"
        "phase=StopRenderThread.join.end\n";
    constexpr std::string_view release_first =
        "phase=D2DBackend.decorationResources.release.begin\n"
        "phase=D2DBackend.decorationResources.release.end\n"
        "phase=StopRenderThread.join.begin\n"
        "phase=RenderLoop.coUninitialize.end\n"
        "phase=StopRenderThread.join.end\n";
    constexpr std::string_view invalid =
        "phase=StopRenderThread.join.begin\n"
        "phase=D2DBackend.decorationResources.release.begin\n"
        "phase=StopRenderThread.join.end\n"
        "phase=D2DBackend.decorationResources.release.end\n"
        "phase=RenderLoop.coUninitialize.end\n";
    return HasValidShutdownPhaseOrder(join_first) &&
           HasValidShutdownPhaseOrder(release_first) &&
           !HasValidShutdownPhaseOrder(invalid);
}

bool RunChild(const wchar_t* executable, const char* scenario)
{
    std::wstring command = L"\"";
    command += executable;
    command += L"\" --child ";
    command.append(scenario, scenario + std::char_traits<char>::length(scenario));
    std::vector<wchar_t> writable(command.begin(), command.end());
    writable.push_back(L'\0');

    STARTUPINFOW startup{};
    startup.cb = sizeof(startup);
    SECURITY_ATTRIBUTES security{};
    security.nLength = sizeof(security);
    security.bInheritHandle = TRUE;
    HANDLE output_read = nullptr;
    HANDLE output_write = nullptr;
    if (!CreatePipe(&output_read, &output_write, &security, 0u))
        return false;
    SetHandleInformation(output_read, HANDLE_FLAG_INHERIT, 0u);
    startup.dwFlags = STARTF_USESTDHANDLES;
    startup.hStdInput = GetStdHandle(STD_INPUT_HANDLE);
    startup.hStdOutput = output_write;
    startup.hStdError = output_write;
    PROCESS_INFORMATION process{};
    if (!CreateProcessW(
            executable, writable.data(), nullptr, nullptr, TRUE,
            CREATE_NO_WINDOW, nullptr, nullptr, &startup, &process))
    {
        CloseHandle(output_read);
        CloseHandle(output_write);
        std::cerr << "Could not launch shutdown child for " << scenario << "\n";
        return false;
    }
    CloseHandle(output_write);

    // Drain stderr/stdout concurrently with process execution. A child that
    // writes more than the anonymous pipe's capacity blocks in WriteFile;
    // waiting for its exit before reading would then always time out.
    std::string diagnostic;
    std::thread output_reader([&]
    {
        std::array<char, 1024> chunk{};
        DWORD bytes = 0u;
        while (ReadFile(output_read, chunk.data(), static_cast<DWORD>(chunk.size()), &bytes, nullptr) && bytes > 0u)
            diagnostic.append(chunk.data(), bytes);
    });

    const DWORD wait = WaitForSingleObject(process.hProcess, 5000u);
    if (wait != WAIT_OBJECT_0)
    {
        TerminateProcess(process.hProcess, 124u);
        WaitForSingleObject(process.hProcess, 2000u);
    }
    output_reader.join();
    CloseHandle(output_read);

    if (wait == WAIT_TIMEOUT)
    {
        std::cerr << "RED: decoration shutdown child timed out for " << scenario
                  << " (render-thread join did not finish)\n" << diagnostic;
        CloseHandle(process.hThread);
        CloseHandle(process.hProcess);
        return false;
    }

    DWORD exit_code = 0u;
    GetExitCodeProcess(process.hProcess, &exit_code);
    CloseHandle(process.hThread);
    CloseHandle(process.hProcess);
    if (wait != WAIT_OBJECT_0 || exit_code != 0u)
    {
        std::cerr << "Decoration shutdown child failed for " << scenario
                  << " with exit " << exit_code << "\n" << diagnostic;
        return false;
    }
    if (std::string_view(scenario) == "pipe-stress")
    {
        if (diagnostic.size() < PipeStressBytes ||
            diagnostic.substr(0, PipeStressBytes) != std::string(PipeStressBytes, 'x'))
        {
            std::cerr << "Shutdown child pipe output was truncated\n";
            return false;
        }
        return true;
    }
    constexpr std::string_view begin_marker = "phase=ee_renderer_destroy.begin";
    constexpr std::string_view end_marker = "phase=ee_renderer_destroy.end";
    const std::string_view phases(diagnostic);
    std::size_t offset = 0;
    int verified_sessions = 0;
    while (true)
    {
        const auto begin = phases.find(begin_marker, offset);
        if (begin == std::string_view::npos)
            break;
        const auto end = phases.find(end_marker, begin + begin_marker.size());
        if (end == std::string_view::npos ||
            !HasValidShutdownPhaseOrder(phases.substr(begin, end - begin)))
        {
            std::cerr << "Shutdown diagnostic phase order regressed for " << scenario
                      << " (session " << verified_sessions + 1 << ")\n" << diagnostic;
            return false;
        }
        offset = end + end_marker.size();
        ++verified_sessions;
    }
    // Production diagnostics intentionally log each phase only once per process.
    // Verify the first shutdown in detail, and use test-owned completion markers
    // to confirm that every subsequent session also completed successfully.
    const int expected_sessions = std::string_view(scenario) == "repeat" ? 4 : 1;
    constexpr std::string_view completed_marker = "[shutdown-test] session.complete index=";
    std::size_t marker_offset = 0;
    int completed_sessions = 0;
    while (true)
    {
        const auto at = phases.find(completed_marker, marker_offset);
        if (at == std::string_view::npos)
            break;
        ++completed_sessions;
        const std::string expected_index = std::to_string(completed_sessions);
        const auto index_start = at + completed_marker.size();
        const auto index_end = index_start + expected_index.size();
        // Windows text-mode stderr may translate LF into CRLF.
        if (phases.substr(index_start, expected_index.size()) != expected_index ||
            index_end >= phases.size() ||
            (phases[index_end] != '\r' && phases[index_end] != '\n'))
        {
            std::cerr << "Shutdown completion markers out of order for " << scenario
                      << "\n" << diagnostic;
            return false;
        }
        marker_offset = index_end + 1;
    }
    if (verified_sessions != 1 || completed_sessions != expected_sessions)
    {
        std::cerr << "Shutdown session count mismatch for " << scenario
                  << ": diagnostic sessions=" << verified_sessions
                  << ", completed sessions=" << completed_sessions
                  << ", expected completions=" << expected_sessions
                  << "\n" << diagnostic;
        return false;
    }
    return true;
}
}

int wmain(int argc, wchar_t** argv)
{
    if (argc == 3 && std::wstring_view(argv[1]) == L"--child")
    {
        std::string scenario;
        for (const wchar_t* value = argv[2]; *value != L'\0'; ++value)
            scenario.push_back(static_cast<char>(*value));
        return ChildMain(scenario);
    }

    wchar_t executable[MAX_PATH]{};
    if (GetModuleFileNameW(nullptr, executable, MAX_PATH) == 0u)
        return 2;

    if (!VerifyShutdownPhaseOrderContract() || !VerifyHeadlessPixels())
        return 1;

    if (!RunChild(executable, "pipe-stress"))
        return 1;

    for (const char* scenario : {"none", "blend", "stop", "resize", "repeat", "switch", "many"})
    {
        if (!RunChild(executable, scenario))
            return 1;
    }

    std::cout << "decoration render-thread shutdown scenarios passed\n";
    return 0;
}
