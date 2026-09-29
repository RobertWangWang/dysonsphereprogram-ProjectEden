// Build with the unmodified upstream smolv.cpp and smolv.h.
#include "smolv.h"
#include <fstream>
#include <iterator>
#include <iostream>

int main(int argc, char** argv) {
    if (argc != 3) return 2;
    std::ifstream input(argv[1], std::ios::binary);
    if (!input) return 3;
    smolv::ByteArray source((std::istreambuf_iterator<char>(input)), std::istreambuf_iterator<char>());
    size_t size = smolv::GetDecodedBufferSize(source.data(), source.size());
    if (size == 0 || size > 256 * 1024 * 1024 || size % 4) return 4;
    smolv::ByteArray decoded(size);
    if (!smolv::Decode(source.data(), source.size(), decoded.data(), decoded.size())) return 5;
    smolv::ByteArray encoded;
    if (!smolv::Encode(decoded.data(), decoded.size(), encoded)) return 6;
    size_t checkSize = smolv::GetDecodedBufferSize(encoded.data(), encoded.size());
    if (checkSize != size) return 7;
    smolv::ByteArray check(checkSize);
    if (!smolv::Decode(encoded.data(), encoded.size(), check.data(), check.size()) || check != decoded) return 8;
    std::ofstream output(argv[2], std::ios::binary);
    output.write(reinterpret_cast<const char*>(decoded.data()), decoded.size());
    return output ? 0 : 9;
}
