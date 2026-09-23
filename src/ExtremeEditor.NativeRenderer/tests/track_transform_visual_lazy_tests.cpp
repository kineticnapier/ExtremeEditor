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
    const std::string header = ReadSource("/src/track_transform_runtime.h");
    const std::string implementation = ReadSource("/src/track_transform_runtime.cpp");
    if (header.empty() || implementation.empty())
        return 2;

    bool failed = false;

    if (header.find("bool has_position = false") == std::string::npos)
    {
        std::cerr << "RED: FloorTrack must classify whether it contains position transforms.\n";
        failed = true;
    }

    if (header.find("EvaluateVisualForFloor(") == std::string::npos)
    {
        std::cerr << "RED: visual-only track must be lazily evaluated per floor.\n";
        failed = true;
    }

    const std::string update = FunctionBody(
        implementation,
        "TrackTransformUpdateMetrics TrackTransformRuntime::Update(");
    const std::string rebuild = FunctionBody(
        implementation,
        "void TrackTransformRuntime::RebuildRuntimeState(");

    const auto has_position_gate = [](const std::string& body)
    {
        return body.find("track.has_position") != std::string::npos &&
               (body.find("if (!track.has_position)") != std::string::npos ||
                body.find("if (track.has_position)") != std::string::npos);
    };

    if (update.empty() || !has_position_gate(update))
    {
        std::cerr << "RED: TrackTransformRuntime::Update must not eagerly resolve visual-only active tracks.\n";
        failed = true;
    }

    if (rebuild.empty() || !has_position_gate(rebuild))
    {
        std::cerr << "RED: RebuildRuntimeState must not eagerly resolve visual-only tracks.\n";
        failed = true;
    }

    if (failed)
        return 1;

    std::cout << "visual-only lazy track-transform contract passed\n";
    return 0;
}
