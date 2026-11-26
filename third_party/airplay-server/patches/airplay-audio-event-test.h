/* SPDX-License-Identifier: LGPL-2.1-or-later */
/* Included by raop_handlers.h after the /info handler. */
#ifdef _WIN32

struct iphone_mirror_event_s {
    raop_conn_t *connection;
    SOCKET listener;
    HANDLE stop;
    HANDLE thread;
    unsigned short port;
    int mode;
};

static int iphone_mirror_event_send_bytes(SOCKET socket, const char *buffer, int length) {
    while (length > 0) {
        int sent = send(socket, buffer, length, 0);
        if (sent <= 0) return 0;
        buffer += sent;
        length -= sent;
    }
    return 1;
}

static int iphone_mirror_event_update(struct iphone_mirror_event_s *event,
    SOCKET client, unsigned int sequence, int phone_audio) {
    char *info = NULL;
    int info_length = 0;
    http_response_t *response = http_response_init("RTSP/1.0", 200, "OK");
    if (!response) return 0;
    raop_handler_info(event->connection, NULL, response, &info, &info_length);
    http_response_destroy(response);
    if (!info) return 0;

    plist_t value = NULL;
    plist_from_bin(info, (uint32_t)info_length, &value);
    free(info);
    if (!value) return 0;
    uint64_t features = ((uint64_t)GLOBAL_FEATURES_2 << 32) | GLOBAL_FEATURES_1;
    if (phone_audio && event->mode >= 2) {
        features &= ~((1ULL << 9) | (1ULL << 11) | (15ULL << 18));
        if (event->mode == 3) features &= ~(1ULL << 30);
        plist_dict_remove_item(value, "txtAirPlay");
        plist_dict_remove_item(value, "audioFormats");
        plist_dict_remove_item(value, "audioLatencies");
    }
    plist_dict_set_item(value, "features", plist_new_uint(features));
    plist_t formats = plist_new_dict();
    plist_dict_set_item(formats, "screenStream", plist_new_uint(21235712));
    plist_dict_set_item(formats, "audioStream", plist_new_uint(phone_audio ? 0 : 21235712));
    plist_dict_set_item(formats, "bufferStream", plist_new_uint(0));
    plist_dict_set_item(value, "supportedFormats", formats);

    plist_t root = plist_new_dict();
    plist_dict_set_item(root, "type", plist_new_string("updateInfo"));
    plist_dict_set_item(root, "value", value);
    char *body = NULL;
    uint32_t body_length = 0;
    plist_to_bin(root, &body, &body_length);
    plist_free(root);
    if (!body) return 0;
    char header[256];
    int header_length = snprintf(header, sizeof(header),
        "POST /command RTSP/1.0\r\nContent-Type: application/x-apple-binary-plist\r\n"
        "Content-Length: %u\r\nCSeq: %u\r\n\r\n", (unsigned int)body_length, sequence);
    int sent = header_length > 0 && header_length < sizeof(header) &&
        iphone_mirror_event_send_bytes(client, header, header_length) &&
        iphone_mirror_event_send_bytes(client, body, (int)body_length);
    free(body);
    logger_log(event->connection->raop->logger, LOGGER_INFO,
        "IPHONE_MIRROR_VIDEO_DIAG 40 %d %d %d %d 0 0",
        event->mode, 2, phone_audio, sent);
    return sent;
}

static DWORD WINAPI iphone_mirror_event_worker(void *context) {
    struct iphone_mirror_event_s *event = context;
    raop_conn_t *conn = event->connection;
    SOCKET client = INVALID_SOCKET;
    ULONGLONG accepted_at = 0, next_update = 0;
    unsigned int sequence = 0;
    int phone_audio = event->mode == 1;
    while (WaitForSingleObject(event->stop, 0) == WAIT_TIMEOUT) {
        SOCKET active = client == INVALID_SOCKET ? event->listener : client;
        fd_set reads;
        FD_ZERO(&reads);
        FD_SET(active, &reads);
        struct timeval timeout = {0, 200000};
        int readable = select(0, &reads, NULL, NULL, &timeout);
        if (readable == SOCKET_ERROR) break;
        ULONGLONG now = GetTickCount64();
        if (client == INVALID_SOCKET) {
            if (!readable) continue;
            struct sockaddr_storage peer;
            int peer_length = sizeof(peer);
            SOCKET accepted = accept(event->listener, (struct sockaddr *)&peer, &peer_length);
            if (accepted == INVALID_SOCKET) continue;
            int address_length = 0;
            const unsigned char *address = netutils_get_address(&peer, &address_length);
            if (!address || address_length != conn->remotelen ||
                memcmp(address, conn->remote, address_length)) {
                closesocket(accepted);
                continue;
            }
            DWORD send_timeout = 1200;
            if (setsockopt(accepted, SOL_SOCKET, SO_SNDTIMEO,
                (const char *)&send_timeout, sizeof(send_timeout)) == SOCKET_ERROR) {
                closesocket(accepted);
                continue;
            }
            client = accepted;
            accepted_at = now;
            next_update = now;
            logger_log(conn->raop->logger, LOGGER_INFO,
                "IPHONE_MIRROR_VIDEO_DIAG 40 %d 1 0 0 0 0", event->mode);
        } else if (readable) {
            char reply[4096];
            int received = recv(client, reply, sizeof(reply), 0);
            if (received <= 0) break;
            if (received >= 12 && !memcmp(reply, "RTSP/1.0 ", 9))
                logger_log(conn->raop->logger, LOGGER_INFO,
                    "IPHONE_MIRROR_VIDEO_DIAG 40 %d 3 %d %d 0 0", event->mode,
                    !memcmp(reply + 9, "200", 3), phone_audio);
        }
        if (event->mode >= 2 && !phone_audio && now - accepted_at >= 15000) {
            phone_audio = 1;
            next_update = now;
        }
        if (now < next_update) continue;
        if (!iphone_mirror_event_update(event, client, sequence++, phone_audio)) break;
        next_update = now + 15000;
    }
    if (client != INVALID_SOCKET) closesocket(client);
    logger_log(conn->raop->logger, LOGGER_INFO,
        "IPHONE_MIRROR_VIDEO_DIAG 40 %d 4 0 0 0 0", event->mode);
    return 0;
}

static unsigned short iphone_mirror_event_start(raop_conn_t *conn) {
    if (conn->audio_event) return conn->audio_event->port;
    int mode = iphone_mirror_diagnostic_mirror_audio_event();
    if (!mode) return 0;
    struct iphone_mirror_event_s *event = calloc(1, sizeof(*event));
    if (!event) return 0;
    event->connection = conn;
    event->mode = mode;
    int listener = netutils_init_socket(&event->port, conn->locallen == 16,
        0, conn->local, conn->locallen);
    if (listener < 0) { free(event); return 0; }
    event->listener = (SOCKET)(uintptr_t)listener;
    if (listen(event->listener, 1) == 0) {
        event->stop = CreateEventW(NULL, TRUE, FALSE, NULL);
        if (event->stop)
            event->thread = CreateThread(NULL, 0, iphone_mirror_event_worker, event, 0, NULL);
    }
    if (!event->thread) {
        if (event->stop) CloseHandle(event->stop);
        closesocket(event->listener);
        free(event);
        return 0;
    }
    conn->audio_event = event;
    logger_log(conn->raop->logger, LOGGER_INFO,
        "IPHONE_MIRROR_VIDEO_DIAG 40 %d 0 %d 0 0 0", mode, event->port);
    return event->port;
}

static void iphone_mirror_event_stop(raop_conn_t *conn) {
    struct iphone_mirror_event_s *event = conn->audio_event;
    if (!event) return;
    conn->audio_event = NULL;
    SetEvent(event->stop);
    THREAD_JOIN(event->thread);
    CloseHandle(event->stop);
    closesocket(event->listener);
    free(event);
}

#else
static unsigned short iphone_mirror_event_start(raop_conn_t *conn) { (void)conn; return 0; }
static void iphone_mirror_event_stop(raop_conn_t *conn) { (void)conn; }
#endif
