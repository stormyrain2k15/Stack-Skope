cmake_minimum_required(VERSION 3.20)
project(stackscope_llamacpp_worker LANGUAGES C CXX)

set(CMAKE_C_STANDARD 11)
set(CMAKE_CXX_STANDARD 17)
set(CMAKE_C_STANDARD_REQUIRED ON)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

# llama.cpp submodule
set(LLAMA_ROOT "${CMAKE_CURRENT_SOURCE_DIR}/vendor/llama.cpp")
if(NOT EXISTS "${LLAMA_ROOT}/CMakeLists.txt")
    message(FATAL_ERROR
        "llama.cpp submodule not initialised at ${LLAMA_ROOT}. "
        "Run: git submodule update --init --recursive")
endif()

set(LLAMA_STATIC ON  CACHE BOOL "Static-link llama.cpp into the worker.")
set(LLAMA_BUILD_TESTS   OFF CACHE BOOL "" FORCE)
set(LLAMA_BUILD_EXAMPLES OFF CACHE BOOL "" FORCE)
set(LLAMA_BUILD_SERVER   OFF CACHE BOOL "" FORCE)
add_subdirectory("${LLAMA_ROOT}" llama_build)

# gRPC C++ (assumes vcpkg or apt-installed protobuf + grpc). On the
# Windows box a `.\vcpkg install grpc protobuf` before the CMake configure
# is required. On Linux CI, `sudo apt install libprotobuf-dev
# protobuf-compiler libgrpc++-dev protobuf-compiler-grpc` suffices.
find_package(Protobuf REQUIRED)
find_package(gRPC     CONFIG REQUIRED)

set(PROTO_DIR "${CMAKE_CURRENT_SOURCE_DIR}/../../proto")
set(GEN_DIR   "${CMAKE_CURRENT_BINARY_DIR}/gen")
file(MAKE_DIRECTORY "${GEN_DIR}")

set(PROTO_FILES
    "${PROTO_DIR}/events.proto"
    "${PROTO_DIR}/worker.proto")

foreach(pf ${PROTO_FILES})
    get_filename_component(pfn "${pf}" NAME_WE)
    set(pb_h  "${GEN_DIR}/${pfn}.pb.h")
    set(pb_cc "${GEN_DIR}/${pfn}.pb.cc")
    set(gpb_h  "${GEN_DIR}/${pfn}.grpc.pb.h")
    set(gpb_cc "${GEN_DIR}/${pfn}.grpc.pb.cc")

    add_custom_command(
        OUTPUT "${pb_h}" "${pb_cc}" "${gpb_h}" "${gpb_cc}"
        COMMAND ${Protobuf_PROTOC_EXECUTABLE}
            --proto_path=${PROTO_DIR}
            --cpp_out=${GEN_DIR}
            --grpc_out=${GEN_DIR}
            --plugin=protoc-gen-grpc=$<TARGET_FILE:gRPC::grpc_cpp_plugin>
            "${pf}"
        DEPENDS "${pf}"
        VERBATIM)
    list(APPEND GENERATED_SRCS "${pb_cc}" "${gpb_cc}")
endforeach()

add_executable(stackscope_llamacpp_worker
    src/main.cc
    src/worker.c
    src/callbacks.c
    ${GENERATED_SRCS})

target_include_directories(stackscope_llamacpp_worker PRIVATE
    "${GEN_DIR}"
    "${LLAMA_ROOT}"
    "${LLAMA_ROOT}/include"
    "src")

target_link_libraries(stackscope_llamacpp_worker PRIVATE
    llama
    ggml
    gRPC::grpc++
    protobuf::libprotobuf)

# Compile-time detection of llama_model_dev_layer() (mid-2024+ llama.cpp).
# When present, worker reads real per-layer placement from llama.cpp.
# When absent, worker reflects the user's request and flags it unverified
# on LoadModelReply.resolved_device_verified so the WPF UI can badge it
# accurately instead of silently lying about placement.
include(CheckCXXSymbolExists)
set(CMAKE_REQUIRED_INCLUDES "${LLAMA_ROOT}/include" "${LLAMA_ROOT}")
check_cxx_symbol_exists(llama_model_dev_layer "llama.h" HAS_LLAMA_MODEL_DEV_LAYER)
if(HAS_LLAMA_MODEL_DEV_LAYER)
    target_compile_definitions(stackscope_llamacpp_worker PRIVATE LLAMA_HAS_MODEL_DEV_LAYER=1)
    message(STATUS "StackScope: llama_model_dev_layer detected — real placement readback enabled.")
else()
    message(WARNING "StackScope: llama_model_dev_layer not found — worker will flag placement as unverified. Update the llama.cpp submodule to a mid-2024+ checkout.")
endif()


if(WIN32)
    target_link_libraries(stackscope_llamacpp_worker PRIVATE ws2_32)
endif()
