#include "spirectl_native_observer.h"

#include <cstring>
#include <cstdio>
#include <string_view>

namespace {
bool parse_hex(std::string_view text, uint8_t *out, size_t size) {
  if (text.size() != size * 2) return false;
  auto nibble = [](char digit) -> int {
    if (digit >= '0' && digit <= '9') return digit - '0';
    if (digit >= 'a' && digit <= 'f') return digit - 'a' + 10;
    if (digit >= 'A' && digit <= 'F') return digit - 'A' + 10;
    return -1;
  };
  for (size_t i = 0; i < size; ++i) {
    const int high = nibble(text[i * 2]);
    const int low = nibble(text[i * 2 + 1]);
    if (high < 0 || low < 0) return false;
    out[i] = static_cast<uint8_t>((high << 4) | low);
  }
  return true;
}

struct FakeTarget { virtual void mutation(uint64_t, uint64_t) {} };
}

int main(int argc, char **argv) {
  if (argc == 2 && std::strcmp(argv[1], "--self-test-hex") == 0) {
    uint8_t parsed[2]{};
    const bool valid = parse_hex("00fF", parsed, 2) && parsed[0] == 0 && parsed[1] == 0xff;
    const bool invalid = !parse_hex("fZ", parsed, 1) && !parse_hex("Zf", parsed, 1) &&
                         !parse_hex("f ", parsed, 1) && !parse_hex("f", parsed, 1) &&
                         !parse_hex("0x", parsed, 1) && !parse_hex("00f", parsed, 2);
    if (!valid || !invalid) return 1;
    std::puts("exact hex parsing: PASS");
    return 0;
  }
  if (argc != 4) {
    std::fprintf(stderr, "usage: %s EXECUTABLE SHA256 BUILD_ID\n", argv[0]);
    return 2;
  }
  SpiFingerprint expected{};
  expected.build_id_size = std::strlen(argv[3]) / 2;
  if (!parse_hex(argv[2], expected.sha256, sizeof(expected.sha256)) ||
      expected.build_id_size > sizeof(expected.build_id) ||
      !parse_hex(argv[3], expected.build_id, expected.build_id_size)) return 2;
  SpiFingerprint installed{};
  if (!spi_inspect_process(argv[1], &installed) ||
      std::memcmp(&installed, &expected, sizeof(expected)) != 0) {
    std::fprintf(stderr, "installed fingerprint mismatch\n");
    return 1;
  }

  // This executable is deliberately a different build. Prove the in-process gate refuses all writes.
  SpiObserver *observer = spi_observer_create(4);
  FakeTarget target;
  void *before = *reinterpret_cast<void **>(&target);
  const SpiSlotHook hook{0, SPI_CANVAS_TRANSFORM};
  const bool accepted = spi_observer_expect_process(observer, &expected);
  const bool armed = spi_observer_arm_vtable(observer, &target, 1, &hook, 1);
  const bool untouched = before == *reinterpret_cast<void **>(&target);
  spi_observer_destroy(observer);
  if (accepted || armed || !untouched) {
    std::fprintf(stderr, "fail-closed preflight failed\n");
    return 1;
  }
  std::printf("installed fingerprint: MATCH; foreign process: REFUSED; memory writes: ZERO\n");
  return 0;
}
