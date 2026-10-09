// Serial -> MQTT bridge for an Olimex ESP32-POE-ISO.
//
// Deliberately narrow: it reads the SP PRO's fast window over UART and publishes it to MQTT,
// with Home Assistant discovery. No HTTP server, no Prometheus, no log backfill, no config
// dump -- those stay in splink on a host, where there is memory and a filesystem for them.
//
// The point of this board for this job is that Ethernet is on RMII and the inverter is on a
// UART. They share no bus, so the failure that took the Raspberry Pi bridge off the air for
// 19 hours -- one USB controller carrying both -- cannot happen here.
#include <stdio.h>
#include <string.h>

#include "driver/gpio.h"
#include "esp_eth.h"
#include "esp_event.h"
#include "esp_log.h"
#include "esp_mac.h"
#include "esp_netif.h"
#include "esp_task_wdt.h"
#include "freertos/FreeRTOS.h"
#include "freertos/task.h"
#include "mqtt_client.h"
#include "nvs_flash.h"

#include "sppro_link.h"

static const char *TAG = "bridge";

static esp_mqtt_client_handle_t s_mqtt;
static volatile bool s_mqtt_up;
static volatile bool s_net_up;

// --- Ethernet ------------------------------------------------------------------------------
static void on_eth_event(void *arg, esp_event_base_t base, int32_t id, void *data) {
    (void)arg; (void)base; (void)data;
    switch (id) {
        case ETHERNET_EVENT_CONNECTED:    ESP_LOGI(TAG, "ethernet link up"); break;
        case ETHERNET_EVENT_DISCONNECTED: ESP_LOGW(TAG, "ethernet link down"); s_net_up = false; break;
        default: break;
    }
}

static void on_got_ip(void *arg, esp_event_base_t base, int32_t id, void *data) {
    (void)arg; (void)base; (void)id;
    const ip_event_got_ip_t *e = (const ip_event_got_ip_t *)data;
    ESP_LOGI(TAG, "address " IPSTR, IP2STR(&e->ip_info.ip));
    s_net_up = true;
}

static void ethernet_start(void) {
    ESP_ERROR_CHECK(esp_netif_init());
    ESP_ERROR_CHECK(esp_event_loop_create_default());
    esp_netif_config_t netif_cfg = ESP_NETIF_DEFAULT_ETH();
    esp_netif_t *netif = esp_netif_new(&netif_cfg);

    // The PHY is held in reset by a GPIO on this board; without this the MDIO bus reads back
    // all ones and PHY detection fails in a way that looks like a dead chip.
    gpio_config_t pwr = {
        .pin_bit_mask = 1ULL << CONFIG_BRIDGE_ETH_PHY_POWER_GPIO,
        .mode = GPIO_MODE_OUTPUT,
    };
    ESP_ERROR_CHECK(gpio_config(&pwr));
    gpio_set_level(CONFIG_BRIDGE_ETH_PHY_POWER_GPIO, 1);
    vTaskDelay(pdMS_TO_TICKS(100));

    eth_mac_config_t mac_cfg = ETH_MAC_DEFAULT_CONFIG();
    eth_esp32_emac_config_t emac_cfg = ETH_ESP32_EMAC_DEFAULT_CONFIG();
    emac_cfg.smi_gpio.mdc_num  = CONFIG_BRIDGE_ETH_MDC_GPIO;
    emac_cfg.smi_gpio.mdio_num = CONFIG_BRIDGE_ETH_MDIO_GPIO;
    // The ESP32 drives the 50 MHz RMII clock out to the PHY on this board, rather than taking
    // a clock in. Getting this backwards gives a link that negotiates and then passes nothing.
    emac_cfg.clock_config.rmii.clock_mode = EMAC_CLK_OUT;
    emac_cfg.clock_config.rmii.clock_gpio = CONFIG_BRIDGE_ETH_CLK_GPIO;

    eth_phy_config_t phy_cfg = ETH_PHY_DEFAULT_CONFIG();
    phy_cfg.phy_addr = CONFIG_BRIDGE_ETH_PHY_ADDR;
    phy_cfg.reset_gpio_num = -1;

    esp_eth_mac_t *mac = esp_eth_mac_new_esp32(&emac_cfg, &mac_cfg);
    esp_eth_phy_t *phy = esp_eth_phy_new_lan87xx(&phy_cfg);
    esp_eth_config_t eth_cfg = ETH_DEFAULT_CONFIG(mac, phy);
    esp_eth_handle_t eth = NULL;
    ESP_ERROR_CHECK(esp_eth_driver_install(&eth_cfg, &eth));
    ESP_ERROR_CHECK(esp_netif_attach(netif, esp_eth_new_netif_glue(eth)));
    ESP_ERROR_CHECK(esp_event_handler_register(ETH_EVENT, ESP_EVENT_ANY_ID, on_eth_event, NULL));
    ESP_ERROR_CHECK(esp_event_handler_register(IP_EVENT, IP_EVENT_ETH_GOT_IP, on_got_ip, NULL));
    ESP_ERROR_CHECK(esp_eth_start(eth));
}

// --- MQTT ----------------------------------------------------------------------------------
#define TOPIC_BASE  "solar-bridge/selectronic/" CONFIG_BRIDGE_INVERTER_NAME
#define STATE_TOPIC TOPIC_BASE "/state_fast"
#define STATUS_TOPIC "solar-bridge/" CONFIG_BRIDGE_INVERTER_NAME "/status"

static void announce_discovery(void);

static void on_mqtt_event(void *arg, esp_event_base_t base, int32_t id, void *data) {
    (void)arg; (void)base;
    switch ((esp_mqtt_event_id_t)id) {
        case MQTT_EVENT_CONNECTED:
            ESP_LOGI(TAG, "mqtt connected");
            s_mqtt_up = true;
            // Re-announce on every connect, not just the first: a dropped link publishes our
            // retained last will, and nothing else would clear it.
            announce_discovery();
            esp_mqtt_client_publish(s_mqtt, STATUS_TOPIC, "online", 0, 1, true);
            break;
        case MQTT_EVENT_DISCONNECTED:
            ESP_LOGW(TAG, "mqtt disconnected");
            s_mqtt_up = false;
            break;
        default: break;
    }
}

static void mqtt_start(void) {
    const esp_mqtt_client_config_t cfg = {
        .broker.address.uri = CONFIG_BRIDGE_MQTT_URI,
        .credentials.username = CONFIG_BRIDGE_MQTT_USERNAME,
        .credentials.authentication.password = CONFIG_BRIDGE_MQTT_PASSWORD,
        .session.last_will = {
            .topic = STATUS_TOPIC,
            .msg = "offline",
            .retain = true,
            .qos = 1,
        },
    };
    s_mqtt = esp_mqtt_client_init(&cfg);
    ESP_ERROR_CHECK(esp_mqtt_client_register_event(s_mqtt, ESP_EVENT_ANY_ID, on_mqtt_event, NULL));
    ESP_ERROR_CHECK(esp_mqtt_client_start(s_mqtt));
}

// One discovery config per reading. Kept as a table so adding a sensor is one line and the
// object_id, which Home Assistant keys history on, is visible in one place.
typedef struct {
    const char *object;      // object_id suffix, after the entity prefix
    const char *name;
    const char *json_key;    // field in the state payload
    const char *unit;
    const char *device_class;
    const char *state_class;
} sensor_def_t;

static const sensor_def_t SENSORS[] = {
    { "ac_load_power",     "AC Load Power",     "ac_load_kilowatts",   "kW", "power",   "measurement" },
    { "inverter_power",    "Inverter AC Power", "inverter_kilowatts",  "kW", "power",   "measurement" },
    { "battery_soc",       "Battery SoC",       "battery_soc_percent", "%",  "battery", "measurement" },
    { "battery_current",   "Battery Current",   "battery_amps",        "A",  "current", "measurement" },
    { "dc_current",        "DC Current",        "dc_amps",             "A",  "current", "measurement" },
    { "charger_state",     "Charger State",     "charger",             NULL, NULL,      NULL },
    { "inverter_mode",     "Inverter Mode",     "inverter_mode",       NULL, NULL,      NULL },
    { "ac_source_status",  "AC Source Status",  "ac_source_status",    NULL, NULL,      NULL },
};

static void announce_discovery(void) {
    char topic[160];
    char payload[640];
    for (size_t i = 0; i < sizeof SENSORS / sizeof SENSORS[0]; i++) {
        const sensor_def_t *s = &SENSORS[i];
        snprintf(topic, sizeof topic,
                 "homeassistant/sensor/%s/%s_%s/config",
                 CONFIG_BRIDGE_DEVICE_ID, CONFIG_BRIDGE_ENTITY_PREFIX, s->object);

        int n = snprintf(payload, sizeof payload,
            "{\"name\":\"%s\",\"object_id\":\"%s_%s\",\"unique_id\":\"solarbridge_%s_%s\","
            "\"state_topic\":\"%s\",\"value_template\":\"{{ value_json.%s }}\","
            "\"availability\":[{\"topic\":\"%s\"}],"
            "\"device\":{\"identifiers\":[\"%s\"],\"name\":\"%s\","
            "\"manufacturer\":\"Selectronic\",\"model\":\"SP PRO\",\"via_device\":\"%s\"}",
            s->name, CONFIG_BRIDGE_ENTITY_PREFIX, s->object,
            CONFIG_BRIDGE_ENTITY_PREFIX, s->object,
            STATE_TOPIC, s->json_key, STATUS_TOPIC,
            CONFIG_BRIDGE_DEVICE_ID, CONFIG_BRIDGE_DEVICE_NAME, CONFIG_BRIDGE_DEVICE_ID);
        if (s->unit)
            n += snprintf(payload + n, sizeof payload - n, ",\"unit_of_measurement\":\"%s\"", s->unit);
        if (s->device_class)
            n += snprintf(payload + n, sizeof payload - n, ",\"device_class\":\"%s\"", s->device_class);
        if (s->state_class)
            n += snprintf(payload + n, sizeof payload - n, ",\"state_class\":\"%s\"", s->state_class);
        snprintf(payload + n, sizeof payload - n, "}");

        esp_mqtt_client_publish(s_mqtt, topic, payload, 0, 1, true);
    }
    ESP_LOGI(TAG, "announced %u entities", (unsigned)(sizeof SENSORS / sizeof SENSORS[0]));
}

// --- Poll loop -----------------------------------------------------------------------------
static void publish_reading(const sppro_fast_t *f) {
    static char payload[384];
    static char last[384];

    // SoC reads 0xFFFF when the inverter does not know it. Publishing "null" lets Home
    // Assistant show unknown rather than a fabricated 0.
    char soc[24];
    if (f->soc_valid) snprintf(soc, sizeof soc, "%.3f", f->battery_soc_percent);
    else              snprintf(soc, sizeof soc, "null");

    snprintf(payload, sizeof payload,
        "{\"ac_load_kilowatts\":%.3f,\"inverter_kilowatts\":%.3f,\"battery_soc_percent\":%s,"
        "\"battery_amps\":%.2f,\"dc_amps\":%.2f,\"charger\":\"%s\",\"inverter_mode\":\"%s\","
        "\"ac_source_status\":\"%s\"}",
        f->ac_load_kw, f->inverter_ac_kw, soc, f->battery_amps, f->dc_amps,
        f->charger_state, f->inverter_mode, f->ac_source_status);

    // Only publish on change. Nothing downstream benefits from identical retained payloads,
    // and it keeps the broker and the recorder quiet when the system is idle.
    if (strcmp(payload, last) == 0) return;
    if (esp_mqtt_client_publish(s_mqtt, STATE_TOPIC, payload, 0, 0, true) >= 0)
        strncpy(last, payload, sizeof last - 1);
}

static void bridge_task(void *arg) {
    (void)arg;
    ESP_ERROR_CHECK(esp_task_wdt_add(NULL));

    sppro_scale_t scale;
    bool have_scale = false;
    int failures = 0;

    for (;;) {
        esp_task_wdt_reset();

        if (!sppro_is_logged_in()) {
            if (sppro_login(CONFIG_BRIDGE_SPPRO_PASSWORD, 2000) != ESP_OK) {
                ESP_LOGW(TAG, "login failed; retrying");
                vTaskDelay(pdMS_TO_TICKS(5000));
                continue;
            }
            // Scale factors are model-specific and read from the device, never assumed. They do
            // not change while the link is up, so this is once per session, not once per poll.
            have_scale = false;
        }
        if (!have_scale) {
            if (sppro_read_scale(&scale, 2000) != ESP_OK) {
                sppro_mark_logged_out();
                vTaskDelay(pdMS_TO_TICKS(2000));
                continue;
            }
            have_scale = true;
            ESP_LOGI(TAG, "scale factors: acV=%d acA=%d dcV=%d dcA=%d",
                     scale.ac_volts, scale.ac_current, scale.dc_volts, scale.dc_current);
        }

        sppro_fast_t f;
        if (sppro_read_fast(&scale, &f, 2000) == ESP_OK) {
            failures = 0;
            if (s_mqtt_up && s_net_up) publish_reading(&f);
        } else if (++failures >= 5) {
            // Repeated failures mean the link is not merely busy. Drop the session so the next
            // pass re-logs in; the SP PRO closes the port on its side after an idle period too.
            ESP_LOGW(TAG, "%d consecutive read failures; re-establishing the link", failures);
            sppro_mark_logged_out();
            failures = 0;
        }

        vTaskDelay(pdMS_TO_TICKS(CONFIG_BRIDGE_POLL_INTERVAL_MS));
    }
}

void app_main(void) {
    esp_err_t nvs = nvs_flash_init();
    if (nvs == ESP_ERR_NVS_NO_FREE_PAGES || nvs == ESP_ERR_NVS_NEW_VERSION_FOUND) {
        ESP_ERROR_CHECK(nvs_flash_erase());
        ESP_ERROR_CHECK(nvs_flash_init());
    }

    ethernet_start();
    mqtt_start();

    const sppro_link_config_t link = {
        .uart_num = CONFIG_BRIDGE_UART_NUM,
        .tx_gpio  = CONFIG_BRIDGE_UART_TX_GPIO,
        .rx_gpio  = CONFIG_BRIDGE_UART_RX_GPIO,
        .baud     = CONFIG_BRIDGE_UART_BAUD,
    };
    ESP_ERROR_CHECK(sppro_link_init(&link));

    xTaskCreate(bridge_task, "bridge", 4096, NULL, 5, NULL);
}
