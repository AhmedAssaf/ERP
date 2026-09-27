// FileUpload's script (ADR-0001, docs/06 spike 2): the file goes to the server over chunked HTTP with fetch, never
// through the Blazor circuit. Start, then each chunk (File.slice, at the size the server gave) with its SHA-256 in hex,
// then complete. A chunk that fails for a transient reason is sent again up to three times with a growing pause; after
// that the upload stops and resume() continues from that chunk (a chunk sent again replaces the one before). Only the
// file's name, size and type, the progress and the outcome go back to .NET.

const retries = 3;
const pauses = [1000, 2000, 4000];
const uploads = new Map();

/** The chosen file's name, size and content type, or null when none is chosen. */
export function describe(input) {
    const file = input && input.files && input.files[0];
    return file ? { name: file.name, size: file.size, type: file.type || "" } : null;
}

/** Starts an upload of the chosen file; resolves with its outcome ({ kind, code, resumeFrom, documentId }). */
export async function upload(input, request, dotnet) {
    const file = input && input.files && input.files[0];
    if (!file) {
        return { kind: "rejected", code: "refused" };
    }

    forget(request.key);
    const state = { input, file, request, dotnet, uploadId: null, chunkSize: 0, chunkCount: 0, controller: null };
    uploads.set(request.key, state);
    return run(state, 0);
}

/** Continues a failed upload from chunk fromChunk (the chunk count when only the completion is left). */
export async function resume(key, fromChunk) {
    const state = uploads.get(key);
    if (!state) {
        return { kind: "failed", resumeFrom: 0 };
    }

    return run(state, fromChunk);
}

/** Stops the upload in progress; its promise resolves as cancelled. */
export function cancel(key) {
    const state = uploads.get(key);
    if (state && state.controller) {
        state.cancelled = true;
        state.controller.abort();
    }
}

/** Drops what is kept for a retry (the component is gone, or a new upload replaces this one). */
export function forget(key) {
    const state = uploads.get(key);
    if (state && state.controller) {
        state.controller.abort();
    }

    uploads.delete(key);
}

async function run(state, from) {
    state.controller = new AbortController();
    state.cancelled = false;
    state.position = from;
    try {
        if (!state.uploadId) {
            const started = await send(state, "POST", state.request.endpoint, json({
                documentType: state.request.documentType,
                fileName: state.file.name,
                size: state.file.size,
                contentType: state.file.type,
            }));
            if (started.outcome) {
                return finish(state, started.outcome, 0);
            }

            const body = await started.response.json();
            state.uploadId = body.uploadId;
            state.chunkSize = body.chunkSize;
            state.chunkCount = body.chunkCount;
            from = 0;
        }

        for (let index = from; index < state.chunkCount; index++) {
            state.position = index;
            const start = index * state.chunkSize;
            const bytes = await state.file.slice(start, Math.min(start + state.chunkSize, state.file.size)).arrayBuffer();
            const hash = await sha256Hex(bytes);
            const sent = await send(state, "PUT", `${base(state)}/chunks/${index}`, { body: bytes, headers: { "X-Chunk-Sha256": hash } });
            if (sent.outcome) {
                return finish(state, sent.outcome, index);
            }

            await state.dotnet.invokeMethodAsync("ReportProgressAsync", Math.min(start + state.chunkSize, state.file.size), state.file.size);
        }

        state.position = state.chunkCount;
        await state.dotnet.invokeMethodAsync("ReportScanningAsync");
        const completed = await send(state, "POST", `${base(state)}/complete`, json({ expiresOn: state.request.expiresOn }));
        if (completed.outcome) {
            return finish(state, completed.outcome, state.chunkCount);
        }

        const result = await completed.response.json();
        return finish(state, { kind: result.status === "clean" ? "done" : "pending", documentId: result.documentId }, state.chunkCount);
    } catch (error) {
        if (state.cancelled) {
            return finish(state, { kind: "cancelled" }, 0);
        }

        // Reading the file or hashing failed (the file changed or was removed): nothing to resume.
        console.error("FileUpload: the upload stopped.", error);
        return finish(state, { kind: "failed" }, state.uploadId ? state.position : 0);
    }
}

// Sends one request; retries transient failures (network, 5xx, 408, 429, a chunk that arrived damaged, a completion
// still running). Returns { response } on success or { outcome } when the upload ends here.
async function send(state, method, url, init) {
    const headers = Object.assign({}, init.headers, { [state.request.tokenHeader]: state.request.token || "" });
    for (let attempt = 0; ; attempt++) {
        let response = null;
        try {
            response = await fetch(url, { method, body: init.body, headers, credentials: "same-origin", signal: state.controller.signal });
        } catch (error) {
            if (state.cancelled) {
                throw error;
            }
        }

        if (response && response.ok) {
            return { response };
        }

        const code = response ? await errorCode(response) : null;
        if (response && !transient(response.status, code)) {
            return { outcome: refusal(response.status, code) };
        }

        if (attempt >= retries) {
            return { outcome: { kind: "failed" } };
        }

        await pause(pauses[attempt], state.controller.signal);
    }
}

// The upload API's codes for a chunk that arrived damaged and for a completion still running: sending again helps.
// Matched by suffix, since the API prefixes its codes with its module ("vendor.chunk_hash_mismatch").
const retryableCodes = ["chunk_wrong_size", "chunk_hash_mismatch", "upload_in_progress"];

function transient(status, code) {
    if (status >= 500 || status === 408) {
        return true;
    }

    // 429 with a code is a refusal the server explains (too many uploads); without one it is the request rate limit.
    if (status === 429) {
        return !code;
    }

    return !!code && retryableCodes.some(c => code === c || code.endsWith("." + c));
}

function refusal(status, code) {
    if (status === 401 || status === 403) {
        return { kind: "rejected", code: "signed_out" };
    }

    return { kind: "rejected", code: code || "refused" };
}

function finish(state, outcome, resumeFrom) {
    if (outcome.kind === "failed") {
        return { kind: "failed", resumeFrom };
    }

    uploads.delete(state.request.key);
    if (outcome.kind !== "cancelled") {
        // A new choice of the same file must fire the change event again.
        state.input.value = "";
    }

    return outcome;
}

async function errorCode(response) {
    try {
        const body = await response.json();
        return body && typeof body.code === "string" ? body.code : null;
    } catch {
        return null;
    }
}

function base(state) {
    return `${state.request.endpoint}/${state.uploadId}`;
}

function json(value) {
    return { body: JSON.stringify(value), headers: { "Content-Type": "application/json" } };
}

async function sha256Hex(bytes) {
    const digest = new Uint8Array(await crypto.subtle.digest("SHA-256", bytes));
    return Array.from(digest, b => b.toString(16).padStart(2, "0")).join("");
}

function pause(ms, signal) {
    return new Promise((resolve, reject) => {
        const timer = setTimeout(resolve, ms);
        signal.addEventListener("abort", () => {
            clearTimeout(timer);
            reject(signal.reason);
        }, { once: true });
    });
}
