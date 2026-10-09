#include "sppro.h"

#include <string.h>

#include "mbedtls/md5.h"

// MD5 over (16-byte challenge || password space-padded to 32 ASCII bytes), packed as eight
// little-endian words. This is the only write this firmware performs: the SP PRO will not
// answer register reads until the challenge is satisfied, so it is not optional. Every other
// write path is deliberately absent.
bool sppro_login_response(const uint8_t challenge[SPPRO_CHALLENGE_LEN],
                          const char *password, uint16_t out_words[8]) {
    size_t len = strlen(password);
    if (len > SPPRO_PASSWORD_LEN) return false;

    uint8_t material[SPPRO_CHALLENGE_LEN + SPPRO_PASSWORD_LEN];
    memcpy(material, challenge, SPPRO_CHALLENGE_LEN);
    // Space padding, not NUL: SP LINK pads with 0x20 and the digest differs if we do otherwise.
    memset(material + SPPRO_CHALLENGE_LEN, ' ', SPPRO_PASSWORD_LEN);
    memcpy(material + SPPRO_CHALLENGE_LEN, password, len);

    uint8_t digest[16];
    if (mbedtls_md5(material, sizeof material, digest) != 0) return false;

    for (int i = 0; i < 8; i++)
        out_words[i] = (uint16_t)(digest[i * 2] | (digest[i * 2 + 1] << 8));
    return true;
}
