/* SwingLoop core - stable C ABI.
 *
 * Consumed by C# (P/Invoke), Swift (module map) and Kotlin (JNI shim).
 * Strings are UTF-8. Every `char*` returned by this API is heap allocated
 * and must be released with sc_free(). Functions returning a handle or a
 * string return NULL on failure; sc_last_error() then describes why.
 *
 * Detector handles are internally synchronized, so camera, audio and UI
 * threads may call into the same detector concurrently.
 */
#ifndef SWINGCORE_C_H
#define SWINGCORE_C_H

#include <stdint.h>

#ifdef _WIN32
#  ifdef SWINGCORE_BUILD
#    define SC_API __declspec(dllexport)
#  else
#    define SC_API __declspec(dllimport)
#  endif
#else
#  define SC_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

SC_API const char* sc_version(void);
SC_API const char* sc_last_error(void); /* thread-local, do not free */
SC_API void sc_free(char* p);

/* ---------------------------------------------------------------- detector */

typedef struct sc_detector sc_detector;

enum {
    SC_PHASE_NO_GOLFER = 0,
    SC_PHASE_IDLE = 1,
    SC_PHASE_ADDRESS = 2,
    SC_PHASE_BACKSWING = 3,
    SC_PHASE_DOWNSWING = 4,
    SC_PHASE_FOLLOW_THROUGH = 5,
    SC_PHASE_COOLDOWN = 6
};

enum {
    SC_EVENT_GOLFER_ENTERED = 1,
    SC_EVENT_GOLFER_LEFT = 2,
    SC_EVENT_ADDRESS = 3,
    SC_EVENT_SWING_STARTED = 4,
    SC_EVENT_WAGGLE_IGNORED = 5,
    SC_EVENT_PRACTICE_SWING = 6,
    SC_EVENT_SWING_CAPTURED = 7,
    SC_EVENT_SWING_ABORTED = 8
};

enum { SC_SOURCE_POSE = 0, SC_SOURCE_AUDIO_MOTION = 1, SC_SOURCE_MANUAL = 2 };

typedef struct sc_swing_event {
    int32_t type;
    int32_t source;
    int64_t timestamp_us;
    int64_t address_us;
    int64_t takeaway_us;
    int64_t top_us;
    int64_t impact_us;
    int64_t finish_us;
    int64_t clip_start_us; /* extract frames in [clip_start_us, clip_end_us] */
    int64_t clip_end_us;
    float confidence;
    float tempo_ratio; /* backswing / downswing time, 0 if unknown */
    int32_t impact_confirmed;
    int32_t reserved;
} sc_swing_event;

/* config_json: optional object overriding SwingDetectorConfig fields by name,
 * e.g. {"useAudio":false,"preRollUs":2500000}. NULL for defaults. */
SC_API sc_detector* sc_detector_create(const char* config_json);
SC_API void sc_detector_destroy(sc_detector* d);
SC_API int32_t sc_detector_configure(sc_detector* d, const char* config_json);
SC_API void sc_detector_reset(sc_detector* d);

/* 17 COCO keypoints as x,y,score triplets (51 floats), normalized coords.
 * keypoints == NULL means the pose engine saw nobody in this frame. */
SC_API void sc_detector_push_pose(sc_detector* d, int64_t ts_us, const float* keypoints);
/* Mono float PCM; runs impact detection internally. */
SC_API void sc_detector_push_audio(sc_detector* d, int64_t first_sample_us, const float* samples, int32_t count,
                                   int32_t sample_rate);
/* 8-bit luma plane; returns the motion energy (0..1) it computed. */
SC_API float sc_detector_push_luma(sc_detector* d, int64_t ts_us, const uint8_t* luma, int32_t width, int32_t height,
                                   int32_t stride);
SC_API void sc_detector_tick(sc_detector* d, int64_t now_us);
SC_API void sc_detector_trigger_manual(sc_detector* d, int64_t now_us);
SC_API int32_t sc_detector_poll(sc_detector* d, sc_swing_event* out); /* 1 if an event was written */
SC_API int32_t sc_detector_phase(sc_detector* d);
SC_API int32_t sc_detector_golfer_present(sc_detector* d);
SC_API void sc_detector_audio_levels(sc_detector* d, float* level_db, float* noise_floor_db);

/* ---------------------------------------------------------------- sessions */

typedef struct sc_session sc_session;

SC_API sc_session* sc_session_create(const char* root_dir, const char* name);
SC_API sc_session* sc_session_open(const char* folder);
SC_API void sc_session_close(sc_session* s);
SC_API char* sc_session_json(sc_session* s);
SC_API char* sc_session_folder(sc_session* s);
/* Takes a shot JSON (id/number/folder are assigned), returns the stored shot JSON. */
SC_API char* sc_session_add_shot(sc_session* s, const char* shot_json);
SC_API int32_t sc_session_update_shot(sc_session* s, const char* shot_json);
SC_API int32_t sc_session_remove_shot(sc_session* s, const char* shot_id, int32_t delete_files);
SC_API int32_t sc_session_set_persistent_annotations(sc_session* s, const char* shapes_json);
SC_API int32_t sc_session_set_cameras(sc_session* s, const char* cameras_json);
SC_API int32_t sc_session_rename(sc_session* s, const char* name);
SC_API int32_t sc_session_end(sc_session* s);
/* JSON array of session summaries under root, newest first. */
SC_API char* sc_sessions_list(const char* root_dir);

/* ------------------------------------------------------------------ layout */

/* out_content and out_cells (nullable) receive n rects as x,y,w,h. */
SC_API int32_t sc_layout_grid(float width, float height, const float* aspects, int32_t n, float gap,
                              float uniform_aspect, float* out_content, float* out_cells, int32_t* out_rows,
                              int32_t* out_cols);
/* out: crop width, crop height, zoom */
SC_API void sc_rotation_crop(float src_w, float src_h, float degrees, float target_aspect, float* out3);
/* out: normalized x, y, w, h */
SC_API void sc_zoom_window(float zoom, float cx, float cy, float* out4);
SC_API void sc_cover_crop(float src_aspect, float dst_aspect, float* out4);

/* ------------------------------------------------------------- annotations */

SC_API char* sc_new_id(void);
/* Index of the topmost shape in shapes_json within tolerance of (x, y), or -1. */
SC_API int32_t sc_annotation_hit_test(const char* shapes_json, float x, float y, float tolerance, float frame_aspect);
SC_API float sc_annotation_angle(const char* shape_json, float frame_aspect);

#ifdef __cplusplus
}
#endif

#endif /* SWINGCORE_C_H */
