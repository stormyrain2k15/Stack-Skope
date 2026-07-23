/* StackScope llama.cpp worker — gRPC glue.
 *
 * This file is compiled as C++ (extension .cc) because gRPC's C++ API
 * is the practical way to speak protobuf from C. The generated proto
 * headers (events.pb.h, worker.pb.h, worker.grpc.pb.h) come from the
 * CMake protoc step.
 *
 * The entire gRPC surface is <200 lines: one servicer that forwards
 * calls into the C worker functions defined in worker.c.
 */
#include <grpcpp/grpcpp.h>
#include <atomic>
#include <string>
#include <thread>
#include <vector>
#include <mutex>
#include <condition_variable>

#include "worker.h"
#include "events.pb.h"
#include "worker.pb.h"
#include "worker.grpc.pb.h"

using stackscope::v1::InferenceWorker;
using stackscope::v1::Event;
using stackscope::v1::TraceMarker;

namespace {

class Servicer final : public InferenceWorker::Service {
public:
    grpc::Status GetCapabilities(grpc::ServerContext*,
                                 const stackscope::v1::CapabilitiesRequest*,
                                 stackscope::v1::CapabilitiesReply* reply) override {
        reply->set_worker_kind("llamacpp");
        reply->set_version("0.1.0");
        reply->add_supported_formats("gguf");
        reply->set_supports_attention(false);
        reply->set_supports_activations(false);
        reply->set_supports_tensor_readback(false);

        // Real enumeration via ggml backend registry. Populates both the
        // legacy `devices` string list AND the rich `device_info` list so
        // the WPF dropdown renders "cuda:0 · NVIDIA RTX 4090 · 24 GB".
        ss_device_info_t buf[32];
        int32_t n = ss_enumerate_devices(buf, 32);
        for (int32_t i = 0; i < n; ++i) {
            reply->add_devices(buf[i].id);
            auto* d = reply->add_device_info();
            d->set_id(buf[i].id);
            d->set_kind(buf[i].kind);
            d->set_name(buf[i].name);
            d->set_total_memory_bytes(buf[i].total_memory_bytes);
            d->set_free_memory_bytes(buf[i].free_memory_bytes);
            d->set_compute_capability(buf[i].compute_capability);
            d->set_driver_version(buf[i].driver_version);
            d->set_multi_processor_count(buf[i].multi_processor_count);
            d->set_is_integrated(buf[i].is_integrated != 0);
            d->set_is_default(buf[i].is_default != 0);
        }
        return grpc::Status::OK;
    }

    grpc::Status LoadModel(grpc::ServerContext*,
                           const stackscope::v1::LoadModelRequest* req,
                           stackscope::v1::LoadModelReply* reply) override {
        std::lock_guard<std::mutex> g(mu_);
        if (worker_) ss_worker_destroy(worker_);
        worker_ = ss_worker_create(req->model_path().c_str(),
                                    req->n_ctx(),
                                    req->device().c_str());
        if (!worker_) {
            return grpc::Status(grpc::StatusCode::INTERNAL,
                                "ss_worker_create failed");
        }
        char arch[32] = {0};
        int32_t nL = 0, nH = 0, hs = 0, vs = 0;
        ss_worker_model_info(worker_, arch, &nL, &nH, &hs, &vs);
        char resolved[32] = {0};
        ss_worker_resolved_device(worker_, resolved);
        reply->set_model_handle("m-0");
        reply->set_architecture(arch);
        reply->set_n_layers(nL);
        reply->set_n_heads(nH);
        reply->set_hidden_size(hs);
        reply->set_vocab_size(vs);
        reply->set_resolved_device(resolved);
        return grpc::Status::OK;
    }

    grpc::Status UnloadModel(grpc::ServerContext*,
                             const stackscope::v1::UnloadModelRequest*,
                             stackscope::v1::UnloadModelReply*) override {
        std::lock_guard<std::mutex> g(mu_);
        if (worker_) { ss_worker_destroy(worker_); worker_ = nullptr; }
        return grpc::Status::OK;
    }

    grpc::Status RunInference(grpc::ServerContext* ctx,
                              const stackscope::v1::RunInferenceRequest* req,
                              grpc::ServerWriter<Event>* writer) override {
        std::lock_guard<std::mutex> g(mu_);
        if (!worker_) {
            return grpc::Status(grpc::StatusCode::FAILED_PRECONDITION,
                                "no model loaded");
        }

        struct SinkCtx {
            grpc::ServerWriter<Event>* writer;
            std::string txid;
            std::atomic<bool> ok{true};
            std::mutex m;
        } sc { writer, req->transaction_id(), {true}, {} };

        auto sink = [](const ss_event_t* e, void* user) {
            SinkCtx* s = reinterpret_cast<SinkCtx*>(user);
            if (!s->ok.load()) return;

            Event msg;
            msg.set_event_id(e->event_id);
            msg.set_transaction_id(s->txid);
            msg.set_timestamp_ns(e->timestamp_ns);
            msg.set_kind(static_cast<stackscope::v1::EventKind>(e->kind));
            msg.set_token_index(e->token_index);
            msg.set_layer_index(e->layer_index);
            msg.set_head_index(e->head_index);
            msg.set_thread_id(e->thread_id);
            msg.set_stream_id(e->stream_id);
            msg.set_device_id(e->device_id);
            if (e->payload && e->payload_len)
                msg.set_payload(e->payload, e->payload_len);
            if (e->marker_name) {
                auto* m = msg.add_markers();
                m->set_name(e->marker_name);
                m->set_begin_ns(e->marker_begin_ns);
                m->set_end_ns(e->marker_end_ns);
                m->set_color_rgba(0xFF8FA9C6u);
                m->set_thread_id(e->thread_id);
                m->set_stream_id(e->stream_id);
                m->set_correlation_id(e->marker_correlation_id);
            }
            std::lock_guard<std::mutex> g(s->m);
            if (!s->writer->Write(msg)) s->ok.store(false);
        };

        int rc = ss_worker_run(worker_,
            req->transaction_id().c_str(),
            req->prompt().c_str(),
            req->max_new_tokens(),
            req->temperature(),
            req->top_p(),
            req->top_k(),
            req->seed(),
            static_cast<int32_t>(req->capture_level()),
            sink, &sc);

        if (ctx->IsCancelled()) {
            return grpc::Status(grpc::StatusCode::CANCELLED, "client cancelled");
        }
        if (rc != 0) {
            return grpc::Status(grpc::StatusCode::INTERNAL,
                                "ss_worker_run failed: rc=" + std::to_string(rc));
        }
        return grpc::Status::OK;
    }

    grpc::Status Heartbeat(grpc::ServerContext*,
                           const stackscope::v1::HeartbeatRequest*,
                           stackscope::v1::HeartbeatReply* reply) override {
        reply->set_uptime_ns(ss_now_ns() - start_ns_);
        reply->set_active_txns(worker_ ? 1 : 0);
        return grpc::Status::OK;
    }

private:
    std::mutex   mu_;
    ss_worker_t* worker_{nullptr};
    int64_t      start_ns_{ss_now_ns()};
};

}  // namespace

int main(int argc, char** argv) {
    std::string endpoint = "0.0.0.0:50502";
    for (int i = 1; i < argc; ++i) {
        std::string a = argv[i];
        if (a == "--endpoint" && i + 1 < argc) endpoint = argv[++i];
    }

    Servicer svc;
    grpc::ServerBuilder b;
    b.AddListeningPort(endpoint, grpc::InsecureServerCredentials());
    b.RegisterService(&svc);
    b.SetMaxSendMessageSize(128 * 1024 * 1024);
    b.SetMaxReceiveMessageSize(128 * 1024 * 1024);
    auto server = b.BuildAndStart();
    fprintf(stderr, "StackScope llama.cpp worker listening on %s\n", endpoint.c_str());
    server->Wait();
    return 0;
}
