#include <furi.h>
#include <furi_hal_bt.h>
#include <furi_hal_usb_hid.h>
#include <bt/bt_service/bt.h>
#include <extra_profiles/hid_profile.h>
#include <gui/gui.h>
#include <gui/view_port.h>
#include <input/input.h>
#include <string.h>
#include <stdio.h>

#define HID_KEYS_NAME ".ble_lab_hid.keys"
#define TEST_DURATION_MS 60000U

typedef enum {
    AppEventConnected,
    AppEventDisconnected,
    AppEventVolumeUp,
    AppEventVolumeDown,
    AppEventExit,
} AppEvent;

typedef struct {
    FuriMessageQueue* queue;
    Gui* gui;
    ViewPort* view;
    Bt* bt;
    FuriHalBleProfileBase* profile;
    bool connected;
    bool open;
    bool started;
    uint32_t start_tick;
    uint32_t sent_count;
} HidLab;

static const BleProfileHidParams ble_params = {
    .device_name_prefix = "LabHID",
    .mac_xor = 0x0342,
};

static uint32_t uptime_ms(uint32_t start_tick) {
    return (furi_get_tick() - start_tick) * 1000U / furi_kernel_get_tick_frequency();
}

static void app_draw(Canvas* canvas, void* context) {
    HidLab* app = context;
    canvas_clear(canvas);
    canvas_set_font(canvas, FontPrimary);
    canvas_draw_str(canvas, 2, 11, "BLE HID Lab");
    canvas_set_font(canvas, FontSecondary);
    canvas_draw_str(canvas, 2, 24, app->connected ? "Status: Connected" : "Status: Pair via phone");
    char line[40];
    snprintf(line, sizeof(line), "Time: %lus / 60s",
        (unsigned long)(app->started ? uptime_ms(app->start_tick) / 1000U : 0U));
    canvas_draw_str(canvas, 2, 36, line);
    snprintf(line, sizeof(line), "Media keys sent: %lu", (unsigned long)app->sent_count);
    canvas_draw_str(canvas, 2, 47, line);
    canvas_draw_str(canvas, 2, 60, "UP/DOWN: Vol BACK: Exit");
}

static void app_input(InputEvent* event, void* context) {
    HidLab* app = context;
    if(event->type != InputTypeShort) return;
    AppEvent e;
    if(event->key == InputKeyUp) e = AppEventVolumeUp;
    else if(event->key == InputKeyDown) e = AppEventVolumeDown;
    else if(event->key == InputKeyBack) e = AppEventExit;
    else return;
    furi_message_queue_put(app->queue, &e, 0);
}

static void app_bt_event(BtStatus status, void* context) {
    HidLab* app = context;
    AppEvent e = (status == BtStatusConnected) ? AppEventConnected : AppEventDisconnected;
    furi_message_queue_put(app->queue, &e, 0);
}

static void send_media_key(HidLab* app, uint16_t key) {
    if(!app->connected || !app->profile) return;
    if(ble_profile_hid_consumer_key_press(app->profile, key)) {
        furi_delay_ms(30);
        ble_profile_hid_consumer_key_release(app->profile, key);
        app->sent_count++;
    }
}

int32_t ble_hid_lab_app(void* argument) {
    UNUSED(argument);
    HidLab* app = malloc(sizeof(HidLab));
    memset(app, 0, sizeof(HidLab));
    app->open = true;
    app->queue = furi_message_queue_alloc(16, sizeof(AppEvent));
    app->gui = furi_record_open(RECORD_GUI);
    app->view = view_port_alloc();
    view_port_draw_callback_set(app->view, app_draw, app);
    view_port_input_callback_set(app->view, app_input, app);
    gui_add_view_port(app->gui, app->view, GuiLayerFullscreen);

    app->bt = furi_record_open(RECORD_BT);
    bt_disconnect(app->bt);
    furi_delay_ms(200);
    bt_keys_storage_set_storage_path(app->bt, APP_DATA_PATH(HID_KEYS_NAME));
    app->profile = bt_profile_start(app->bt, ble_profile_hid, (void*)&ble_params);

    if(app->profile) {
        bt_set_status_changed_callback(app->bt, app_bt_event, app);
        furi_hal_bt_start_advertising();
        app->start_tick = furi_get_tick();
        app->started = true;

        while(app->open && uptime_ms(app->start_tick) < TEST_DURATION_MS) {
            AppEvent e;
            if(furi_message_queue_get(app->queue, &e, 100) == FuriStatusOk) {
                if(e == AppEventConnected) app->connected = true;
                else if(e == AppEventDisconnected) app->connected = false;
                else if(e == AppEventVolumeUp) {
                    send_media_key(app, HID_CONSUMER_VOLUME_INCREMENT);
                } else if(e == AppEventVolumeDown) {
                    send_media_key(app, HID_CONSUMER_VOLUME_DECREMENT);
                } else if(e == AppEventExit) {
                    app->open = false;
                }
            }
            view_port_update(app->view);
        }

        bt_set_status_changed_callback(app->bt, NULL, NULL);
        bt_disconnect(app->bt);
        furi_delay_ms(200);
    }

    bt_keys_storage_set_default_path(app->bt);
    bt_profile_restore_default(app->bt);
    furi_record_close(RECORD_BT);

    gui_remove_view_port(app->gui, app->view);
    view_port_free(app->view);
    furi_record_close(RECORD_GUI);
    furi_message_queue_free(app->queue);
    free(app);
    return 0;
}
