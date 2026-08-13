#pragma once

#include <array>
#include <cassert>
#include <cstdint>
#include <string>
#include <string_view>

namespace guance::test {

namespace detail {

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

inline uint32_t reverse_bits(uint32_t value, int bit_count) {
    uint32_t reversed = 0;
    for (int index = 0; index < bit_count; ++index) {
        reversed = (reversed << 1) | (value & 1u);
        value >>= 1u;
    }
    return reversed;
}

class BitReader final {
public:
    explicit BitReader(std::string_view input) : input_(input) {}

    uint32_t read_bits(int bit_count) {
        uint32_t value = 0;
        for (int index = 0; index < bit_count; ++index) {
            assert(byte_offset_ < input_.size());
            const auto byte = static_cast<unsigned char>(input_[byte_offset_]);
            value |= static_cast<uint32_t>((byte >> bit_offset_) & 1u) << index;
            if (++bit_offset_ == 8) {
                bit_offset_ = 0;
                ++byte_offset_;
            }
        }
        return value;
    }

private:
    std::string_view input_;
    std::size_t byte_offset_ = 0;
    int bit_offset_ = 0;
};

inline int decode_fixed_symbol(BitReader& reader) {
    uint32_t code = 0;
    for (int bit_count = 1; bit_count <= 9; ++bit_count) {
        code |= reader.read_bits(1) << (bit_count - 1);
        int first = 0;
        int last = -1;
        if (bit_count == 7) {
            first = 256;
            last = 279;
        } else if (bit_count == 8) {
            first = 0;
            last = 143;
            for (int symbol = 280; symbol <= 287; ++symbol) {
                const auto canonical = static_cast<uint32_t>(0xc0 + symbol - 280);
                if (code == reverse_bits(canonical, 8)) return symbol;
            }
        } else if (bit_count == 9) {
            first = 144;
            last = 255;
        }
        for (int symbol = first; symbol <= last; ++symbol) {
            uint32_t canonical = 0;
            if (symbol <= 143) canonical = static_cast<uint32_t>(0x30 + symbol);
            else if (symbol <= 255) canonical = static_cast<uint32_t>(0x190 + symbol - 144);
            else canonical = static_cast<uint32_t>(symbol - 256);
            if (code == reverse_bits(canonical, bit_count)) return symbol;
        }
    }
    assert(false && "invalid fixed DEFLATE symbol");
    return -1;
}

} // namespace detail

inline std::string inflate_raw_deflate(std::string_view input) {
    detail::BitReader reader(input);
    std::string output;
    bool final_block = false;
    while (!final_block) {
        final_block = reader.read_bits(1) != 0;
        const auto block_type = reader.read_bits(2);
        assert(block_type == 1 && "test decoder expects fixed Huffman DEFLATE");
        while (true) {
            const auto symbol = detail::decode_fixed_symbol(reader);
            if (symbol < 256) {
                output.push_back(static_cast<char>(symbol));
                continue;
            }
            if (symbol == 256) break;
            assert(symbol >= 257 && symbol <= 285);
            const auto length_index = static_cast<std::size_t>(symbol - 257);
            auto length = detail::kLengthBases[length_index];
            const auto length_extra_bits = detail::kLengthExtraBits[length_index];
            if (length_extra_bits > 0) {
                length += static_cast<int>(reader.read_bits(length_extra_bits));
            }

            const auto encoded_distance = reader.read_bits(5);
            const auto distance_symbol = detail::reverse_bits(encoded_distance, 5);
            assert(distance_symbol < detail::kDistanceBases.size());
            auto distance = detail::kDistanceBases[distance_symbol];
            const auto distance_extra_bits = detail::kDistanceExtraBits[distance_symbol];
            if (distance_extra_bits > 0) {
                distance += static_cast<int>(reader.read_bits(distance_extra_bits));
            }
            assert(distance > 0 && static_cast<std::size_t>(distance) <= output.size());
            for (int index = 0; index < length; ++index) {
                output.push_back(output[output.size() - static_cast<std::size_t>(distance)]);
            }
        }
    }
    return output;
}

inline std::string http_request_body(const std::string& request) {
    const auto body_start = request.find("\r\n\r\n");
    assert(body_start != std::string::npos);
    return request.substr(body_start + 4);
}

inline std::string inflate_http_request_body(const std::string& request) {
    assert(request.find("Content-Encoding: deflate\r\n") != std::string::npos);
    return inflate_raw_deflate(http_request_body(request));
}

} // namespace guance::test
