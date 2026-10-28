#include <furi.h>
#include <furi_hal_bt.h>
#include <furi_hal_version.h>
#include <gui/gui.h>
#include <gui/view_port.h>
#include <input/input.h>
#include <extra_beacon.h>
#include <stdio.h>
#include <string.h>

#define MAX_RUN_MS 60000U
#define BURST_WINDOW_MS 2000U

static const uint16_t intervals_ms[] = {1000, 500, 200};

static const uint8_t name_payload[] = {
    0x02, 0x01, 0x06,
    0x07, 0x09, 'F', 'Z', '-', 'L', 'a', 'b',
};

static const uint8_t data_payload[] = {
    0x02, 0x01, 0x06,
    0x09, 0xFF, 0xFF, 0xFF, 'F', 'Z', 'L', 'A', 'B', 0x01,
};

typedef struct {
    FuriMessageQueue* events;
    Gui* gui;
    ViewPort* view;
    bool app_open;
    bool running;
    bool transmitting;
    bool burst;
    bool use_data_payload;
    bool owns_beacon;
    bool saved_has_config;
    bool blocked_by_beacon;
    bool radio_error;
    uint8_t interval_index;
    uint32_t started_tick;
    GapExtraBeaconConfig saved_config;
    uint8_t saved_data[EXTRA_BEACON_MAX_DATA_SIZE];
    uint8_t saved_data_len;
} BleLab;

static uint32_t elapsed_ms(uint32_t start_tick) {
    const uint32_t elapsed_ticks = furi_get_tick() - start_tick;
    return elapsed_ticks * 1000U / furi_kernel_get_tick_frequency();
}

static void app_draw(Canvas* canvas, void* context) {
    BleLab* app = context;
    canvas_clear(canvas);
    canvas_set_font(canvas, FontPrimary);
    canvas_draw_str(canvas, 2, 11, "BLE Lab Beacon");
    canvas_set_font(canvas, FontSecondary);
    if(app->blocked_by_beacon) {
        canvas_draw_str(canvas, 2, 24, "Extra beacon in use");
        canvas_draw_str(canvas, 2, 36, "Stop other beacon first");
    } else if(app->radio_error) {
        canvas_draw_str(canvas, 2, 24, "Radio error: try later");
        canvas_draw_str(canvas, 2, 36, "Press OK to retry");
    } else if(app->running) {
        canvas_draw_str(canvas, 2, 24, app->transmitting ? "ON: Advertising" : "OFF: Burst pause");
        char line[32];
        const uint32_t passed = elapsed_ms(app->started_tick);
        snprintf(line, sizeof(line), "Elapsed: %lus / 60s", (unsigned long)(passed / 1000U));
        canvas_draw_str(canvas, 2, 36, line);
    } else {
        canvas_draw_str(canvas, 2, 24, "Ready: OK to start");
    }
    char line[40];
    snprintf(line, sizeof(line), "%ums %s %s",
             (unsigned)intervals_ms[app->interval_index],
             app->burst ? "Burst" : "Steady",
             app->use_data_payload ? "Data" : "Name");
    canvas_draw_str(canvas, 2, 47, line);
    canvas_draw_str(canvas, 2, 60, "UP:rate L:mode R:data");
}

static void app_input(InputEvent* event, void* context) {
    BleLab* app = context;
    if(event->type != InputTypeShort) return;
    InputKey key = event->key;
    furi_message_queue_put(app->events, &key, 0);
}

static void stop_run(BleLab* app) {
    if(app->transmitting) {
        furi_hal_bt_extra_beacon_stop();
        app->transmitting = false;
    }
    app->running = false;
    if(app->owns_beacon) {
        if(app->saved_has_config) {
            furi_hal_bt_extra_beacon_set_config(&app->saved_config);
        }
        furi_hal_bt_extra_beacon_set_data(app->saved_data, app->saved_data_len);
        app->owns_beacon = false;
    }
}

static bool set_transmitting(BleLab* app, bool enabled) {
    if(app->transmitting == enabled) return true;
    bool ok = enabled ? furi_hal_bt_extra_beacon_start() : furi_hal_bt_extra_beacon_stop();
    if(ok) app->transmitting = enabled;
    return ok;
}

static bool start_run(BleLab* app) {
    app->radio_error = false;
    app->blocked_by_beacon = false;
    if(furi_hal_bt_extra_beacon_is_active()) {
        app->blocked_by_beacon = true;
        return false;
    }
    const GapExtraBeaconConfig* previous = furi_hal_bt_extra_beacon_get_config();
    app->saved_has_config = previous != NULL;
    if(previous) app->saved_config = *previous;
    app->saved_data_len = furi_hal_bt_extra_beacon_get_data(app->saved_data);

    GapExtraBeaconConfig config = {0};
    config.min_adv_interval_ms = intervals_ms[app->interval_index];
    config.max_adv_interval_ms = intervals_ms[app->interval_index];
    config.adv_channel_map = GapAdvChannelMapAll;
    config.adv_power_level = GapAdvPowerLevel_Neg20_85dBm;
    config.address_type = GapAddressTypeRandom;
    memcpy(config.address, furi_hal_version_get_ble_mac(), sizeof(config.address));
    config.address[0] ^= 0xA5;
    config.address[1] ^= 0xC3;
    config.address[5] = (config.address[5] & 0x3F) | 0xC0;

    app->owns_beacon = true;
    if(!furi_hal_bt_extra_beacon_set_config(&config)) {
        app->radio_error = true;
        stop_run(app);
        return false;
    }
    const uint8_t* payload = app->use_data_payload ? data_payload : name_payload;
    const uint8_t payload_len = app->use_data_payload ? sizeof(data_payload) : sizeof(name_payload);
    if(!furi_hal_bt_extra_beacon_set_data(payload, payload_len)) {
        app->radio_error = true;
        stop_run(app);
        return false;
    }
    if(!set_transmitting(app, true)) {
        app->radio_error = true;
        stop_run(app);
        return false;
    }
    app->started_tick = furi_get_tick();
    app->running = true;
    return true;
}

static void tick_run(BleLab* app) {
    if(!app->running) return;
    uint32_t passed = elapsed_ms(app->started_tick);
    if(passed >= MAX_RUN_MS) {
        stop_run(app);
        return;
    }
    bool should_transmit = !app->burst || ((passed / BURST_WINDOW_MS) % 2U == 0U);
    if(!set_transmitting(app, should_transmit)) {
        app->radio_error = true;
        stop_run(app);
    }
}

int32_t ble_lab_app(void* argument) {
    UNUSED(argument);
    BleLab* app = malloc(sizeof(BleLab));
    memset(app, 0, sizeof(BleLab));
    app->app_open = true;
    app->events = furi_message_queue_alloc(8, sizeof(InputKey));
    app->gui = furi_record_open(RECORD_GUI);
    app->view = view_port_alloc();
    view_port_draw_callback_set(app->view, app_draw, app);
    view_port_input_callback_set(app->view, app_input, app);
    gui_add_view_port(app->gui, app->view, GuiLayerFullscreen);

    while(app->app_open) {
        InputKey key;
        if(furi_message_queue_get(app->events, &key, 100) == FuriStatusOk) {
            if(key == InputKeyBack) {
                app->app_open = false;
            } else if(key == InputKeyOk) {
                if(app->running) stop_run(app);
                else start_run(app);
            } else if(!app->running && key == InputKeyUp) {
                app->interval_index = (app->interval_index + 1U) % COUNT_OF(intervals_ms);
            } else if(!app->running && key == InputKeyLeft) {
                app->burst = !app->burst;
            } else if(!app->running && key == InputKeyRight) {
                app->use_data_payload = !app->use_data_payload;
            }
        }
        tick_run(app);
        view_port_update(app->view);
    }
    stop_run(app);
    gui_remove_view_port(app->gui, app->view);
    view_port_free(app->view);
    furi_record_close(RECORD_GUI);
    furi_message_queue_free(app->events);
    free(app);
    return 0;
}
