# ADR-0001: Serve vendor file uploads over chunked HTTP, not the Blazor circuit

Date: 2026-09-21
Status: Accepted
Deciders: Ahmed Assaf
Related: F-12, F-22, F-23, F-24, F-25; `docs/06-spike-results.md` section 3; `spikes/BlazorUploadSpike`

## Context

The vendor portal is Blazor Server. Blazor's `InputFile` streams a file through the SignalR circuit as a JS interop data stream bound to the connection that started it. Spike 2 showed that a dropped WebSocket reconnects to the same circuit in about a second, but the in-flight stream is lost, the server times out after 60 seconds, and the circuit ends with an unhandled error. Vendors submit offers of tens of megabytes from mobile connections in the last hours before a deadline, so a transport drop mid-upload is a normal event, not an edge case.

## Decision

All vendor file transfers (technical and financial offer files, vendor documents) use a direct chunked HTTP upload: the browser slices the file into 4 MB chunks and sends each as an independent `PUT` with retry to a minimal API endpoint; a final completion call stitches the chunks, computes the SHA-256, stores the object, and records the hash. The Blazor page only starts the upload and shows progress on a best-effort basis. `InputFile` is not used for vendor uploads.

## Consequences

- A WebSocket drop or a Blazor reconnect cannot lose an upload (spike scenario E: 50 MB, forced drop, zero retries, hash verified).
- The completion hash gives the submission receipt (F-25) for free, and the endpoint can refuse chunks after the deadline (F-24) independently of the UI.
- Resume after a page reload needs the upload id and completed chunk indexes kept in browser storage; a later task.
- The upload endpoint must enforce tenant, tender, vendor, and envelope authorization itself, because it is outside the Blazor component's context.
- Financial envelope encryption (F-23) happens at completion time on the server, with the per-tender data key; chunks in the staging area are short-lived and never listable by tenant users.
- `docs/05-mvp-scope.md` row 10 and `docs/03-diagrams.md` section 4 are to be read with this change; the sealed-envelope sequence gains a "chunks then complete" step.

## Alternatives considered

| Option | Why not now |
|---|---|
| Keep `InputFile` and raise the JS interop timeout | The stream is still bound to the old connection; a longer timeout only delays the failure. |
| Move the whole vendor portal to static server rendering with form posts | Fixes uploads but gives up interactive validation and progress; the spike showed only the transfer needs to leave the circuit. |
| A resumable upload protocol such as tus | Right shape, but the chunk-and-complete pattern covers the pilot with far less surface; adopt tus semantics later if resume-across-devices becomes a requirement. |
