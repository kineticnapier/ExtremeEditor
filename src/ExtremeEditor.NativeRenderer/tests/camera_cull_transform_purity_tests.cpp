#include <fstream>
#include <iostream>
#include <sstream>
#include <string>

#ifndef EE_NATIVE_SOURCE_DIR
#error EE_NATIVE_SOURCE_DIR must be defined
#endif

int main()
{
    const std::string path = std::string(EE_NATIVE_SOURCE_DIR) + "/src/d2d_backend_camera.cpp";
    std::ifstream input(path, std::ios::binary);
    if (!input)
    {
        std::cerr << "RED setup failure: unable to open " << path << '\n';
        return 2;
    }

    std::ostringstream buffer;
    buffer << input.rdbuf();
    const std::string source = buffer.str();

    constexpr const char* beginMarker = "void D2DBackend::QueryVisibleFloorsCamera(";
    constexpr const char* endMarker = "void D2DBackend::DrawSceneOverlaysCamera(";

    const std::size_t begin = source.find(beginMarker);
    const std::size_t end = source.find(endMarker, begin);
    if (begin == std::string::npos || end == std::string::npos || end <= begin)
    {
        std::cerr << "RED setup failure: unable to isolate QueryVisibleFloorsCamera.\n";
        return 2;
    }

    const std::string functionBody = source.substr(begin, end - begin);
    if (functionBody.find("UpdateTrackTransforms") != std::string::npos)
    {
        std::cerr
            << "RED: QueryVisibleFloorsCamera must not mutate track transforms; "
            << "Renderer::RenderLoop is the single UpdateTrackTransforms owner.\n";
        return 1;
    }

    std::cout << "camera cull transform purity passed\n";
    return 0;
}
