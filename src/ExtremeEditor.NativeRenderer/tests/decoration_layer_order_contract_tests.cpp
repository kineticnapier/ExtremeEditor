#include <filesystem>
#include <fstream>
#include <iostream>
#include <iterator>
#include <string>

// The GUI viewport (d2d_backend_compat.cpp) and the headless / video renderer
// (d2d_backend_offscreen.cpp) must compose a frame in the stock ADOFAI sorting
// layer order: Bg decorations, Floor, Planet, Default decorations, FgFlash.
namespace
{
std::string Read(const std::filesystem::path& path)
{
    std::ifstream stream(path, std::ios::binary);
    return std::string(
        std::istreambuf_iterator<char>(stream),
        std::istreambuf_iterator<char>());
}

std::size_t Find(const std::string& text, const char* token, std::size_t from = 0u)
{
    return text.find(token, from);
}
}

int main()
{
    const std::filesystem::path root = EE_NATIVE_SOURCE_DIR;
    int failures = 0;
    for (const char* file : {"d2d_backend_compat.cpp", "d2d_backend_offscreen.cpp"})
    {
        const std::string source = Read(root / "src" / file);
        int file_failures = 0;
        const auto fail = [&](const std::string& message)
        {
            std::cerr << "FAIL: " << file << ": " << message << '\n';
            ++file_failures;
            ++failures;
        };

        const std::size_t background = Find(source, "DecorationSortingLayer::Background");
        const std::size_t floors = Find(source, "floor_renderer_.DrawCamera(");
        const std::size_t planets = Find(source, "DrawPlaybackPlanetsCamera(");
        const std::size_t foreground = Find(source, "DecorationSortingLayer::Foreground");
        const std::size_t flash = Find(source, "DrawForegroundFlash()");
        if (background == std::string::npos)
            fail("background decorations are not drawn as their own pass");
        if (foreground == std::string::npos)
            fail("foreground decorations are not drawn as their own pass");
        if (floors == std::string::npos || planets == std::string::npos || flash == std::string::npos)
            fail("frame composition anchors are missing");
        if (file_failures != 0)
            continue;
        if (!(background < floors))
            fail("background decorations must be drawn before the tiles");
        if (!(floors < planets && planets < foreground))
            fail("foreground decorations must be drawn after tiles and planets");
        if (!(foreground < flash))
            fail("foreground flash must stay above foreground decorations");
    }
    return failures == 0 ? 0 : 1;
}
