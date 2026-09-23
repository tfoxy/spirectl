cmake_minimum_required(VERSION 3.20)
project(spirectl_native_scene_invalidation LANGUAGES CXX)

if(NOT CMAKE_SYSTEM_NAME STREQUAL "Linux" OR
   NOT CMAKE_SYSTEM_PROCESSOR MATCHES "^(x86_64|amd64|AMD64)$")
  message(FATAL_ERROR "This isolated detour fixture supports Linux x86-64 only")
endif()

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

find_package(OpenSSL REQUIRED)

add_library(spirectl_native_observer SHARED
  src/native_observer.cpp
  src/gdextension_entry.cpp)
target_include_directories(spirectl_native_observer PUBLIC include)
target_link_libraries(spirectl_native_observer PRIVATE OpenSSL::Crypto dl pthread)
target_compile_options(spirectl_native_observer PRIVATE -Wall -Wextra -Werror)

add_executable(native_observer_probe probe/fixture_host.cpp)
target_include_directories(native_observer_probe PRIVATE include)
target_link_libraries(native_observer_probe PRIVATE spirectl_native_observer)
target_compile_options(native_observer_probe PRIVATE -Wall -Wextra -Werror -fno-omit-frame-pointer
  -fno-devirtualize -fno-devirtualize-speculatively)
target_link_options(native_observer_probe PRIVATE -Wl,-rpath,$ORIGIN)

add_executable(native_observer_preflight probe/preflight.cpp)
target_include_directories(native_observer_preflight PRIVATE include)
target_link_libraries(native_observer_preflight PRIVATE spirectl_native_observer)
target_compile_options(native_observer_preflight PRIVATE -Wall -Wextra -Werror)
target_link_options(native_observer_preflight PRIVATE -Wl,-rpath,$ORIGIN)

add_executable(native_observer_malformed_fingerprint probe/malformed_fingerprint.cpp)
target_include_directories(native_observer_malformed_fingerprint PRIVATE include)
target_link_libraries(native_observer_malformed_fingerprint PRIVATE spirectl_native_observer)
target_compile_options(native_observer_malformed_fingerprint PRIVATE -Wall -Wextra -Werror)
target_link_options(native_observer_malformed_fingerprint PRIVATE -Wl,-rpath,$ORIGIN)

enable_testing()
add_test(NAME native_observer_probe COMMAND native_observer_probe)
add_test(NAME native_observer_hex_rejection COMMAND native_observer_preflight --self-test-hex)
add_test(NAME native_observer_malformed_fingerprint COMMAND native_observer_malformed_fingerprint)
