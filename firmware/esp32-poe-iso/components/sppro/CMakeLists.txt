idf_component_register(SRCS "sppro_proto.c" "sppro_names.c" "sppro_login.c" "sppro_link.c"
                       INCLUDE_DIRS "include"
                       REQUIRES driver
                       PRIV_REQUIRES mbedtls esp_timer)
