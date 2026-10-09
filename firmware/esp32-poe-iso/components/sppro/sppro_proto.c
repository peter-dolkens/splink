#include "sppro.h"

#include <string.h>

// --- CRC-16/KERMIT -------------------------------------------------------------------------
// Computed on the fly rather than from a 512-byte table: the frames here are at most a few
// hundred bytes and run at a few hertz, so the table would cost more RAM than it saves time.
uint16_t sppro_crc16(const uint8_t *data, size_t len) {
    uint16_t crc = 0;
    for (size_t i = 0; i < len; i++) {
        crc ^= data[i];
        for (int bit = 0; bit < 8; bit++)
            crc = (crc & 1) ? (uint16_t)((crc >> 1) ^ 0x8408u) : (uint16_t)(crc >> 1);
    }
    return crc;
}

bool sppro_crc_ok(const uint8_t *frame_with_crc, size_t len) {
    return sppro_crc16(frame_with_crc, len) == 0;
}

// --- Framing -------------------------------------------------------------------------------
static void write_header(uint8_t *out, uint8_t op, uint32_t address, int word_count) {
    out[0] = op;
    out[1] = (uint8_t)(word_count - 1);
    out[2] = (uint8_t)(address & 0xFF);
    out[3] = (uint8_t)((address >> 8) & 0xFF);
    out[4] = (uint8_t)((address >> 16) & 0xFF);
    out[5] = (uint8_t)((address >> 24) & 0xFF);
    uint16_t crc = sppro_crc16(out, 6);
    out[6] = (uint8_t)crc;
    out[7] = (uint8_t)(crc >> 8);
}

bool sppro_build_read(uint8_t out[SPPRO_HEADER_LEN], uint32_t address, int word_count) {
    if (word_count < 1 || word_count > SPPRO_MAX_WORDS) return false;
    write_header(out, SPPRO_OP_READ, address, word_count);
    return true;
}

bool sppro_build_write(uint8_t *out, uint32_t address, const uint16_t *words, int word_count) {
    if (word_count < 1 || word_count > SPPRO_MAX_WORDS) return false;
    write_header(out, SPPRO_OP_WRITE, address, word_count);
    for (int i = 0; i < word_count; i++) {
        out[SPPRO_HEADER_LEN + i * 2]     = (uint8_t)(words[i] & 0xFF);
        out[SPPRO_HEADER_LEN + i * 2 + 1] = (uint8_t)(words[i] >> 8);
    }
    // The data CRC covers the data bytes only, not the header.
    uint16_t crc = sppro_crc16(out + SPPRO_HEADER_LEN, (size_t)word_count * 2);
    out[SPPRO_HEADER_LEN + word_count * 2]     = (uint8_t)crc;
    out[SPPRO_HEADER_LEN + word_count * 2 + 1] = (uint8_t)(crc >> 8);
    return true;
}

bool sppro_parse_response(const uint8_t *frame, size_t len, uint16_t *words, int word_count) {
    if (word_count < 1 || word_count > SPPRO_MAX_WORDS) return false;
    if (len != sppro_response_len(word_count)) return false;
    if (frame[0] != SPPRO_OP_READ && frame[0] != SPPRO_OP_WRITE) return false;
    // The device echoes wordCount-1; a mismatch means we are parsing a frame we did not ask for.
    if (frame[1] != (uint8_t)(word_count - 1)) return false;
    if (!sppro_crc_ok(frame, SPPRO_HEADER_LEN)) return false;
    if (!sppro_crc_ok(frame + SPPRO_HEADER_LEN, (size_t)word_count * 2 + SPPRO_CRC_LEN)) return false;
    for (int i = 0; i < word_count; i++)
        words[i] = (uint16_t)(frame[SPPRO_HEADER_LEN + i * 2] |
                              (frame[SPPRO_HEADER_LEN + i * 2 + 1] << 8));
    return true;
}

// --- Scaling -------------------------------------------------------------------------------
bool sppro_scale_from_words(const uint16_t *words, int count, sppro_scale_t *out) {
    if (count < 6) return false;
    out->ac_volts       = (int16_t)words[0];
    out->ac_current     = (int16_t)words[1];
    out->dc_volts       = (int16_t)words[2];
    out->dc_current     = (int16_t)words[3];
    out->temperature    = (int16_t)words[4];
    out->internal_volts = (int16_t)words[5];
    return true;
}

static int32_t signed32(uint16_t lo, uint16_t hi) {
    return (int32_t)((uint32_t)lo | ((uint32_t)hi << 16));
}

// Grouped exactly as the C# is, because floating-point multiplication is not associative and
// regrouping these would make the two implementations disagree in the last bits.
static double ac_kw32(const sppro_scale_t *s, uint16_t lo, uint16_t hi) {
    return signed32(lo, hi) * (s->ac_volts * (s->ac_current / 26214400.0 / 1000.0));
}

static double dc_amps32(const sppro_scale_t *s, uint16_t lo, uint16_t hi) {
    return signed32(lo, hi) * (s->dc_current / 327680.0);
}

static double dc_amps16(const sppro_scale_t *s, uint16_t raw) {
    return (int16_t)raw * (s->dc_current / 327680.0);
}

// --- Fast decode ---------------------------------------------------------------------------
void sppro_decode_fast(const uint16_t *run0, const uint16_t *run1,
                       const sppro_scale_t *scale, sppro_fast_t *out) {
    // run1 starts at Now-block offset 34, so word N is run1[N - 34].
    #define W(n) (run1[(n) - SPPRO_FAST_RUN1_OFF])
    out->inverter_ac_kw = ac_kw32(scale, run0[0], run0[1]);
    out->ac_load_kw     = ac_kw32(scale, W(34), W(35));
    out->charger_state     = sppro_charger_state_name(W(36));
    out->inverter_mode     = sppro_inverter_mode_name(W(37));
    out->ac_source_status  = sppro_ac_source_status_name(W(40));
    // 0xFFFF is the "not known" sentinel, not a reading of 255.996%.
    out->soc_valid = W(41) != 0xFFFFu;
    out->battery_soc_percent = out->soc_valid ? W(41) / 256.0 : 0.0;
    out->dc_amps      = dc_amps16(scale, W(43));
    out->battery_amps = dc_amps32(scale, W(44), W(45));
    #undef W
}
