#include <fstream>
#include <iostream>
#include <sstream>
#include <string>

#ifndef EE_NATIVE_SOURCE_DIR
#error EE_NATIVE_SOURCE_DIR must be defined
#endif

namespace
{
std::string ReadSource(const char* relative_path)
{
    const std::string path = std::string(EE_NATIVE_SOURCE_DIR) + relative_path;
    std::ifstream input(path, std::ios::binary);
    if (!input)
    {
        std::cerr << "RED setup failure: unable to open " << path << '\n';
        return {};
    }

    std::ostringstream buffer;
    buffer << input.rdbuf();
    return buffer.str();
}

std::string FunctionBody(const std::string& source, const std::string& signature)
{
    const std::size_t start = source.find(signature);
    if (start == std::string::npos)
        return {};

    const std::size_t open = source.find('{', start);
    if (open == std::string::npos)
        return {};

    int depth = 0;
    for (std::size_t i = open; i < source.size(); ++i)
    {
        if (source[i] == '{')
            ++depth;
        else if (source[i] == '}' && --depth == 0)
            return source.substr(open, i - open + 1);
    }
    return {};
}
}

int main()
{
    const std::string scene_header = ReadSource("/src/level_scene.h");
    const std::string backend = ReadSource("/src/d2d_backend.cpp");
    const std::string camera_backend = ReadSource("/src/d2d_backend_camera.cpp");
    if (scene_header.empty() || backend.empty() || camera_backend.empty())
        return 2;

    bool failed = false;

    if (scene_header.find("AppendVisualTransformCandidates(") == std::string::npos)
    {
        std::cerr
            << "RED: LevelScene must expose conservative visual-transform cull candidates "
            << "for scaled floors whose centers are outside the viewport.\n";
        failed = true;
    }

    const std::string query = FunctionBody(
        backend,
        "void D2DBackend::QueryVisibleFloors(");
    if (query.empty() || query.find("AppendVisualTransformCandidates(") == std::string::npos)
    {
        std::cerr
            << "RED: QueryVisibleFloors must supplement center-based spatial culling "
            << "with visual-transform candidates.\n";
        failed = true;
    }

    const std::string camera_query = FunctionBody(
        camera_backend,
        "void D2DBackend::QueryVisibleFloorsCamera(");
    if (camera_query.empty() || camera_query.find("AppendVisualTransformCandidates(") == std::string::npos)
    {
        std::cerr
            << "RED: QueryVisibleFloorsCamera must supplement center-based spatial culling "
            << "with visual-transform candidates.\n";
        failed = true;
    }

    if (failed)
        return 1;

    std::cout << "visual transform cull coverage contract passed\n";
    return 0;
}
