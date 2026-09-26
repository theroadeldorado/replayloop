cmake_minimum_required(VERSION 3.20)
project(swingcore VERSION 0.1.0 LANGUAGES CXX)

# Windows: swingcore.dll (P/Invoke). Android: libswingcore.so (JNI).
# iOS: static library wrapped in an XCFramework (see docs/ARCHITECTURE.md).
option(SWINGCORE_SHARED "Build swingcore as a shared library" ON)
option(SWINGCORE_TESTS "Build unit tests" ON)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)
set(CMAKE_CXX_VISIBILITY_PRESET hidden)
set(CMAKE_VISIBILITY_INLINES_HIDDEN ON)

set(SWINGCORE_SOURCES
    src/annotations.cpp
    src/json.cpp
    src/layout.cpp
    src/session.cpp
    src/signals.cpp
    src/swing_detector.cpp
    src/swingcore_c.cpp)

if(SWINGCORE_SHARED)
    add_library(swingcore SHARED ${SWINGCORE_SOURCES})
else()
    add_library(swingcore STATIC ${SWINGCORE_SOURCES})
endif()

target_include_directories(swingcore PUBLIC ${CMAKE_CURRENT_SOURCE_DIR}/include)
target_compile_definitions(swingcore PRIVATE SWINGCORE_BUILD)

if(MSVC)
    target_compile_options(swingcore PRIVATE /W4 /permissive- /utf-8 /EHsc)
    # Static CRT so the DLL runs on machines without the VC++ redistributable.
    set_property(TARGET swingcore PROPERTY MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
else()
    target_compile_options(swingcore PRIVATE -Wall -Wextra -Wpedantic)
endif()

if(SWINGCORE_TESTS AND NOT IOS AND NOT ANDROID)
    enable_testing()
    # Tests exercise internal C++ classes, so they link the sources directly
    # rather than the (symbol-hidden) shared library.
    add_executable(swingcore_tests tests/test_main.cpp ${SWINGCORE_SOURCES})
    target_include_directories(swingcore_tests PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/include)
    target_compile_definitions(swingcore_tests PRIVATE SWINGCORE_BUILD)
    if(MSVC)
        target_compile_options(swingcore_tests PRIVATE /utf-8 /EHsc)
        set_property(TARGET swingcore_tests PROPERTY MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
    endif()
    add_test(NAME swingcore_tests COMMAND swingcore_tests)
endif()

install(TARGETS swingcore
        RUNTIME DESTINATION bin
        LIBRARY DESTINATION lib
        ARCHIVE DESTINATION lib)
install(DIRECTORY include/ DESTINATION include)
