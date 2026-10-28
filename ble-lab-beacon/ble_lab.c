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
#define IDENTITY_WINDOW_MS 5000U
#define IDENTITY_COUNT 4U

static const uint16_t intervals_ms[] = {500, 200, 100};

typedef struct {
    FuriMessageQueue* events;
    Gui* gui;
    ViewPort* view;
    bool app_open;
    bool running;
    bool transmitting;
    bool burst;
    bool multi_mode;
    bool owns_beacon;
    bool saved_has_config;
    bool blocked_by_beacon;
    bool radio_error;
    uint8_t interval_index;
    uint8_t identity_index;
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
    canvas_draw_str(canvas, 2, 11, "BLE Lab Multi");
    canvas_set_font(canvas, FontSecondary);
    if(app->blocked_by_beacon) {
        canvas_draw_str(canvas, 2, 24, "Beacon already in use");
        canvas_draw_str(canvas, 2, 36, "Stop other beacon first");
    } else if(app->radio_error) {
        canvas_draw_str(canvas, 2, 24, "Radio setup failed");
        canvas_draw_str(canvas, 2, 36, "Press OK to retry");
    } else if(app->running) {
        char line[35];
        snprintf(line, sizeof(line), "%s  FZLAB-%02u",
            app->transmitting ? "TX" : "Pause",
            (unsigned)(app->identity_index + 1U));
        canvas_draw_str(canvas, 2, 24, line);
        const uint32_t passed = elapsed_ms(app->started_tick);
        snprintf(line, sizeof(line), "Time: %lus / 60s", (unsigned long)(passed / 1000U));
        canvas_draw_str(canvas, 2, 36, line);
    } else {
        canvas_draw_str(canvas, 2, 24, "Ready: press OK");
        canvas_draw_str(canvas, 2, 36, "OK: start  BACK: exit");
    }
    char line[40];
    snprintf(line, sizeof(line), "%ums %s %s",
             (unsigned)intervals_ms[app->interval_index],
             app->burst ? "Burst" : "Steady",
             app->multi_mode ? "4 IDs" : "1 ID");
    canvas_draw_str(canvas, 2, 47, line);
    canvas_draw_str(canvas, 2, 60, "UP:rate L:burst R:IDs");
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
    const bool ok = enabled ? furi_hal_bt_extra_beacon_start() : furi_hal_bt_extra_beacon_stop();
    if(ok) app->transmitting = enabled;
    return ok;
}

static bool configure_identity(BleLab* app, uint8_t identity_index, bool should_transmit) {
    if(!set_transmitting(app, false)) return false;
    GapExtraBeaconConfig config = {0};
    config.min_adv_interval_ms = intervals_ms[app->interval_index];
    config.max_adv_interval_ms = intervals_ms[app->interval_index];
    config.adv_channel_map = GapAdvChannelMapAll;
    config.adv_power_level = GapAdvPowerLevel_0dBm;
    config.address_type = GapAddressTypeRandom;
    memcpy(config.address, furi_hal_version_get_ble_mac(), sizeof(config.address));
    config.address[0] ^= (uint8_t)(0xA5U + (identity_index * 17U));
    config.address[1] ^= 0xC3U;
    config.address[5] = (config.address[5] & 0x3FU) | 0xC0U;

    const uint8_t payload[] = {
        0x02, 0x01, 0x06,
        0x09, 0x09, 'F', 'Z', 'L', 'A', 'B', '-', '0',
        (uint8_t)('1' + identity_index),
    };

    if(!furi_hal_bt_extra_beacon_set_config(&config)) return false;
    if(!furi_hal_bt_extra_beacon_set_data(payload, sizeof(payload))) return false;
    app->identity_index = identity_index;
    return set_transmitting(app, should_transmit);
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
    app->owns_beacon = true;

    if(!configure_identity(app, 0, true)) {
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
    const uint32_t passed = elapsed_ms(app->started_tick);
    if(passed >= MAX_RUN_MS) {
        stop_run(app);
        return;
    }
    const bool should_transmit =
        !app->burst || ((passed / BURST_WINDOW_MS) % 2U == 0U);
    const uint8_t identity =
        app->multi_mode ? (uint8_t)((passed / IDENTITY_WINDOW_MS) % IDENTITY_COUNT) : 0U;

    const bool ok = (identity != app->identity_index)
        ? configure_identity(app, identity, should_transmit)
        : set_transmitting(app, should_transmit);
    if(!ok) {
        app->radio_error = true;
        stop_run(app);
    }
}

int32_t ble_lab_app(void* argument) {
    UNUSED(argument);
    BleLab* app = malloc(sizeof(BleLab));
    memset(app, 0, sizeof(BleLab));
    app->app_open = true;
    app->multi_mode = true;
    app->interval_index = 2; // 100ms default advertising interval
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
                app->multi_mode = !app->multi_mode;
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
