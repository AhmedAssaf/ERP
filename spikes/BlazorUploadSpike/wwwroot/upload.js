// Direct chunked upload that bypasses the Blazor circuit. Each chunk is an independent HTTP PUT with retry,
// so a dropped WebSocket (or a Blazor reconnect) cannot kill the transfer. Progress is reported to .NET on a
// best-effort basis: if the circuit is down at that moment, the progress call fails and is ignored.
export async function chunkedUpload(input, dotnetRef, chunkSize) {
  const file = input.files[0];
  if (!file) throw new Error("no file selected");
  const id = crypto.randomUUID();
  const total = Math.ceil(file.size / chunkSize);
  let retries = 0;
  for (let i = 0; i < total; i++) {
    const blob = file.slice(i * chunkSize, Math.min(file.size, (i + 1) * chunkSize));
    let attempt = 0;
    for (;;) {
      try {
        const r = await fetch(`/api/upload/${id}/${i}`, { method: "PUT", body: blob, headers: { "Content-Type": "application/octet-stream" } });
        if (!r.ok) throw new Error(`HTTP ${r.status}`);
        break;
      } catch (e) {
        attempt++; retries++;
        if (attempt > 30) throw e;
        await new Promise(res => setTimeout(res, Math.min(10000, 500 * attempt)));
      }
    }
    try { await dotnetRef.invokeMethodAsync("OnChunk", i + 1, total, retries); } catch { /* circuit down, progress skipped */ }
  }
  const done = await fetch(`/api/upload/${id}/complete`, { method: "POST" });
  if (!done.ok) throw new Error(`complete failed: HTTP ${done.status}`);
  const j = await done.json();
  j.retries = retries;
  return j;
}
