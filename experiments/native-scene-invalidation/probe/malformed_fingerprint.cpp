#include "spirectl_native_observer.h"

#include <array>
#include <cstdint>
#include <cstdlib>
#include <cstdio>
#include <cstring>
#include <elf.h>
#include <limits>
#include <unistd.h>
#include <vector>

namespace {
template <typename T>
void put(std::vector<uint8_t> &bytes, size_t offset, const T &value) {
  std::memcpy(bytes.data() + offset, &value, sizeof(value));
}

bool rejects(const std::vector<uint8_t> &bytes, const char *case_name) {
  char path[] = "/tmp/spirectl-malformed-elf-XXXXXX";
  const int descriptor = mkstemp(path);
  if (descriptor < 0) return false;
  const bool written = write(descriptor, bytes.data(), bytes.size()) == static_cast<ssize_t>(bytes.size());
  close(descriptor);
  SpiFingerprint fingerprint{};
  const bool rejected = written && !spi_inspect_process(path, &fingerprint);
  unlink(path);
  if (!rejected) std::fprintf(stderr, "FAIL: malformed ELF accepted: %s\n", case_name);
  return rejected;
}
}

int main() {
  std::vector<uint8_t> bytes(sizeof(Elf64_Ehdr) + sizeof(Elf64_Phdr) + sizeof(Elf64_Nhdr));
  Elf64_Ehdr header{};
  std::memcpy(header.e_ident, ELFMAG, SELFMAG);
  header.e_ident[EI_CLASS] = ELFCLASS64;
  header.e_phentsize = sizeof(Elf64_Phdr);
  header.e_phnum = 1;
  header.e_phoff = sizeof(Elf64_Ehdr);

  header.e_phoff = std::numeric_limits<Elf64_Off>::max() - sizeof(Elf64_Phdr) + 1;
  put(bytes, 0, header);
  bool ok = rejects(bytes, "program table offset overflow");

  header.e_phoff = sizeof(Elf64_Ehdr);
  put(bytes, 0, header);
  Elf64_Phdr program{};
  program.p_type = PT_NOTE;
  program.p_offset = std::numeric_limits<Elf64_Off>::max() - 7;
  program.p_filesz = 16;
  put(bytes, sizeof(Elf64_Ehdr), program);
  ok &= rejects(bytes, "note segment offset overflow");

  program.p_offset = sizeof(Elf64_Ehdr) + sizeof(Elf64_Phdr);
  program.p_filesz = sizeof(Elf64_Nhdr);
  put(bytes, sizeof(Elf64_Ehdr), program);
  Elf64_Nhdr note{};
  note.n_namesz = std::numeric_limits<Elf64_Word>::max();
  note.n_descsz = std::numeric_limits<Elf64_Word>::max();
  note.n_type = NT_GNU_BUILD_ID;
  put(bytes, static_cast<size_t>(program.p_offset), note);
  ok &= rejects(bytes, "note name and description bounds");

  std::printf("malformed ELF fingerprint rejection: %s\n", ok ? "PASS" : "FAIL");
  return ok ? 0 : 1;
}
