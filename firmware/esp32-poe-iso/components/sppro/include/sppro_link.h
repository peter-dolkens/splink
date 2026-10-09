// UART transport for the SP PRO link. Separated from sppro.h so the protocol code stays
// host-testable with no ESP-IDF dependency.
#ifndef SPPRO_LINK_H
#define SPPRO_LINK_H

#include "esp_err.h"
#include "hal/uart_types.h"
#include "sppro.h"

typedef struct {
    uart_port_t uart_num;
    int tx_gpio;
    int rx_gpio;
    int baud;
} sppro_link_config_t;

esp_err_t sppro_link_init(const sppro_link_config_t *cfg);
esp_err_t sppro_login(const char *password, int timeout_ms);
bool      sppro_is_logged_in(void);
void      sppro_mark_logged_out(void);
esp_err_t sppro_read_words(uint32_t address, int word_count, uint16_t *out, int timeout_ms);
esp_err_t sppro_read_scale(sppro_scale_t *scale, int timeout_ms);
esp_err_t sppro_read_fast(const sppro_scale_t *scale, sppro_fast_t *out, int timeout_ms);

#endif  // SPPRO_LINK_H
