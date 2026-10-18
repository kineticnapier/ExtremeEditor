#include <filesystem>
#include <fstream>
#include <iostream>
#include <iterator>
#include <string>

namespace
{
std::string Read(const std::filesystem::path& path)
{
    std::ifstream file(path, std::ios::binary);
    return std::string(std::istreambuf_iterator<char>(file),
                       std::istreambuf_iterator<char>());
}
}

int main()
{
    const std::filesystem::path native = EE_NATIVE_SOURCE_DIR;
    const std::filesystem::path wpf = EE_WPF_SOURCE_DIR;
    const std::string hud = Read(native / "src" / "d2d_backend_editor.cpp");
    const std::string renderer = Read(native / "src" / "renderer.cpp");
    const std::string camera = Read(native / "src" / "d2d_backend_compat.cpp");
    const std::string shortcuts = Read(wpf / "MainWindow" / "MainWindow.AdoFaiKeybinds.cs");
    const std::string viewport = Read(wpf / "Native" / "NativeLevelViewport.Preparation.cs");

    int failures = 0;
    const auto require = [&](bool ok, const char* message)
    {
        if (!ok)
        {
            std::cerr << "FAIL: " << message << '\n';
            ++failures;
        }
    };
    require(hud.find("center_panel") == std::string::npos,
        "selected tile must not be covered by permanent green number panel");
    require(hud.find("for (const std::uint32_t index : visible_candidates_)") != std::string::npos,
        "number overlay must traverse only visible floor indices");
    require(hud.find("std::to_wstring(index)") != std::string::npos,
        "all floor numbers, not only selected index, are rendered");
    require(hud.find("zoom < 16.0f") != std::string::npos,
        "tiny illegible labels should be suppressed when zoomed far out");
    require(renderer.find(
        "backend.SetShowFloorNumbers(show_floor_numbers && !clean_view && !playback_active)") != std::string::npos,
        "clean view and playback must suppress floor numbers");
    require(camera.find("DrawFloorNumbers(*scene") != std::string::npos,
        "camera renderer must composite optional floor numbers");
    require(shortcuts.find("key == Key.F") != std::string::npos &&
            shortcuts.find("NativeViewport.SetShowFloorNumbers(_showAllFloorNumbers)") != std::string::npos,
        "Ctrl+F must toggle the native floor-number overlay");
    require(viewport.find("_session.SetShowFloorNumbers(_showFloorNumbers)") != std::string::npos,
        "label display state must survive renderer recreation");

    if (failures != 0)
        return 1;
    std::cout << "editor_floor_numbers_contract_tests: PASS\n";
    return 0;
}
