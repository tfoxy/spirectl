#include "spirectl_native_observer.h"

#include <openssl/sha.h>

#include <algorithm>
#include <array>
#include <atomic>
#include <cerrno>
#include <cstring>
#include <elf.h>
#include <fcntl.h>
#include <fstream>
#include <memory>
#include <mutex>
#include <sys/mman.h>
#include <sys/stat.h>
#include <unistd.h>
#include <unordered_map>
#include <unordered_set>
#include <vector>

namespace {
constexpr size_t kPatchSize = 12;
constexpr size_t kMaximumVtableEntries = 4096;
constexpr size_t kMaximumHooks = 8;

struct DirtyEvent {
  uint64_t node_id;
  SpiDirtyKind kind;
};

struct Cell {
  std::atomic<size_t> sequence{0};
  DirtyEvent event{};
};

class DirtyRing {
 public:
  explicit DirtyRing(size_t requested) {
    capacity_ = 2;
    while (capacity_ < std::max<size_t>(requested, 2)) {
      capacity_ <<= 1;
    }
    cells_ = std::make_unique<Cell[]>(capacity_);
    for (size_t i = 0; i < capacity_; ++i) {
      cells_[i].sequence.store(i, std::memory_order_relaxed);
    }
  }

  bool push(DirtyEvent event) {
    size_t position = enqueue_.load(std::memory_order_relaxed);
    for (;;) {
      Cell &cell = cells_[position & (capacity_ - 1)];
      const size_t sequence = cell.sequence.load(std::memory_order_acquire);
      const intptr_t difference = static_cast<intptr_t>(sequence) - static_cast<intptr_t>(position);
      if (difference == 0) {
        if (enqueue_.compare_exchange_weak(position, position + 1, std::memory_order_relaxed)) {
          cell.event = event;
          cell.sequence.store(position + 1, std::memory_order_release);
          return true;
        }
      } else if (difference < 0) {
        overflow_.store(true, std::memory_order_release);
        return false;
      } else {
        position = enqueue_.load(std::memory_order_relaxed);
      }
    }
  }

  bool pop(DirtyEvent &event) {
    Cell &cell = cells_[dequeue_ & (capacity_ - 1)];
    if (cell.sequence.load(std::memory_order_acquire) != dequeue_ + 1) {
      return false;
    }
    event = cell.event;
    cell.sequence.store(dequeue_ + capacity_, std::memory_order_release);
    ++dequeue_;
    return true;
  }

  bool take_overflow() { return overflow_.exchange(false, std::memory_order_acq_rel); }

 private:
  size_t capacity_{};
  std::unique_ptr<Cell[]> cells_;
  std::atomic<size_t> enqueue_{0};
  size_t dequeue_{0};
  std::atomic<bool> overflow_{false};
};

using RedrawMethod = void (*)(uint64_t);

struct HookRecord {
  size_t slot{};
  SpiDirtyKind kind{};
  void *original{};
};
}

struct SpiObserver {
  explicit SpiObserver(size_t capacity) : ring(capacity) {}

  DirtyRing ring;
  std::mutex map_mutex;
  std::unordered_map<uint64_t, uint64_t> rid_to_node;
  std::atomic<bool> fingerprint_ok{false};
  std::atomic<bool> stopped{false};
  void *server_object{};
  void **original_vtable{};
  std::unique_ptr<void *[]> shadow_vtable;
  size_t vtable_entries{};
  std::array<HookRecord, kMaximumHooks> hooks{};
  size_t hook_count{};
  void *redraw_target{};
  std::array<uint8_t, kPatchSize> redraw_original{};
  void *redraw_trampoline{};
  size_t redraw_mapping_size{};
  std::mutex lifecycle_mutex;
  std::unordered_set<uint64_t> last_drain_nodes;
  bool last_drain_overflowed{};
  bool teardown_failed{};

  void mark_rid(uint64_t rid, SpiDirtyKind kind) {
    uint64_t node = 0;
    {
      std::lock_guard lock(map_mutex);
      const auto found = rid_to_node.find(rid);
      if (found == rid_to_node.end()) {
        return;
      }
      node = found->second;
    }
    ring.push({node, kind});
  }
};

namespace {
std::atomic<SpiObserver *> active_observer{nullptr};

bool read_file(const char *path, std::vector<uint8_t> &bytes) {
  std::ifstream stream(path, std::ios::binary);
  if (!stream) return false;
  stream.seekg(0, std::ios::end);
  const auto size = stream.tellg();
  if (size <= 0) return false;
  bytes.resize(static_cast<size_t>(size));
  stream.seekg(0);
  return static_cast<bool>(stream.read(reinterpret_cast<char *>(bytes.data()), size));
}

bool parse_build_id(const std::vector<uint8_t> &bytes, SpiFingerprint &out) {
  if (bytes.size() < sizeof(Elf64_Ehdr)) return false;
  const auto *header = reinterpret_cast<const Elf64_Ehdr *>(bytes.data());
  if (std::memcmp(header->e_ident, ELFMAG, SELFMAG) != 0 ||
      header->e_ident[EI_CLASS] != ELFCLASS64 || header->e_phentsize != sizeof(Elf64_Phdr)) return false;
  if (header->e_phoff > bytes.size() ||
      header->e_phnum > (bytes.size() - static_cast<size_t>(header->e_phoff)) / sizeof(Elf64_Phdr)) return false;
  const auto *programs = reinterpret_cast<const Elf64_Phdr *>(bytes.data() + header->e_phoff);
  for (size_t i = 0; i < header->e_phnum; ++i) {
    if (programs[i].p_type != PT_NOTE || programs[i].p_offset > bytes.size() ||
        programs[i].p_filesz > bytes.size() - static_cast<size_t>(programs[i].p_offset)) continue;
    size_t cursor = static_cast<size_t>(programs[i].p_offset);
    const size_t end = cursor + static_cast<size_t>(programs[i].p_filesz);
    while (cursor <= end && sizeof(Elf64_Nhdr) <= end - cursor) {
      const auto *note = reinterpret_cast<const Elf64_Nhdr *>(bytes.data() + cursor);
      cursor += sizeof(Elf64_Nhdr);
      const size_t name_size = (static_cast<size_t>(note->n_namesz) + 3U) & ~size_t{3};
      const size_t desc_size = (static_cast<size_t>(note->n_descsz) + 3U) & ~size_t{3};
      if (name_size > end - cursor) break;
      const char *name = reinterpret_cast<const char *>(bytes.data() + cursor);
      cursor += name_size;
      if (desc_size > end - cursor) break;
      const uint8_t *description = bytes.data() + cursor;
      cursor += desc_size;
      if (note->n_type == NT_GNU_BUILD_ID && note->n_namesz >= 3 && std::memcmp(name, "GNU", 3) == 0 &&
          note->n_descsz <= sizeof(out.build_id)) {
        std::memcpy(out.build_id, description, note->n_descsz);
        out.build_id_size = note->n_descsz;
        return true;
      }
    }
  }
  return false;
}

HookRecord *find_hook(SpiObserver *observer, SpiDirtyKind kind) {
  for (size_t i = 0; i < observer->hook_count; ++i) {
    if (observer->hooks[i].kind == kind) return &observer->hooks[i];
  }
  return nullptr;
}

HookRecord *prepare_server_call(SpiDirtyKind kind, uint64_t rid) {
  SpiObserver *observer = active_observer.load(std::memory_order_acquire);
  if (observer == nullptr) return nullptr;
  HookRecord *hook = find_hook(observer, kind);
  if (hook == nullptr || hook->original == nullptr) {
    observer->stopped.store(true, std::memory_order_release);
    return nullptr;
  }
  if (!observer->stopped.load(std::memory_order_acquire)) observer->mark_rid(rid, kind);
  return hook;
}

// These signatures mirror the public RenderingServer virtuals. Opaque pointers preserve the ABI of
// const Transform2D/Color/StringName/Variant references without depending on private engine layout.
void hook_canvas_transform(void *self, uint64_t rid, const void *value) {
  if (auto *h = prepare_server_call(SPI_CANVAS_TRANSFORM, rid))
    reinterpret_cast<void (*)(void *, uint64_t, const void *)>(h->original)(self, rid, value);
}
void hook_canvas_visible(void *self, uint64_t rid, bool value) {
  if (auto *h = prepare_server_call(SPI_CANVAS_VISIBLE, rid))
    reinterpret_cast<void (*)(void *, uint64_t, bool)>(h->original)(self, rid, value);
}
void hook_canvas_modulate(void *self, uint64_t rid, const void *value) {
  if (auto *h = prepare_server_call(SPI_CANVAS_MODULATE, rid))
    reinterpret_cast<void (*)(void *, uint64_t, const void *)>(h->original)(self, rid, value);
}
void hook_canvas_self_modulate(void *self, uint64_t rid, const void *value) {
  if (auto *h = prepare_server_call(SPI_CANVAS_SELF_MODULATE, rid))
    reinterpret_cast<void (*)(void *, uint64_t, const void *)>(h->original)(self, rid, value);
}
void hook_canvas_z(void *self, uint64_t rid, int32_t value) {
  if (auto *h = prepare_server_call(SPI_CANVAS_Z, rid))
    reinterpret_cast<void (*)(void *, uint64_t, int32_t)>(h->original)(self, rid, value);
}
void hook_viewport_transform(void *self, uint64_t viewport, uint64_t canvas, const void *value) {
  if (auto *h = prepare_server_call(SPI_VIEWPORT_CANVAS_TRANSFORM, canvas))
    reinterpret_cast<void (*)(void *, uint64_t, uint64_t, const void *)>(h->original)(self, viewport, canvas,
                                                                                       value);
}
void hook_material_parameter(void *self, uint64_t material, const void *name, const void *value) {
  if (auto *h = prepare_server_call(SPI_MATERIAL_PARAMETER, material))
    reinterpret_cast<void (*)(void *, uint64_t, const void *, const void *)>(h->original)(self, material, name,
                                                                                         value);
}
void hook_particle_state(void *self, uint64_t particles, bool emitting) {
  if (auto *h = prepare_server_call(SPI_PARTICLE_STATE, particles))
    reinterpret_cast<void (*)(void *, uint64_t, bool)>(h->original)(self, particles, emitting);
}

void *wrapper_for(SpiDirtyKind kind) {
  switch (kind) {
    case SPI_CANVAS_TRANSFORM: return reinterpret_cast<void *>(hook_canvas_transform);
    case SPI_CANVAS_VISIBLE: return reinterpret_cast<void *>(hook_canvas_visible);
    case SPI_CANVAS_MODULATE: return reinterpret_cast<void *>(hook_canvas_modulate);
    case SPI_CANVAS_SELF_MODULATE: return reinterpret_cast<void *>(hook_canvas_self_modulate);
    case SPI_CANVAS_Z: return reinterpret_cast<void *>(hook_canvas_z);
    case SPI_VIEWPORT_CANVAS_TRANSFORM: return reinterpret_cast<void *>(hook_viewport_transform);
    case SPI_MATERIAL_PARAMETER: return reinterpret_cast<void *>(hook_material_parameter);
    case SPI_PARTICLE_STATE: return reinterpret_cast<void *>(hook_particle_state);
    default: return nullptr;
  }
}

void hook_redraw(uint64_t rid) {
  SpiObserver *observer = active_observer.load(std::memory_order_acquire);
  if (observer == nullptr || observer->redraw_trampoline == nullptr) return;
  if (!observer->stopped.load(std::memory_order_acquire)) observer->mark_rid(rid, SPI_QUEUE_REDRAW);
  reinterpret_cast<RedrawMethod>(observer->redraw_trampoline)(rid);
}

void write_absolute_jump(uint8_t *destination, const void *target) {
  destination[0] = 0x48;
  destination[1] = 0xB8;
  const uint64_t address = reinterpret_cast<uint64_t>(target);
  std::memcpy(destination + 2, &address, sizeof(address));
  destination[10] = 0xFF;
  destination[11] = 0xE0;
}

bool set_page_permissions(void *address, int permissions) {
  const long page_size = sysconf(_SC_PAGESIZE);
  if (page_size <= 0) return false;
  const uintptr_t page = reinterpret_cast<uintptr_t>(address) & ~(static_cast<uintptr_t>(page_size) - 1);
  return mprotect(reinterpret_cast<void *>(page), static_cast<size_t>(page_size), permissions) == 0;
}
}

extern "C" {
bool spi_inspect_process(const char *path, SpiFingerprint *out) {
  if (path == nullptr || out == nullptr) return false;
  std::vector<uint8_t> bytes;
  if (!read_file(path, bytes)) return false;
  std::memset(out, 0, sizeof(*out));
  SHA256(bytes.data(), bytes.size(), out->sha256);
  return parse_build_id(bytes, *out);
}

SpiObserver *spi_observer_create(size_t ring_capacity) {
  if (ring_capacity == 0 || ring_capacity > (1U << 20)) return nullptr;
  return new SpiObserver(ring_capacity);
}

void spi_observer_destroy(SpiObserver *observer) {
  if (observer == nullptr) return;
  if (!spi_observer_disarm(observer)) return; // Deliberately leak executable backing on unsafe teardown.
  delete observer;
}

bool spi_observer_expect_process(SpiObserver *observer, const SpiFingerprint *expected) {
  if (observer == nullptr || expected == nullptr) return false;
  SpiFingerprint actual{};
  const bool matches = spi_inspect_process("/proc/self/exe", &actual) &&
      std::memcmp(actual.sha256, expected->sha256, sizeof(actual.sha256)) == 0 &&
      actual.build_id_size == expected->build_id_size &&
      std::memcmp(actual.build_id, expected->build_id, actual.build_id_size) == 0;
  observer->fingerprint_ok.store(matches, std::memory_order_release);
  return matches;
}

bool spi_observer_map_rid(SpiObserver *observer, uint64_t rid, uint64_t node_id) {
  if (observer == nullptr || node_id == 0) return false;
  std::lock_guard lock(observer->map_mutex);
  return observer->rid_to_node.emplace(rid, node_id).second;
}

void spi_observer_unmap_rid(SpiObserver *observer, uint64_t rid) {
  if (observer == nullptr) return;
  std::lock_guard lock(observer->map_mutex);
  observer->rid_to_node.erase(rid);
}

bool spi_observer_arm_vtable(SpiObserver *observer, void *object, size_t vtable_entries,
                             const SpiSlotHook *hooks, size_t hook_count) {
  if (observer == nullptr || object == nullptr || hooks == nullptr || hook_count == 0 ||
      hook_count > kMaximumHooks || vtable_entries == 0 || vtable_entries > kMaximumVtableEntries ||
      !observer->fingerprint_ok.load(std::memory_order_acquire)) return false;
  std::lock_guard lock(observer->lifecycle_mutex);
  for (size_t i = 0; i < hook_count; ++i) {
    if (hooks[i].slot >= vtable_entries || wrapper_for(hooks[i].kind) == nullptr) return false;
    for (size_t j = 0; j < i; ++j) {
      if (hooks[j].slot == hooks[i].slot || hooks[j].kind == hooks[i].kind) return false;
    }
  }
  SpiObserver *expected = nullptr;
  if (!active_observer.compare_exchange_strong(expected, observer, std::memory_order_acq_rel)) return false;
  observer->server_object = object;
  observer->original_vtable = *reinterpret_cast<void ***>(object);
  observer->vtable_entries = vtable_entries;
  observer->shadow_vtable = std::make_unique<void *[]>(vtable_entries);
  std::copy_n(observer->original_vtable, vtable_entries, observer->shadow_vtable.get());
  for (size_t i = 0; i < hook_count; ++i) {
    void *wrapper = wrapper_for(hooks[i].kind);
    if (hooks[i].slot >= vtable_entries || wrapper == nullptr) {
      active_observer.store(nullptr, std::memory_order_release);
      observer->shadow_vtable.reset();
      observer->server_object = nullptr;
      return false;
    }
    observer->hooks[i] = {hooks[i].slot, hooks[i].kind,
                          observer->original_vtable[hooks[i].slot]};
    observer->shadow_vtable[hooks[i].slot] = wrapper;
  }
  observer->hook_count = hook_count;
  std::atomic_ref<void *>(*reinterpret_cast<void **>(object)).store(observer->shadow_vtable.get(),
                                                                    std::memory_order_release);
  return true;
}

bool spi_observer_arm_queue_redraw(SpiObserver *observer, void *target,
                                   const uint8_t expected_prefix[kPatchSize]) {
  if (observer == nullptr || target == nullptr || expected_prefix == nullptr ||
      active_observer.load(std::memory_order_acquire) != observer ||
      !observer->fingerprint_ok.load(std::memory_order_acquire)) return false;
  std::lock_guard lock(observer->lifecycle_mutex);
  if (observer->redraw_target != nullptr || observer->redraw_trampoline != nullptr) return false;
  const long page_size = sysconf(_SC_PAGESIZE);
  if (page_size <= 0 || static_cast<size_t>(page_size) < kPatchSize ||
      reinterpret_cast<uintptr_t>(target) % static_cast<size_t>(page_size) >
          static_cast<size_t>(page_size) - kPatchSize) return false;
  if (std::memcmp(target, expected_prefix, kPatchSize) != 0) return false;
  std::memcpy(observer->redraw_original.data(), target, kPatchSize);
  observer->redraw_mapping_size = static_cast<size_t>(page_size);
  observer->redraw_trampoline = mmap(nullptr, observer->redraw_mapping_size, PROT_READ | PROT_WRITE,
                                     MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
  if (observer->redraw_trampoline == MAP_FAILED) {
    observer->redraw_trampoline = nullptr;
    return false;
  }
  auto *trampoline = reinterpret_cast<uint8_t *>(observer->redraw_trampoline);
  std::memcpy(trampoline, target, kPatchSize);
  write_absolute_jump(trampoline + kPatchSize, reinterpret_cast<uint8_t *>(target) + kPatchSize);
  if (mprotect(observer->redraw_trampoline, observer->redraw_mapping_size, PROT_READ | PROT_EXEC) != 0 ||
      !set_page_permissions(target, PROT_READ | PROT_WRITE | PROT_EXEC)) {
    munmap(observer->redraw_trampoline, observer->redraw_mapping_size);
    observer->redraw_trampoline = nullptr;
    return false;
  }
  std::array<uint8_t, kPatchSize> patch{};
  write_absolute_jump(patch.data(), reinterpret_cast<void *>(hook_redraw));
  // Record ownership before the first write, including when restoring RX permissions subsequently fails.
  // Disarm must still see that live detour and keep its trampoline until restoration has been proved.
  observer->redraw_target = target;
  std::memcpy(target, patch.data(), patch.size());
  __builtin___clear_cache(reinterpret_cast<char *>(target), reinterpret_cast<char *>(target) + patch.size());
  if (!set_page_permissions(target, PROT_READ | PROT_EXEC)) {
    observer->stopped.store(true, std::memory_order_release);
    return false;
  }
  return true;
}

bool spi_observer_notify_node(SpiObserver *observer, uint64_t node_id, SpiDirtyKind kind) {
  if (observer == nullptr || node_id == 0 || observer->stopped.load(std::memory_order_acquire)) return false;
  return observer->ring.push({node_id, kind});
}

SpiDrainResult spi_observer_drain(SpiObserver *observer, SpiDirtyNode *out, size_t capacity) {
  SpiDrainResult result{};
  if (observer == nullptr) { result.stopped = true; return result; }
  DirtyEvent event{};
  while (result.count < capacity && observer->ring.pop(event)) {
    out[result.count++] = {event.node_id, event.kind};
  }
  result.overflowed = observer->ring.take_overflow();
  observer->last_drain_nodes.clear();
  for (size_t i = 0; i < result.count; ++i) observer->last_drain_nodes.insert(out[i].node_id);
  observer->last_drain_overflowed = result.overflowed;
  result.stopped = observer->stopped.load(std::memory_order_acquire);
  return result;
}

bool spi_observer_validate_capture(SpiObserver *observer, const uint64_t *changed, size_t count) {
  if (observer == nullptr || (count != 0 && changed == nullptr) ||
      observer->stopped.load(std::memory_order_acquire)) return false;
  if (observer->last_drain_overflowed) return true;
  for (size_t i = 0; i < count; ++i) {
    if (!observer->last_drain_nodes.contains(changed[i])) {
      observer->stopped.store(true, std::memory_order_release);
      return false;
    }
  }
  return true;
}

bool spi_observer_disarm(SpiObserver *observer) {
  if (observer == nullptr) return false;
  std::lock_guard lock(observer->lifecycle_mutex);
  if (observer->teardown_failed) return false;
  bool clean = true;
  if (observer->redraw_target != nullptr) {
    std::array<uint8_t, kPatchSize> expected_patch{};
    write_absolute_jump(expected_patch.data(), reinterpret_cast<void *>(hook_redraw));
    if (std::memcmp(observer->redraw_target, expected_patch.data(), kPatchSize) != 0 ||
        !set_page_permissions(observer->redraw_target, PROT_READ | PROT_WRITE | PROT_EXEC)) {
      clean = false;
    } else {
      std::memcpy(observer->redraw_target, observer->redraw_original.data(), kPatchSize);
      __builtin___clear_cache(reinterpret_cast<char *>(observer->redraw_target),
                              reinterpret_cast<char *>(observer->redraw_target) + kPatchSize);
      clean = set_page_permissions(observer->redraw_target, PROT_READ | PROT_EXEC);
    }
    observer->redraw_target = nullptr;
  }
  if (observer->server_object != nullptr) {
    void *current = std::atomic_ref<void *>(*reinterpret_cast<void **>(observer->server_object))
                        .load(std::memory_order_acquire);
    if (current != observer->shadow_vtable.get()) {
      clean = false;
    } else {
      std::atomic_ref<void *>(*reinterpret_cast<void **>(observer->server_object))
          .store(observer->original_vtable, std::memory_order_release);
    }
    observer->server_object = nullptr;
  }
  if (!clean) {
    observer->teardown_failed = true;
    observer->stopped.store(true, std::memory_order_release);
    return false;
  }
  SpiObserver *active = observer;
  active_observer.compare_exchange_strong(active, nullptr, std::memory_order_acq_rel);
  if (observer->redraw_trampoline != nullptr) {
    munmap(observer->redraw_trampoline, observer->redraw_mapping_size);
    observer->redraw_trampoline = nullptr;
  }
  observer->shadow_vtable.reset();
  observer->stopped.store(false, std::memory_order_release);
  return true;
}

bool spi_observer_is_stopped(const SpiObserver *observer) {
  return observer == nullptr || observer->stopped.load(std::memory_order_acquire);
}
}
