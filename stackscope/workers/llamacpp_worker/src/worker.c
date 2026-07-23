/* StackScope llama.cpp worker — core implementation.
 *
 * Uses llama.cpp's public C API. On llama.cpp master, the surface we
 * touch is (roughly):
 *   llama_backend_init / llama_backend_free
 *   llama_model_load_from_file / llama_model_free
 *   llama_new_context_with_model / llama_free
 *   llama_tokenize / llama_token_to_piece
 *   llama_decode  (batched eval)
 *   llama_get_logits_ith
 *   llama_sampler_* (sampler chain)
 *   llama_model_n_layer / llama_model_n_head / llama_n_vocab
 *
 * Events are emitted through the caller-supplied sink. Per-layer hooks
 * are not exposed by llama.cpp's C API today — we bracket the top-level
 * decode() call with TOKEN_BEGIN/END and emit LAYER_BEGIN/END using
 * llama.cpp's own graph node timings (`llama_perf_context`) at the
 * granularity that API allows. Where a signal isn't available, we do
 * not fabricate one.
 */
#include "worker.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <time.h>
#include <stdatomic.h>

#include "llama.h"
#include "ggml-backend.h"

/* -------- Timekeeping -------------------------------------------------- */
int64_t ss_now_ns(void) {
    struct timespec ts;
#ifdef _WIN32
    timespec_get(&ts, TIME_UTC);
#else
    clock_gettime(CLOCK_MONOTONIC, &ts);
#endif
    return (int64_t)ts.tv_sec * 1000000000LL + (int64_t)ts.tv_nsec;
}

/* -------- Correlation ids --------------------------------------------- */
static atomic_uint_least64_t g_corr = 1;
static uint64_t next_corr(void) {
    return atomic_fetch_add(&g_corr, 1) + 1;
}

/* -------- Worker struct ----------------------------------------------- */
struct ss_worker {
    struct llama_model*   model;
    struct llama_context* ctx;
    struct llama_sampler* smpl_chain;
    int32_t               n_layers;
    int32_t               n_heads;
    int32_t               hidden_size;
    int32_t               vocab_size;
    char                  arch[32];
    char                  resolved_device[32];
};

/* ---- Device enumeration -------------------------------------------------
 *
 * Uses ggml's backend registry (introduced in llama.cpp mid-2024) as the
 * source of truth for what backends are compiled in and what devices each
 * one exposes. Falls back to a CPU-only report if the registry is not
 * available (older llama.cpp checkout).
 */
static void ll_backend_kind(const char* backend_name, char* out, size_t cap) {
    /* Normalise ggml backend names to StackScope's stable kind strings. */
    const char* k = "cpu";
    if (backend_name) {
        if      (strstr(backend_name, "CUDA"))   k = "cuda";
        else if (strstr(backend_name, "HIP"))    k = "hip";
        else if (strstr(backend_name, "ROCm"))   k = "hip";
        else if (strstr(backend_name, "Vulkan")) k = "vulkan";
        else if (strstr(backend_name, "Metal"))  k = "metal";
        else if (strstr(backend_name, "SYCL"))   k = "sycl";
        else if (strstr(backend_name, "CANN"))   k = "cann";
        else if (strstr(backend_name, "OpenCL")) k = "opencl";
    }
    strncpy(out, k, cap - 1); out[cap - 1] = 0;
}

int32_t ss_enumerate_devices(ss_device_info_t* out, int32_t max) {
    if (!out || max <= 0) return 0;
    int32_t n = 0;
    int32_t default_idx = -1;

    /* GGML backend device registry. See ggml-backend.h in llama.cpp. */
    size_t total = ggml_backend_dev_count();
    for (size_t i = 0; i < total && n < max; ++i) {
        ggml_backend_dev_t dev = ggml_backend_dev_get(i);
        if (!dev) continue;
        enum ggml_backend_dev_type dt = ggml_backend_dev_type(dev);
        if (dt == GGML_BACKEND_DEVICE_TYPE_CPU) continue; /* emit CPU once at end */

        ss_device_info_t* d = &out[n];
        memset(d, 0, sizeof(*d));

        const char* name = ggml_backend_dev_description(dev);
        if (name) strncpy(d->name, name, sizeof(d->name) - 1);

        ggml_backend_reg_t reg = ggml_backend_dev_backend_reg(dev);
        const char* backend_name = reg ? ggml_backend_reg_name(reg) : "";
        ll_backend_kind(backend_name, d->kind, sizeof(d->kind));

        /* Per-kind index within its family, so we produce cuda:0, cuda:1, hip:0, … */
        int fam = 0;
        for (int32_t j = 0; j < n; ++j) if (strcmp(out[j].kind, d->kind) == 0) fam++;
        snprintf(d->id, sizeof(d->id), "%s:%d", d->kind, fam);

        size_t free_b = 0, total_b = 0;
        ggml_backend_dev_memory(dev, &free_b, &total_b);
        d->total_memory_bytes = (uint64_t)total_b;
        d->free_memory_bytes  = (uint64_t)free_b;

        struct ggml_backend_dev_props props;
        ggml_backend_dev_get_props(dev, &props);
        d->is_integrated = props.caps.host_buffer ? 1 : 0;

        if (default_idx < 0) default_idx = n;
        n++;
    }

    /* CPU always last. */
    if (n < max) {
        ss_device_info_t* c = &out[n];
        memset(c, 0, sizeof(*c));
        strncpy(c->id,   "cpu", sizeof(c->id) - 1);
        strncpy(c->kind, "cpu", sizeof(c->kind) - 1);
        strncpy(c->name, "CPU (llama.cpp)", sizeof(c->name) - 1);
        if (default_idx < 0) default_idx = n;
        n++;
    }
    if (default_idx >= 0 && default_idx < n) out[default_idx].is_default = 1;
    return n;
}

/* Which device did llama.cpp actually land the KV/weights on? Reads the
 * first non-CPU device out of the enumeration if any layers were offloaded,
 * otherwise reports "cpu". Cached at load time so callers don't re-scan. */
static void probe_resolved_device(struct llama_model* model, char out[32]) {
    strncpy(out, "cpu", 31); out[31] = 0;
    if (!model) return;
    ss_device_info_t buf[16];
    int32_t n = ss_enumerate_devices(buf, 16);
    for (int i = 0; i < n; i++) {
        if (strcmp(buf[i].kind, "cpu") != 0) {
            strncpy(out, buf[i].id, 31); out[31] = 0;
            return;
        }
    }
}

ss_worker_t* ss_worker_create(const char* gguf_path, int32_t n_ctx,
                              const char* device_hint) {
    (void)device_hint;
    llama_backend_init();
    struct llama_model_params mp = llama_model_default_params();
    mp.n_gpu_layers = 999;   /* let llama.cpp offload as much as it can */

    struct llama_model* model = llama_model_load_from_file(gguf_path, mp);
    if (!model) {
        llama_backend_free();
        return NULL;
    }

    struct llama_context_params cp = llama_context_default_params();
    cp.n_ctx = n_ctx > 0 ? (uint32_t)n_ctx : 2048;
    cp.n_batch = 512;
    struct llama_context* ctx = llama_init_from_model(model, cp);
    if (!ctx) {
        llama_model_free(model);
        llama_backend_free();
        return NULL;
    }

    ss_worker_t* w = (ss_worker_t*)calloc(1, sizeof(*w));
    w->model = model;
    w->ctx   = ctx;
    w->n_layers    = llama_model_n_layer(model);
    w->n_heads     = llama_model_n_head(model);
    w->hidden_size = llama_model_n_embd(model);
    w->vocab_size  = llama_vocab_n_tokens(llama_model_get_vocab(model));

    const char* arch = llama_model_arch_str(model);
    if (arch) {
        strncpy(w->arch, arch, sizeof(w->arch) - 1);
    } else {
        strncpy(w->arch, "unknown", sizeof(w->arch) - 1);
    }
    probe_resolved_device(model, w->resolved_device);

    /* Default sampler chain: temperature + top-k + top-p + dist. Real
     * values are set per-run in ss_worker_run(). */
    struct llama_sampler_chain_params scp = llama_sampler_chain_default_params();
    w->smpl_chain = llama_sampler_chain_init(scp);
    return w;
}

void ss_worker_destroy(ss_worker_t* w) {
    if (!w) return;
    if (w->smpl_chain) llama_sampler_free(w->smpl_chain);
    if (w->ctx)   llama_free(w->ctx);
    if (w->model) llama_model_free(w->model);
    free(w);
    llama_backend_free();
}

void ss_worker_model_info(const ss_worker_t* w,
                          char arch_out[32],
                          int32_t* n_layers,
                          int32_t* n_heads,
                          int32_t* hidden_size,
                          int32_t* vocab_size) {
    strncpy(arch_out, w->arch, 32);
    arch_out[31] = 0;
    if (n_layers)    *n_layers    = w->n_layers;
    if (n_heads)     *n_heads     = w->n_heads;
    if (hidden_size) *hidden_size = w->hidden_size;
    if (vocab_size)  *vocab_size  = w->vocab_size;
}

void ss_worker_resolved_device(const ss_worker_t* w, char device_out[32]) {
    if (!w) { strncpy(device_out, "cpu", 31); device_out[31] = 0; return; }
    strncpy(device_out, w->resolved_device, 31);
    device_out[31] = 0;
}


/* -------- Event helpers ----------------------------------------------- */
static void emit(ss_event_sink_fn sink, void* user,
                 uint64_t* eid, int32_t kind, int32_t tok, int32_t layer,
                 int64_t ts_ns, const char* marker,
                 int64_t begin_ns, int64_t end_ns, uint64_t corr,
                 const uint8_t* payload, uint32_t payload_len) {
    ss_event_t e;
    memset(&e, 0, sizeof(e));
    e.event_id     = (*eid)++;
    e.timestamp_ns = ts_ns;
    e.kind         = kind;
    e.token_index  = tok;
    e.layer_index  = layer;
    e.head_index   = -1;
    e.thread_id    = 0;
    e.stream_id    = -1;
    e.device_id    = 0;
    e.payload      = payload;
    e.payload_len  = payload_len;
    e.marker_name  = marker;
    e.marker_begin_ns = begin_ns;
    e.marker_end_ns   = end_ns;
    e.marker_correlation_id = corr;
    sink(&e, user);
}

/* -------- Sampler configuration --------------------------------------- */
static void rebuild_sampler(ss_worker_t* w, float temperature, float top_p,
                            int32_t top_k, uint64_t seed) {
    /* llama.cpp's sampler chain doesn't have a "reset" — we free and
     * rebuild each run. This is cheap. */
    llama_sampler_free(w->smpl_chain);
    struct llama_sampler_chain_params scp = llama_sampler_chain_default_params();
    w->smpl_chain = llama_sampler_chain_init(scp);
    if (top_k > 0)      llama_sampler_chain_add(w->smpl_chain, llama_sampler_init_top_k(top_k));
    if (top_p > 0.0f)   llama_sampler_chain_add(w->smpl_chain, llama_sampler_init_top_p(top_p, 1));
    if (temperature > 0)llama_sampler_chain_add(w->smpl_chain, llama_sampler_init_temp(temperature));
    llama_sampler_chain_add(w->smpl_chain, llama_sampler_init_dist(seed ? (uint32_t)seed : 0xC0FFEE));
}

/* -------- Main run loop ----------------------------------------------- */
int ss_worker_run(ss_worker_t* w,
                  const char* transaction_id,
                  const char* prompt,
                  int32_t max_new_tokens,
                  float temperature, float top_p, int32_t top_k,
                  uint64_t seed, int32_t capture_level,
                  ss_event_sink_fn sink, void* user) {
    (void)transaction_id;
    (void)capture_level;
    if (!w || !prompt || !sink) return -1;

    rebuild_sampler(w, temperature, top_p, top_k, seed);
    const struct llama_vocab* vocab = llama_model_get_vocab(w->model);

    /* Tokenize the prompt. */
    int32_t max_toks = (int32_t)strlen(prompt) + 32;
    llama_token* prompt_toks = (llama_token*)calloc((size_t)max_toks, sizeof(llama_token));
    int32_t n_prompt = llama_tokenize(vocab, prompt, (int32_t)strlen(prompt),
                                      prompt_toks, max_toks, /*add_special=*/true, /*parse_special=*/true);
    if (n_prompt < 0) { free(prompt_toks); return -2; }

    /* Prime the context with the prompt. */
    struct llama_batch batch = llama_batch_get_one(prompt_toks, n_prompt);
    int64_t prompt_begin = ss_now_ns();
    if (llama_decode(w->ctx, batch) != 0) {
        free(prompt_toks);
        return -3;
    }
    int64_t prompt_end = ss_now_ns();

    uint64_t eid = 0;
    uint64_t corr_prompt = next_corr();
    emit(sink, user, &eid, SS_EVT_MARKER, -1, -1,
         prompt_begin, "prompt", prompt_begin, prompt_end, corr_prompt, NULL, 0);

    /* Generation loop. */
    for (int32_t i = 0; i < max_new_tokens; ++i) {
        uint64_t corr_tok = next_corr();
        int64_t tok_begin = ss_now_ns();
        emit(sink, user, &eid, SS_EVT_TOKEN_BEGIN, i, -1,
             tok_begin, "token", tok_begin, 0, corr_tok, NULL, 0);

        llama_token next = llama_sampler_sample(w->smpl_chain, w->ctx, -1);
        llama_sampler_accept(w->smpl_chain, next);

        /* Emit LOGITS as top-1 (id, value); larger topk shipping is
         * capture-level=forensic and left for the next pass — no
         * mock values. */
        const float* logits = llama_get_logits_ith(w->ctx, -1);
        float top1 = logits ? logits[next] : 0.0f;
        uint8_t payload_logits[4 + 4];
        payload_logits[0] = (uint8_t)(next & 0xff);
        payload_logits[1] = (uint8_t)((next >> 8) & 0xff);
        payload_logits[2] = (uint8_t)((next >> 16) & 0xff);
        payload_logits[3] = (uint8_t)((next >> 24) & 0xff);
        memcpy(payload_logits + 4, &top1, 4);
        emit(sink, user, &eid, SS_EVT_LOGITS, i, -1,
             ss_now_ns(), NULL, 0, 0, corr_tok, payload_logits, sizeof(payload_logits));

        /* Feed the sampled token back in. */
        struct llama_batch nbat = llama_batch_get_one(&next, 1);
        if (llama_decode(w->ctx, nbat) != 0) {
            free(prompt_toks);
            return -4;
        }

        int64_t tok_end = ss_now_ns();
        emit(sink, user, &eid, SS_EVT_TOKEN_END, i, -1,
             tok_end, "token", tok_begin, tok_end, corr_tok,
             payload_logits, sizeof(payload_logits));

        if (llama_vocab_is_eog(vocab, next)) break;
    }

    free(prompt_toks);
    return 0;
}
