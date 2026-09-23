#include "spirectl_native_observer.h"

#include <array>
#include <atomic>
#include <cerrno>
#include <cstdio>
#include <cstring>
#include <set>
#include <string>
#include <sys/mman.h>
#include <sys/syscall.h>
#include <sys/wait.h>
#include <unistd.h>

namespace {
std::atomic<uintptr_t> fail_rx_page{};
std::atomic<bool> fail_rx_once{};

void fail_next_rx_for(void *address) {
  const auto page_size = static_cast<uintptr_t>(sysconf(_SC_PAGESIZE));
  fail_rx_page.store(reinterpret_cast<uintptr_t>(address) & ~(page_size - 1),
                     std::memory_order_release);
  fail_rx_once.store(true, std::memory_order_release);
}
}

// Interpose only inside the generated fixture executable. The observer still calls the real kernel operation;
// one armed RX transition for the selected code page fails after the detour bytes have already been written.
extern "C" __attribute__((visibility("default"), noinline))
int mprotect(void *address, size_t length, int protection) noexcept {
  const auto selected_page = fail_rx_page.load(std::memory_order_acquire);
  if (protection == (PROT_READ | PROT_EXEC) && selected_page != 0 &&
      reinterpret_cast<uintptr_t>(address) == selected_page &&
      fail_rx_once.exchange(false, std::memory_order_acq_rel)) {
    errno = EACCES;
    return -1;
  }
  return static_cast<int>(syscall(SYS_mprotect, address, length, protection));
}

namespace {
struct FakeRenderingServer {
  virtual void canvas_transform(uint64_t, const void *) { ++calls; }
  virtual void canvas_visible(uint64_t, bool) { ++calls; }
  virtual void canvas_modulate(uint64_t, const void *) { ++calls; }
  virtual void canvas_self_modulate(uint64_t, const void *) { ++calls; }
  virtual void canvas_z(uint64_t, int32_t) { ++calls; }
  virtual void viewport_transform(uint64_t, uint64_t, const void *) { ++calls; }
  virtual void material_parameter(uint64_t, const void *, const void *) { ++calls; }
  virtual void particle_state(uint64_t, bool) { ++calls; }
  uint64_t calls{};
};

extern "C" __attribute__((naked, noinline, visibility("default"))) void fixture_queue_redraw(uint64_t) {
  asm volatile(".rept 12\n\tnop\n\t.endr\n\tret\n");
}

__attribute__((noinline)) void mutate(FakeRenderingServer *server, size_t slot, uint64_t rid) {
  switch (slot) {
    case 0: server->canvas_transform(rid, nullptr); break;
    case 1: server->canvas_visible(rid, true); break;
    case 2: server->canvas_modulate(rid, nullptr); break;
    case 3: server->canvas_self_modulate(rid, nullptr); break;
    case 4: server->canvas_z(rid, 1); break;
    case 5: server->viewport_transform(99, rid, nullptr); break;
    case 6: server->material_parameter(rid, nullptr, nullptr); break;
    case 7: server->particle_state(rid, true); break;
  }
}

bool check(bool condition, const char *message) {
  if (!condition) std::fprintf(stderr, "FAIL: %s\n", message);
  return condition;
}
}

int main(int argc, char **argv) {
  (void)argc;
  bool ok = true;
  SpiFingerprint own{};
  ok &= check(spi_inspect_process("/proc/self/exe", &own), "inspect ELF fingerprint");

  SpiObserver *rejected = spi_observer_create(8);
  SpiFingerprint wrong = own;
  wrong.sha256[0] ^= 0xff;
  ok &= check(!spi_observer_expect_process(rejected, &wrong), "reject wrong fingerprint");
  FakeRenderingServer untouched;
  const SpiSlotHook first_hook{0, SPI_CANVAS_TRANSFORM};
  void *untouched_vtable = *reinterpret_cast<void **>(&untouched);
  ok &= check(!spi_observer_arm_vtable(rejected, &untouched, 8, &first_hook, 1), "zero writes after mismatch");
  ok &= check(*reinterpret_cast<void **>(&untouched) == untouched_vtable, "mismatch preserved vptr");
  spi_observer_destroy(rejected);

  SpiObserver *observer = spi_observer_create(16);
  ok &= check(observer != nullptr, "create observer");
  ok &= check(spi_observer_expect_process(observer, &own), "accept exact fingerprint");
  for (uint64_t rid = 10; rid < 18; ++rid) ok &= check(spi_observer_map_rid(observer, rid, 1000 + rid), "map RID");

  FakeRenderingServer server;
  void *original_vtable = *reinterpret_cast<void **>(&server);
  const std::array<SpiSlotHook, 8> hooks{{
      {0, SPI_CANVAS_TRANSFORM}, {1, SPI_CANVAS_VISIBLE}, {2, SPI_CANVAS_MODULATE},
      {3, SPI_CANVAS_SELF_MODULATE}, {4, SPI_CANVAS_Z}, {5, SPI_VIEWPORT_CANVAS_TRANSFORM},
      {6, SPI_MATERIAL_PARAMETER}, {7, SPI_PARTICLE_STATE}}};
  const std::array<SpiSlotHook, 2> duplicate_kind{{{0, SPI_CANVAS_TRANSFORM}, {1, SPI_CANVAS_TRANSFORM}}};
  const std::array<SpiSlotHook, 2> duplicate_slot{{{0, SPI_CANVAS_TRANSFORM}, {0, SPI_CANVAS_VISIBLE}}};
  ok &= check(!spi_observer_arm_vtable(observer, &server, 8, duplicate_kind.data(), duplicate_kind.size()) &&
                  !spi_observer_arm_vtable(observer, &server, 8, duplicate_slot.data(), duplicate_slot.size()) &&
                  *reinterpret_cast<void **>(&server) == original_vtable,
              "ambiguous hook maps are rejected before changing the vptr");
  ok &= check(spi_observer_arm_vtable(observer, &server, 8, hooks.data(), hooks.size()), "arm shadow vtable");
  std::array<uint8_t, 12> target_prefix{};
  std::memcpy(target_prefix.data(), reinterpret_cast<void *>(fixture_queue_redraw), target_prefix.size());
  std::array<uint8_t, 12> wrong_prefix = target_prefix;
  wrong_prefix[0] ^= 0xff;
  ok &= check(!spi_observer_arm_queue_redraw(observer, reinterpret_cast<void *>(fixture_queue_redraw),
                                             wrong_prefix.data()),
              "reject unexpected queue_redraw target without writes");
  ok &= check(std::memcmp(reinterpret_cast<void *>(fixture_queue_redraw), target_prefix.data(),
                          target_prefix.size()) == 0,
              "target rejection preserved code");
  const size_t page_size = static_cast<size_t>(sysconf(_SC_PAGESIZE));
  void *cross_page = mmap(nullptr, page_size * 2, PROT_READ | PROT_WRITE,
                         MAP_PRIVATE | MAP_ANONYMOUS, -1, 0);
  ok &= check(cross_page != MAP_FAILED, "allocate boundary fixture");
  if (cross_page != MAP_FAILED) {
    auto *boundary_target = static_cast<uint8_t *>(cross_page) + page_size - 6;
    std::memcpy(boundary_target, target_prefix.data(), target_prefix.size());
    ok &= check(!spi_observer_arm_queue_redraw(observer, boundary_target, target_prefix.data()) &&
                    std::memcmp(boundary_target, target_prefix.data(), target_prefix.size()) == 0,
                "cross-page target rejected without writes");
    munmap(cross_page, page_size * 2);
  }
  ok &= check(spi_observer_arm_queue_redraw(observer, reinterpret_cast<void *>(fixture_queue_redraw),
                                            target_prefix.data()),
              "arm exact queue_redraw target");
  std::array<uint8_t, 12> installed_prefix{};
  std::memcpy(installed_prefix.data(), reinterpret_cast<void *>(fixture_queue_redraw), installed_prefix.size());
  ok &= check(!spi_observer_arm_queue_redraw(observer, reinterpret_cast<void *>(fixture_queue_redraw),
                                             installed_prefix.data()),
              "a second arm cannot replace the original trampoline or restoration bytes");

  for (size_t slot = 0; slot < hooks.size(); ++slot) mutate(&server, slot, 10 + slot);
  fixture_queue_redraw(10);
  // Public signals cover free/reparent and draw-affecting changes with no RenderingServer write.
  ok &= check(spi_observer_notify_node(observer, 2010, SPI_PUBLIC_SIGNAL), "publish free/reparent signal");
  ok &= check(spi_observer_notify_node(observer, 2011, SPI_PUBLIC_SIGNAL), "publish layout/text/theme/texture signal");

  std::array<SpiDirtyNode, 32> dirty{};
  const SpiDrainResult first = spi_observer_drain(observer, dirty.data(), dirty.size());
  ok &= check(!first.overflowed && !first.stopped && first.count == 11, "same-capture complete invalidation");
  std::set<SpiDirtyKind> kinds;
  for (size_t i = 0; i < first.count; ++i) kinds.insert(dirty[i].kind);
  for (uint32_t kind = SPI_CANVAS_TRANSFORM; kind <= SPI_PUBLIC_SIGNAL; ++kind)
    ok &= check(kinds.count(static_cast<SpiDirtyKind>(kind)) != 0, "all seam families observed");
  ok &= check(server.calls == 8, "original RenderingServer methods preserved");
  std::array<uint64_t, 10> changed{{1010, 1011, 1012, 1013, 1014, 1015, 1016, 1017, 2010, 2011}};
  ok &= check(spi_observer_validate_capture(observer, changed.data(), changed.size()),
              "authoritative shadow agrees in same capture");

  // A bounded ring reports overflow so the consumer performs a full authoritative capture.
  for (size_t i = 0; i < 40; ++i) spi_observer_notify_node(observer, 3000 + i, SPI_PUBLIC_SIGNAL);
  const SpiDrainResult overflow = spi_observer_drain(observer, dirty.data(), dirty.size());
  ok &= check(overflow.overflowed, "overflow forces full capture");

  ok &= check(spi_observer_disarm(observer), "clean disarm");
  ok &= check(*reinterpret_cast<void **>(&server) == original_vtable, "vtable restored");
  ok &= check(std::memcmp(reinterpret_cast<void *>(fixture_queue_redraw), target_prefix.data(),
                          target_prefix.size()) == 0,
              "queue_redraw bytes restored");
  mutate(&server, 0, 10);
  fixture_queue_redraw(10);
  const SpiDrainResult after = spi_observer_drain(observer, dirty.data(), dirty.size());
  ok &= check(after.count == 0 && !after.stopped, "dormant after disarm");
  spi_observer_destroy(observer);

  // A poll-only change that had no native invalidation is a hard miss: stop incremental use.
  SpiObserver *miss = spi_observer_create(4);
  ok &= check(spi_observer_expect_process(miss, &own), "arm shadow validator");
  ok &= check(spi_observer_notify_node(miss, 4000, SPI_PUBLIC_SIGNAL), "shadow validator event");
  const SpiDrainResult before_miss = spi_observer_drain(miss, dirty.data(), dirty.size());
  ok &= check(before_miss.count == 1, "shadow validator drain");
  const uint64_t silent_change = 4001;
  ok &= check(!spi_observer_validate_capture(miss, &silent_change, 1) && spi_observer_is_stopped(miss),
              "missed invalidation fails and stops");
  spi_observer_destroy(miss);

  // A competing vptr write makes restoration unverifiable. Fail and retain backing storage safely.
  const pid_t child = fork();
  if (child == 0) {
    SpiObserver *unsafe = spi_observer_create(4);
    FakeRenderingServer child_server;
    void *child_original = *reinterpret_cast<void **>(&child_server);
    if (!spi_observer_expect_process(unsafe, &own) ||
        !spi_observer_arm_vtable(unsafe, &child_server, 8, hooks.data(), hooks.size())) _exit(2);
    *reinterpret_cast<void **>(&child_server) = child_original;
    const bool refused = !spi_observer_disarm(unsafe) && spi_observer_is_stopped(unsafe);
    _exit(refused ? 0 : 3);
  }
  int child_status = 0;
  ok &= check(child > 0 && waitpid(child, &child_status, 0) == child && WIFEXITED(child_status) &&
                  WEXITSTATUS(child_status) == 0,
              "unverifiable teardown fails and stops without freeing hook backing");

  // Fail the final RWX -> RX transition after queue_redraw has been detoured. Arm must fail and stop, but disarm
  // still owns the live patch: it restores the original bytes before releasing the trampoline. Keep this in a
  // child so a bad detour restoration is an ordinary fixture failure rather than killing the probe.
  const pid_t rx_failure_child = fork();
  if (rx_failure_child == 0) {
    SpiObserver *failed_arm = spi_observer_create(4);
    FakeRenderingServer child_server;
    std::array<uint8_t, 12> original_prefix{};
    std::memcpy(original_prefix.data(), reinterpret_cast<void *>(fixture_queue_redraw),
                original_prefix.size());
    if (!failed_arm || !spi_observer_expect_process(failed_arm, &own) ||
        !spi_observer_arm_vtable(failed_arm, &child_server, 8, hooks.data(), hooks.size())) _exit(4);

    fail_next_rx_for(reinterpret_cast<void *>(fixture_queue_redraw));
    const bool arm_failed =
        !spi_observer_arm_queue_redraw(failed_arm, reinterpret_cast<void *>(fixture_queue_redraw),
                                      original_prefix.data()) &&
        spi_observer_is_stopped(failed_arm);
    const bool detour_was_written =
        std::memcmp(reinterpret_cast<void *>(fixture_queue_redraw), original_prefix.data(),
                    original_prefix.size()) != 0;
    const bool disarmed = spi_observer_disarm(failed_arm);
    const bool restored =
        std::memcmp(reinterpret_cast<void *>(fixture_queue_redraw), original_prefix.data(),
                    original_prefix.size()) == 0;

    // Restoration must leave the original target callable, as well as byte-identical.
    fixture_queue_redraw(7000);
    spi_observer_destroy(failed_arm);
    _exit(arm_failed && detour_was_written && disarmed && restored ? 0 : 5);
  }
  int rx_failure_status = 0;
  ok &= check(rx_failure_child > 0 &&
                  waitpid(rx_failure_child, &rx_failure_status, 0) == rx_failure_child &&
                  WIFEXITED(rx_failure_status) && WEXITSTATUS(rx_failure_status) == 0,
              "failed RX restore is disarmed safely and the original call remains usable");

  std::printf("native observer probe: %s (%s)\n", ok ? "PASS" : "FAIL", argv[0]);
  return ok ? 0 : 1;
}
