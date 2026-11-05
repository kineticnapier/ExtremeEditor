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
#include <thread>
#include <vector>

namespace
{
constexpr std::uint32_t AssetId = 0xdec0u;

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
    _putenv_s("EXTREMEEDITOR_SHUTDOWN_DIAGNOSTICS", "1");
    const std::filesystem::path bitmap = WriteTestBitmap();
    if (bitmap.empty())
        return 3;

    const int repetitions = scenario == "repeat" ? 4 : 1;
    bool ok = true;
    for (int i = 0; i < repetitions && ok; ++i)
        ok = RunSession(scenario == "repeat" ? "blend" : scenario, bitmap);

    std::error_code error;
    std::filesystem::remove(bitmap, error);
    return ok ? 0 : 4;
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

    const DWORD wait = WaitForSingleObject(process.hProcess, 5000u);
    if (wait == WAIT_TIMEOUT)
    {
        TerminateProcess(process.hProcess, 124u);
        WaitForSingleObject(process.hProcess, 2000u);
        std::string diagnostic;
        std::array<char, 1024> chunk{};
        DWORD bytes = 0u;
        while (ReadFile(output_read, chunk.data(), static_cast<DWORD>(chunk.size()), &bytes, nullptr) && bytes > 0u)
            diagnostic.append(chunk.data(), bytes);
        std::cerr << "RED: decoration shutdown child timed out for " << scenario
                  << " (render-thread join did not finish)\n" << diagnostic;
        CloseHandle(output_read);
        CloseHandle(process.hThread);
        CloseHandle(process.hProcess);
        return false;
    }

    std::string diagnostic;
    std::array<char, 1024> chunk{};
    DWORD bytes = 0u;
    while (ReadFile(output_read, chunk.data(), static_cast<DWORD>(chunk.size()), &bytes, nullptr) && bytes > 0u)
        diagnostic.append(chunk.data(), bytes);
    CloseHandle(output_read);

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
    const char* required[] =
    {
        "D2DBackend.decorationResources.release.begin",
        "D2DBackend.decorationResources.release.end",
        "RenderLoop.coUninitialize.end",
        "StopRenderThread.join.begin",
        "StopRenderThread.join.end"
    };
    for (const char* phase : required)
    {
        if (diagnostic.find(phase) == std::string::npos)
        {
            std::cerr << "Shutdown diagnostic phase missing for " << scenario
                      << ": " << phase << "\n" << diagnostic;
            return false;
        }
    }
    const std::size_t join_begin = diagnostic.find("StopRenderThread.join.begin");
    const std::size_t release_begin = diagnostic.find("D2DBackend.decorationResources.release.begin");
    const std::size_t release_end = diagnostic.find("D2DBackend.decorationResources.release.end");
    const std::size_t com_end = diagnostic.find("RenderLoop.coUninitialize.end");
    const std::size_t join_end = diagnostic.find("StopRenderThread.join.end");
    if (!(join_begin < release_begin && release_begin < release_end &&
          release_end < com_end && com_end < join_end))
    {
        std::cerr << "Shutdown diagnostic phase order regressed for " << scenario
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

    if (!VerifyHeadlessPixels())
        return 1;

    for (const char* scenario : {"none", "blend", "stop", "resize", "repeat", "switch", "many"})
    {
        if (!RunChild(executable, scenario))
            return 1;
    }

    std::cout << "decoration render-thread shutdown scenarios passed\n";
    return 0;
}
