#pragma once

#include <cstddef>
#include <cstdint>

#if defined(__GNUC__)
#define SPI_EXPORT __attribute__((visibility("default")))
#else
#define SPI_EXPORT
#endif

extern "C" {

enum SpiDirtyKind : uint32_t {
  SPI_CANVAS_TRANSFORM = 1,
  SPI_CANVAS_VISIBLE,
  SPI_CANVAS_MODULATE,
  SPI_CANVAS_SELF_MODULATE,
  SPI_CANVAS_Z,
  SPI_VIEWPORT_CANVAS_TRANSFORM,
  SPI_MATERIAL_PARAMETER,
  SPI_PARTICLE_STATE,
  SPI_QUEUE_REDRAW,
  SPI_PUBLIC_SIGNAL,
};

struct SpiFingerprint {
  uint8_t sha256[32];
  uint8_t build_id[32];
  size_t build_id_size;
};

struct SpiSlotHook {
  size_t slot;
  SpiDirtyKind kind;
};

struct SpiDirtyNode {
  uint64_t node_id;
  SpiDirtyKind kind;
};

struct SpiDrainResult {
  size_t count;
  bool overflowed;
  bool stopped;
};

struct SpiObserver;

SPI_EXPORT bool spi_inspect_process(const char *path, SpiFingerprint *out);
SPI_EXPORT SpiObserver *spi_observer_create(size_t ring_capacity);
SPI_EXPORT void spi_observer_destroy(SpiObserver *observer);
SPI_EXPORT bool spi_observer_expect_process(SpiObserver *observer, const SpiFingerprint *expected);
SPI_EXPORT bool spi_observer_map_rid(SpiObserver *observer, uint64_t rid, uint64_t node_id);
SPI_EXPORT void spi_observer_unmap_rid(SpiObserver *observer, uint64_t rid);
SPI_EXPORT bool spi_observer_arm_vtable(SpiObserver *observer, void *object,
                                       size_t vtable_entries, const SpiSlotHook *hooks,
                                       size_t hook_count);
SPI_EXPORT bool spi_observer_arm_queue_redraw(SpiObserver *observer, void *target,
                                             const uint8_t expected_prefix[12]);
SPI_EXPORT bool spi_observer_notify_node(SpiObserver *observer, uint64_t node_id,
                                        SpiDirtyKind kind);
SPI_EXPORT SpiDrainResult spi_observer_drain(SpiObserver *observer, SpiDirtyNode *out,
                                            size_t capacity);
SPI_EXPORT bool spi_observer_validate_capture(SpiObserver *observer,
                                             const uint64_t *authoritative_changed_nodes,
                                             size_t changed_count);
SPI_EXPORT bool spi_observer_disarm(SpiObserver *observer);
SPI_EXPORT bool spi_observer_is_stopped(const SpiObserver *observer);

}
