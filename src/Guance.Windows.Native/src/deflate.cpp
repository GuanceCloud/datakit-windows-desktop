#include "deflate.h"

#include <algorithm>
#include <array>
#include <cstdint>
#include <limits>
#include <utility>
#include <vector>

namespace guance::rum {

namespace {

constexpr std::array<int, 29> kLengthBases{
    3, 4, 5, 6, 7, 8, 9, 10,
    11, 13, 15, 17, 19, 23, 27, 31,
    35, 43, 51, 59, 67, 83, 99, 115,
    131, 163, 195, 227, 258};
constexpr std::array<int, 29> kLengthExtraBits{
    0, 0, 0, 0, 0, 0, 0, 0,
    1, 1, 1, 1, 2, 2, 2, 2,
    3, 3, 3, 3, 4, 4, 4, 4,
    5, 5, 5, 5, 0};
constexpr std::array<int, 30> kDistanceBases{
    1, 2, 3, 4, 5, 7, 9, 13,
    17, 25, 33, 49, 65, 97, 129, 193,
    257, 385, 513, 769, 1025, 1537, 2049, 3073,
    4097, 6145, 8193, 12289, 16385, 24577};
constexpr std::array<int, 30> kDistanceExtraBits{
    0, 0, 0, 0, 1, 1, 2, 2,
    3, 3, 4, 4, 5, 5, 6, 6,
    7, 7, 8, 8, 9, 9, 10, 10,
    11, 11, 12, 12, 13, 13};

uint32_t reverse_bits(uint32_t value, int bit_count) {
    uint32_t reversed = 0;
    for (int index = 0; index < bit_count; ++index) {
        reversed = (reversed << 1) | (value & 1u);
        value >>= 1u;
    }
    return reversed;
}

class BitWriter final {
public:
    explicit BitWriter(std::size_t expected_size) {
        output_.reserve(expected_size);
    }

    void write_bits(uint32_t value, int bit_count) {
        bits_ |= static_cast<uint64_t>(value) << bit_count_;
        bit_count_ += bit_count;
        while (bit_count_ >= 8) {
            output_.push_back(static_cast<char>(bits_ & 0xffu));
            bits_ >>= 8u;
            bit_count_ -= 8;
        }
    }

    std::string finish() {
        if (bit_count_ > 0) {
            output_.push_back(static_cast<char>(bits_ & 0xffu));
        }
        return std::move(output_);
    }

private:
    std::string output_;
    uint64_t bits_ = 0;
    int bit_count_ = 0;
};

void write_fixed_symbol(BitWriter& writer, int symbol) {
    if (symbol <= 143) {
        writer.write_bits(reverse_bits(static_cast<uint32_t>(0x30 + symbol), 8), 8);
    } else if (symbol <= 255) {
        writer.write_bits(reverse_bits(static_cast<uint32_t>(0x190 + symbol - 144), 9), 9);
    } else if (symbol <= 279) {
        writer.write_bits(reverse_bits(static_cast<uint32_t>(symbol - 256), 7), 7);
    } else {
        writer.write_bits(reverse_bits(static_cast<uint32_t>(0xc0 + symbol - 280), 8), 8);
    }
}

void write_length(BitWriter& writer, int length) {
    for (std::size_t index = 0; index < kLengthBases.size(); ++index) {
        const auto extra_bits = kLengthExtraBits[index];
        const auto maximum = kLengthBases[index] +
            (extra_bits == 0 ? 0 : (1 << extra_bits) - 1);
        if (length > maximum) {
            continue;
        }
        write_fixed_symbol(writer, 257 + static_cast<int>(index));
        if (extra_bits > 0) {
            writer.write_bits(
                static_cast<uint32_t>(length - kLengthBases[index]),
                extra_bits);
        }
        return;
    }
}

void write_distance(BitWriter& writer, int distance) {
    for (std::size_t index = 0; index < kDistanceBases.size(); ++index) {
        const auto extra_bits = kDistanceExtraBits[index];
        const auto maximum = kDistanceBases[index] +
            (extra_bits == 0 ? 0 : (1 << extra_bits) - 1);
        if (distance > maximum) {
            continue;
        }
        writer.write_bits(reverse_bits(static_cast<uint32_t>(index), 5), 5);
        if (extra_bits > 0) {
            writer.write_bits(
                static_cast<uint32_t>(distance - kDistanceBases[index]),
                extra_bits);
        }
        return;
    }
}

std::size_t hash_at(std::string_view input, std::size_t position) {
    const auto first = static_cast<unsigned char>(input[position]);
    const auto second = static_cast<unsigned char>(input[position + 1]);
    const auto third = static_cast<unsigned char>(input[position + 2]);
    return ((static_cast<std::size_t>(first) * 251u + second) * 251u + third) & 0xffffu;
}

uint32_t adler32(std::string_view input) {
    constexpr uint32_t modulus = 65521;
    uint32_t first = 1;
    uint32_t second = 0;
    for (const unsigned char byte : input) {
        first = (first + byte) % modulus;
        second = (second + first) % modulus;
    }
    return (second << 16u) | first;
}

} // namespace

std::string deflate_compress(std::string_view input) {
    BitWriter writer(input.size());
    writer.write_bits(1, 1); // BFINAL
    writer.write_bits(1, 2); // BTYPE=01, fixed Huffman codes

    constexpr auto no_position = std::numeric_limits<std::size_t>::max();
    constexpr std::size_t window_size = 32768;
    constexpr std::size_t maximum_match = 258;
    constexpr int maximum_chain_search = 128;
    std::array<std::size_t, 65536> heads{};
    heads.fill(no_position);
    std::vector<std::size_t> previous(input.size(), no_position);

    const auto insert = [&](std::size_t position) {
        if (position + 2 >= input.size()) {
            return no_position;
        }
        const auto hash = hash_at(input, position);
        const auto candidate = heads[hash];
        previous[position] = candidate;
        heads[hash] = position;
        return candidate;
    };

    std::size_t position = 0;
    while (position < input.size()) {
        std::size_t best_length = 0;
        std::size_t best_distance = 0;
        auto candidate = insert(position);
        const auto match_limit = std::min(maximum_match, input.size() - position);

        for (int searched = 0;
             candidate != no_position && searched < maximum_chain_search;
             ++searched) {
            if (candidate >= position || position - candidate > window_size) {
                candidate = previous[candidate];
                continue;
            }
            if (best_length < match_limit &&
                input[candidate + best_length] != input[position + best_length]) {
                candidate = previous[candidate];
                continue;
            }
            std::size_t length = 0;
            while (length < match_limit &&
                   input[candidate + length] == input[position + length]) {
                ++length;
            }
            if (length >= 3 && length > best_length) {
                best_length = length;
                best_distance = position - candidate;
                if (length == match_limit) {
                    break;
                }
            }
            candidate = previous[candidate];
        }

        if (best_length >= 3) {
            write_length(writer, static_cast<int>(best_length));
            write_distance(writer, static_cast<int>(best_distance));
            for (std::size_t offset = 1; offset < best_length; ++offset) {
                insert(position + offset);
            }
            position += best_length;
        } else {
            write_fixed_symbol(writer, static_cast<unsigned char>(input[position]));
            ++position;
        }
    }

    write_fixed_symbol(writer, 256);
    auto raw_deflate = writer.finish();

    std::string output;
    output.reserve(raw_deflate.size() + 6);
    output.push_back(static_cast<char>(0x78)); // DEFLATE with a 32 KiB window.
    output.push_back(static_cast<char>(0x01)); // Fastest compression, no dictionary.
    output += raw_deflate;

    const auto checksum = adler32(input);
    output.push_back(static_cast<char>((checksum >> 24u) & 0xffu));
    output.push_back(static_cast<char>((checksum >> 16u) & 0xffu));
    output.push_back(static_cast<char>((checksum >> 8u) & 0xffu));
    output.push_back(static_cast<char>(checksum & 0xffu));
    return output;
}

} // namespace guance::rum
