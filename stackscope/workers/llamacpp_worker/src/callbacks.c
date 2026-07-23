/* Placeholder file kept in the CMake source list to reserve room for
 * fine-grained per-op callbacks once llama.cpp exposes a stable hook
 * API in its C surface. Today the effective granularity is one
 * TOKEN_BEGIN/END per generation step + a MARKER around the prompt
 * decode (see worker.c). No fake events are emitted here.
 */
#include "worker.h"

/* Intentionally no symbols. */
