cmake_minimum_required(VERSION 3.24)

project(ExtremeEditorNativeRenderer LANGUAGES CXX)

enable_testing()

add_library(ExtremeEditor.NativeRenderer SHARED
    src/exports.cpp
    src/native_window.cpp
    src/renderer.cpp
    src/renderer_editor.cpp
    src/renderer_diagnostics.cpp
    src/d2d_backend.cpp
    src/d2d_backend_compat.cpp
    src/d2d_backend_editor.cpp
    src/floor_instanced_renderer.cpp
    src/icon_instanced_renderer.cpp
    src/level_scene.cpp
)

target_compile_features(ExtremeEditor.NativeRenderer PRIVATE cxx_std_20)
target_compile_definitions(ExtremeEditor.NativeRenderer PRIVATE
    EE_RENDERER_BUILD=1
    UNICODE
    _UNICODE
    NOMINMAX
)
target_include_directories(ExtremeEditor.NativeRenderer
    PUBLIC
        ${CMAKE_CURRENT_SOURCE_DIR}/include
    PRIVATE
        ${CMAKE_CURRENT_SOURCE_DIR}/src
)

target_link_libraries(ExtremeEditor.NativeRenderer PRIVATE
    user32
    d3d11
    d3dcompiler
    dxgi
    d2d1
    dwrite
    windowscodecs
    ole32
)

if(MSVC)
    target_compile_options(ExtremeEditor.NativeRenderer PRIVATE /W4 /permissive-)
endif()

add_executable(native_renderer_tests
    tests/native_renderer_tests.cpp
)

target_compile_features(native_renderer_tests PRIVATE cxx_std_20)
target_compile_definitions(native_renderer_tests PRIVATE UNICODE _UNICODE NOMINMAX)
target_include_directories(native_renderer_tests PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/include)
target_link_libraries(native_renderer_tests PRIVATE ExtremeEditor.NativeRenderer)

add_test(NAME native_renderer_tests COMMAND native_renderer_tests)
