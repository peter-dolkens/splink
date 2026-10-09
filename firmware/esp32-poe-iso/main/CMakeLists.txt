idf_component_register(SRCS "main.c"
                       REQUIRES sppro esp_eth esp_netif esp_event mqtt nvs_flash driver esp_timer)
