#include <filesystem>
#include <fstream>
#include <iostream>
#include <iterator>
#include <string>
#include <vector>

namespace
{
std::string Read(const std::filesystem::path& path)
{
    std::ifstream stream(path, std::ios::binary);
    return std::string(
        std::istreambuf_iterator<char>(stream),
        std::istreambuf_iterator<char>());
}
}

int main()
{
    const std::filesystem::path root = EE_NATIVE_SOURCE_DIR;
    const std::string header = Read(root / "include" / "extreme_editor_renderer.h");
    std::string sources;
    for (const auto& entry : std::filesystem::directory_iterator(root / "src"))
    {
        if (entry.path().extension() == ".cpp" || entry.path().extension() == ".h")
            sources += Read(entry.path());
    }

    int failures = 0;
    const auto require = [&failures](bool condition, const std::string& message)
    {
        if (!condition)
        {
            std::cerr << "FAIL: " << message << '\n';
            ++failures;
        }
    };

    for (const char* token : {
             "EeBackgroundState", "EeBackgroundEvent",
             "ee_renderer_set_background_timeline", "ee_renderer_set_background_asset",
             "ee_renderer_replace_background_assets",
             "EE_BACKGROUND_DISPLAY_FIT_TO_SCREEN", "EE_BACKGROUND_DISPLAY_UNSCALED",
             "EE_BACKGROUND_DISPLAY_TILED", "EE_BACKGROUND_IMAGE_SMOOTHING",
             "EE_BACKGROUND_LOCK_ROTATION", "EE_BACKGROUND_LOOP" })
    {
        require(header.find(token) != std::string::npos,
            std::string("native background ABI is missing ") + token);
    }

    for (const char* token : {
             "BackgroundRuntime", "DrawBackground", "BackgroundAssetCache",
             "ResolveBackgroundAtTime" })
    {
        require(sources.find(token) != std::string::npos,
            std::string("dedicated background runtime/render contract is missing ") + token);
    }

    for (const char* file : {
             "d2d_backend.cpp", "d2d_backend_compat.cpp", "d2d_backend_offscreen.cpp" })
    {
        const std::string render_path = Read(root / "src" / file);
        const std::size_t background = render_path.find("DrawBackground(");
        const std::size_t floors = render_path.find("floor_renderer_.Draw");
        require(background != std::string::npos && floors != std::string::npos && background < floors,
            std::string(file) + " does not draw the background before floors/grid");
    }
    require(sources.find("BackgroundAssetCache") != std::string::npos &&
            sources.find("SetBackgroundAsset") != std::string::npos &&
            sources.find("ReplaceBackgroundAssets") != std::string::npos,
        "background image decode/upload is not isolated to prepare-time asset registration");

    const std::string renderer = Read(root / "src" / "renderer.cpp");
    const std::size_t replace = renderer.find("Renderer::ReplaceBackgroundAssets(");
    const std::size_t replace_end = renderer.find("\nbool Renderer::", replace + 1u);
    require(replace != std::string::npos && replace_end != std::string::npos &&
            renderer.substr(replace, replace_end - replace).find("background_assets_ = std::move(next)") != std::string::npos,
        "#83 background assets must be built once and atomically replace the old map");

    return failures == 0 ? 0 : 1;
}
