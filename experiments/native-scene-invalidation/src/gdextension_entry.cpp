#include <cstdint>
#include <cstdlib>
#include <fstream>

extern "C" {
using GDExtensionBool = uint8_t;
using GDExtensionClassLibraryPtr = void *;
using GDExtensionInterfaceFunctionPtr = void (*)();
using GDExtensionInterfaceGetProcAddress = GDExtensionInterfaceFunctionPtr (*)(const char *);
enum GDExtensionInitializationLevel {
  GDEXTENSION_INITIALIZATION_CORE,
  GDEXTENSION_INITIALIZATION_SERVERS,
  GDEXTENSION_INITIALIZATION_SCENE,
  GDEXTENSION_INITIALIZATION_EDITOR,
};
using GDExtensionInitializeCallback = void (*)(void *, GDExtensionInitializationLevel);
using GDExtensionDeinitializeCallback = void (*)(void *, GDExtensionInitializationLevel);
struct GDExtensionInitialization {
  GDExtensionInitializationLevel minimum_initialization_level;
  void *userdata;
  GDExtensionInitializeCallback initialize;
  GDExtensionDeinitializeCallback deinitialize;
};

static void initialize(void *, GDExtensionInitializationLevel level) {
  if (level != GDEXTENSION_INITIALIZATION_SCENE) {
    return;
  }
  const char *path = std::getenv("SPI_NATIVE_OBSERVER_LOAD_MARKER");
  if (path != nullptr && path[0] != '\0') {
    std::ofstream(path) << "loaded\n";
  }
}

static void deinitialize(void *, GDExtensionInitializationLevel) {}

__attribute__((visibility("default"))) GDExtensionBool spirectl_native_observer_init(
    GDExtensionInterfaceGetProcAddress, GDExtensionClassLibraryPtr,
    GDExtensionInitialization *initialization) {
  if (initialization == nullptr) {
    return 0;
  }
  initialization->minimum_initialization_level = GDEXTENSION_INITIALIZATION_SCENE;
  initialization->userdata = nullptr;
  initialization->initialize = initialize;
  initialization->deinitialize = deinitialize;
  return 1;
}
}
