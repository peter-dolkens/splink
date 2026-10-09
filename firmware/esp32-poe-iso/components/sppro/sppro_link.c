// UART transport: one exchange at a time, with the login handshake the device demands.
#include "sppro_link.h"

#include <string.h>

#include "driver/uart.h"
#include "esp_log.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"

static const char *TAG = "sppro";

// One request/response at a time. The SP PRO has no multiplexing, so a second caller mid-frame
// would read someone else's bytes; this is the same serialisation splink does host-side.
static SemaphoreHandle_t s_lock;
static uart_port_t s_uart;
static bool s_logged_in;

esp_err_t sppro_link_init(const sppro_link_config_t *cfg) {
    s_uart = cfg->uart_num;
    s_lock = xSemaphoreCreateMutex();
    if (!s_lock) return ESP_ERR_NO_MEM;

    const uart_config_t uart = {
        .baud_rate = cfg->baud,
        .data_bits = UART_DATA_8_BITS,
        .parity    = UART_PARITY_DISABLE,
        .stop_bits = UART_STOP_BITS_1,
        // 8N1, no flow control -- matches what the protocol notes record for this link.
        .flow_ctrl = UART_HW_FLOWCTRL_DISABLE,
        .source_clk = UART_SCLK_DEFAULT,
    };
    ESP_ERROR_CHECK(uart_driver_install(s_uart, 1024, 0, 0, NULL, 0));
    ESP_ERROR_CHECK(uart_param_config(s_uart, &uart));
    ESP_ERROR_CHECK(uart_set_pin(s_uart, cfg->tx_gpio, cfg->rx_gpio,
                                 UART_PIN_NO_CHANGE, UART_PIN_NO_CHANGE));
    s_logged_in = false;
    return ESP_OK;
}

// Sends a frame and reads back exactly `want` bytes. Resynchronises on a start byte, because a
// late reply to a previous request can otherwise shift every subsequent frame by a few bytes.
static esp_err_t exchange(const uint8_t *req, size_t req_len,
                          uint8_t *resp, size_t want, int timeout_ms) {
    uart_flush_input(s_uart);
    if (uart_write_bytes(s_uart, req, req_len) != (int)req_len) return ESP_FAIL;

    TickType_t deadline = xTaskGetTickCount() + pdMS_TO_TICKS(timeout_ms);
    size_t got = 0;
    while (got < want) {
        TickType_t now = xTaskGetTickCount();
        if (now >= deadline) return ESP_ERR_TIMEOUT;
        int n = uart_read_bytes(s_uart, resp + got, want - got, deadline - now);
        if (n <= 0) return ESP_ERR_TIMEOUT;
        got += (size_t)n;
        // The first byte must be an opcode; if it is not, drop bytes until one is.
        if (got > 0 && resp[0] != SPPRO_OP_READ && resp[0] != SPPRO_OP_WRITE) {
            size_t skip = 1;
            while (skip < got && resp[skip] != SPPRO_OP_READ && resp[skip] != SPPRO_OP_WRITE) skip++;
            memmove(resp, resp + skip, got - skip);
            got -= skip;
        }
    }
    return ESP_OK;
}

esp_err_t sppro_read_words(uint32_t address, int word_count, uint16_t *out, int timeout_ms) {
    if (word_count < 1 || word_count > SPPRO_MAX_WORDS) return ESP_ERR_INVALID_ARG;
    uint8_t req[SPPRO_HEADER_LEN];
    if (!sppro_build_read(req, address, word_count)) return ESP_ERR_INVALID_ARG;

    uint8_t resp[SPPRO_HEADER_LEN + SPPRO_MAX_WORDS * 2 + SPPRO_CRC_LEN];
    size_t want = sppro_response_len(word_count);

    xSemaphoreTake(s_lock, portMAX_DELAY);
    esp_err_t err = exchange(req, sizeof req, resp, want, timeout_ms);
    xSemaphoreGive(s_lock);
    if (err != ESP_OK) return err;

    return sppro_parse_response(resp, want, out, word_count) ? ESP_OK : ESP_ERR_INVALID_CRC;
}

esp_err_t sppro_login(const char *password, int timeout_ms) {
    uint16_t port = 0;
    esp_err_t err = sppro_read_words(SPPRO_REG_LINK_PORT, 1, &port, timeout_ms);
    if (err != ESP_OK) return err;
    if (port != 0xFFFFu) {            // already open: no challenge outstanding
        s_logged_in = true;
        return ESP_OK;
    }

    uint16_t challenge_words[8];
    err = sppro_read_words(SPPRO_REG_LOGIN_CHALLENGE, 8, challenge_words, timeout_ms);
    if (err != ESP_OK) return err;

    uint8_t challenge[SPPRO_CHALLENGE_LEN];
    for (int i = 0; i < 8; i++) {
        challenge[i * 2]     = (uint8_t)(challenge_words[i] & 0xFF);
        challenge[i * 2 + 1] = (uint8_t)(challenge_words[i] >> 8);
    }

    uint16_t answer[8];
    if (!sppro_login_response(challenge, password, answer)) return ESP_ERR_INVALID_ARG;

    // The one write this firmware performs. See README: there is no other write path, and the
    // helper that builds write frames exists solely to satisfy this handshake.
    uint8_t req[SPPRO_HEADER_LEN + 16 + SPPRO_CRC_LEN];
    if (!sppro_build_write(req, SPPRO_REG_LOGIN_CHALLENGE, answer, 8)) return ESP_ERR_INVALID_ARG;
    uint8_t echo[sizeof req];
    xSemaphoreTake(s_lock, portMAX_DELAY);
    err = exchange(req, sizeof req, echo, sizeof echo, timeout_ms);
    xSemaphoreGive(s_lock);
    if (err != ESP_OK) return err;

    uint16_t result = 0;
    err = sppro_read_words(SPPRO_REG_LOGIN_RESULT, 1, &result, timeout_ms);
    if (err != ESP_OK) return err;
    if (result != 1) {
        ESP_LOGE(TAG, "login rejected -- wrong password");
        return ESP_ERR_INVALID_STATE;
    }
    s_logged_in = true;
    return ESP_OK;
}

bool sppro_is_logged_in(void) { return s_logged_in; }
void sppro_mark_logged_out(void) { s_logged_in = false; }

esp_err_t sppro_read_scale(sppro_scale_t *scale, int timeout_ms) {
    uint16_t words[6];
    esp_err_t err = sppro_read_words(SPPRO_REG_COMMON_SCALE, 6, words, timeout_ms);
    if (err != ESP_OK) return err;
    return sppro_scale_from_words(words, 6, scale) ? ESP_OK : ESP_FAIL;
}

esp_err_t sppro_read_fast(const sppro_scale_t *scale, sppro_fast_t *out, int timeout_ms) {
    uint16_t run0[SPPRO_FAST_RUN0_LEN], run1[SPPRO_FAST_RUN1_LEN];
    esp_err_t err = sppro_read_words(SPPRO_REG_NOW_BLOCK + SPPRO_FAST_RUN0_OFF,
                                     SPPRO_FAST_RUN0_LEN, run0, timeout_ms);
    if (err != ESP_OK) return err;
    err = sppro_read_words(SPPRO_REG_NOW_BLOCK + SPPRO_FAST_RUN1_OFF,
                           SPPRO_FAST_RUN1_LEN, run1, timeout_ms);
    if (err != ESP_OK) return err;
    sppro_decode_fast(run0, run1, scale, out);
    return ESP_OK;
}
