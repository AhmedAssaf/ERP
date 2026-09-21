# Spike Results: Arabic PDF and Vendor Upload on a Weak Connection

Date: 2026-09-21
Status: both spikes from `05-mvp-scope.md` section 5 executed. Code is in `spikes/`.
Environment: Windows 11, .NET SDK 9.0.121 (the .NET 10 SDK is not installed on this machine yet; nothing in either spike depends on 10), QuestPDF 2026.9.0, Chrome with DevTools network emulation.

## 1. Verdicts

| Spike | Question | Verdict | What changes in the design |
|---|---|---|---|
| Arabic PDF | Can QuestPDF render a branded PO with mixed Arabic and English, correct shaping, right-to-left tables, and a Saudi-style font? | **Pass** | Keep QuestPDF. One rule for the PDF module: wrap every English-only run in a left-to-right container. |
| Blazor upload | Does a Blazor Server upload survive a 50 MB file on throttled 3G with a connectivity gap? | **Pass with a design change** | Latency spikes are fine. A dropped WebSocket kills the in-flight InputFile stream and, a minute later, the circuit. Vendor file uploads must go over direct chunked HTTP, not through the circuit. The chunked fallback was built and tested in the same spike. |

## 2. Spike 1: Arabic PDF with QuestPDF

Code: `spikes/ArabicPdfSpike/Program.cs`. Run `dotnet run` inside that folder; it writes `bin/Debug/net9.0/out/po-arabic.pdf` and a PNG of the page.

What was rendered: an A4 purchase order with tenant name in Arabic and English, CR and VAT numbers, a bilingual title bar, vendor block, order details with a Hijri date, a six-column BoQ table with Arabic and English descriptions and numeric columns, a totals block with VAT, an Arabic terms paragraph in Traditional Arabic, an English terms paragraph, three signature lines, and a bilingual footer.

Observed:

- Arabic shaping and ligatures are correct in both Tahoma and Traditional Arabic, registered from the Windows fonts folder.
- `ContentFromRightToLeft()` on the page flips the whole layout correctly: the header row, the table columns, the totals alignment, and the signature order.
- Mixed runs such as "س.ت 1010123456 | الرقم الضريبي 300012345600003" render in the right order without any markup.
- Numbers in the BoQ columns stay left-to-right and align as expected.
- **Defect found and fixed:** an English-only line inside the right-to-left page had its trailing period moved to the start ("Al-Faisal Contracting Co." rendered as ".Al-Faisal Contracting Co"). Same for the English terms paragraph. Wrapping those items in `ContentFromLeftToRight()` fixes it. A date such as "(21 Sep 2026)" embedded in an Arabic line needed Unicode isolate marks (U+2066 and U+2069) to keep its parts in order.
- `FontManager.RegisterFont(Stream)` is obsolete in QuestPDF 2026.9; use `RegisterFontFromStream`.

Rules for the product's PDF module:

1. Embed fonts as resources (Noto Naskh Arabic and Noto Sans, or the tenant's licensed fonts) so Linux containers render identically to this Windows run.
2. Every text element that is English-only goes in a left-to-right container. Mixed Arabic-first lines need nothing.
3. Use isolate marks around embedded Latin fragments in Arabic sentences.
4. Add a golden-image test: render the PO and compare the PNG against a stored reference so a QuestPDF upgrade cannot silently change Arabic layout.

## 3. Spike 2: Blazor Server upload on a weak connection

Code: `spikes/BlazorUploadSpike/`. Run with `ASPNETCORE_ENVIRONMENT=Development dotnet run --no-launch-profile` and open `http://127.0.0.1:5273/`. The page shows the circuit id, a per-second tick counter that proves the circuit is alive, a draft text field that must survive reconnects, the standard `InputFile` uploader, and the chunked fallback uploader.

Test file: 50 MB of random bytes, SHA-256 prefix `F1E661FFA8BF5D00`, so the server can prove it received every byte.

Throttle: Chrome DevTools "Fast 3G" (about 1.6 Mbps down, 750 kbps up, 560 ms round trip) on a 390 by 844 mobile viewport, "Slow 3G" for the latency spike (2 s round trip).

### 3.1 Results by scenario

| # | Scenario | Circuit | Draft state | Upload | Notes |
|---|---|---|---|---|---|
| A | Fast 3G, then Slow 3G for 10 s, back to Fast 3G | Same circuit, ticks never stopped, no reconnect overlay | Kept | Kept moving | Latency spikes are harmless. Render batches queued up to 8 s and then drained. |
| B | Fast 3G, then DevTools "Offline" for 6 s | Same circuit | Kept | Kept moving | The established WebSocket was not severed by the offline emulation, so nothing happened. An earlier run of the same step did sever it and ended on Chrome's offline error page after the client reloaded; the emulation is not deterministic, which is why scenario C was added. |
| C | Force the WebSocket closed from inside the page mid-upload | Reconnected in 1.2 s to a new WebSocket, **same circuit id**, ticks continued | Kept | **Stalled** at 18.7 percent. Sixty seconds later the server threw `TimeoutException: Did not receive any data in the allotted time` from the InputFile stream, the circuit reported an unhandled error, and the page showed the Blazor error banner | This is the real failure mode. Circuit-level reconnect works; the JS-to-.NET data stream that `InputFile` uses does not survive a transport change. |
| D | Clean run, Fast 3G, no interference, 50 MB through `InputFile` | Same circuit, 649 ticks | Kept | **Completed**: 52,428,800 bytes, SHA-256 prefix `F1E661FFA8BF5D00` matches the source file, 536 s (about 98 KB per second) | Baseline: `InputFile` is correct and complete when nothing interrupts the socket. |
| E | Chunked HTTP fallback, Fast 3G, WebSocket forced closed after chunk 1 of 13 | Reconnected in 1.2 s, **same circuit id**, 655 ticks, no reconnect overlay, no error banner | Kept | **Completed**: 52,428,800 bytes in 13 chunks, SHA-256 prefix `F1E661FFA8BF5D00` matches, **0 retries**, 643 s. Progress calls resumed on the new connection | The fix under test. The transfer never noticed the drop because no chunk was in flight over the circuit. |

### 3.2 Why scenario C fails

`InputFile.OpenReadStream` pulls the file through the SignalR connection as a JS interop data stream tied to the connection that started it. When Blazor reconnects, it re-attaches the circuit to a new connection, but the half-finished stream on the old connection is gone. The server side keeps waiting on the pipe until the JS interop timeout (60 s) fires. The exception surfaces as a circuit error rather than inside the component's `try` block, which is why the page ends with the error banner instead of a friendly "upload failed" message.

Blazor's reconnect defaults, read from the served `blazor.web.js`: 30 retries, the first 10 with no delay, then 5 s, then 30 s. If the server answers that the circuit is unknown, the client calls `location.reload()` immediately. That is what happened in the non-deterministic run of scenario B: the reload fired while the browser was still offline and landed on the error page. In the product this means a vendor who loses signal for long enough for the circuit to be evicted (3 minutes by default) will see a full page reload and lose any unsaved form state.

### 3.3 The fix: direct chunked HTTP upload

`spikes/BlazorUploadSpike/wwwroot/upload.js` slices the file into 4 MB chunks and sends each as an independent `PUT` to a minimal API endpoint, retrying a chunk up to 30 times with backoff. A final `POST` asks the server to stitch the chunks and return the SHA-256. Progress is reported to the Blazor component on a best-effort basis; if the circuit is down at that moment the call fails and is ignored, and the upload keeps going.

Properties that matter for F-22 to F-24:

- The transfer does not depend on the circuit at all. A reconnect, or even a full Blazor error, cannot stop it.
- A retried chunk overwrites the same file, so retries are idempotent.
- The server can enforce the deadline per chunk (reject `PUT` after the tender closes) and per completion (F-24).
- Resuming after a page reload is a small extension: keep the upload id and completed chunk indexes in `localStorage`.
- The receipt hash (F-25) falls out of the completion step for free.

### 3.4 Other observations from the spike

- Uploading through `InputFile` at Fast 3G ran at roughly 60 to 120 KB per second in these runs, so a 50 MB technical offer takes 7 to 14 minutes on a weak connection. The vendor UI must show progress and must not block the rest of the wizard while this runs.
- Raising `MaximumReceiveMessageSize` to 1 MB on the hub is needed for `InputFile` to be usable at all with large files; the default 32 KB makes the transfer very chatty.
- The mobile-viewport emulation in DevTools reloads the page when applied, which invalidated one run. Apply emulation before loading the page.
- Running with `--no-launch-profile` drops `ASPNETCORE_ENVIRONMENT=Development`, and in Production mode the scoped stylesheet 500s because static web assets are off. Set the environment explicitly when running spikes this way.

## 4. Decisions taken from the spikes

1. QuestPDF stays. The four PDF rules in section 2 go into the design spec for F-36.
2. Vendor file uploads use direct chunked HTTP with a completion step and server-side hashing, never `InputFile` through the circuit. The Blazor page only shows progress. This applies to F-22 technical and financial uploads and to vendor documents in F-12.
3. Blazor Server stays for both portals. Scenario A shows latency is not a problem for the circuit, and scenario C is solved by taking uploads off the circuit.
4. Configure reconnection so a vendor never sees a silent full-page reload: raise `DisconnectedCircuitRetentionPeriod`, keep form drafts in browser storage, and replace the default reconnect UI text with Arabic and English copy.
5. Add an Arabic PDF golden-image test and a throttled upload end-to-end test to the CI plan.

## 5. Update to document 05

Section 5 of `05-mvp-scope.md` listed the fallback for spike 2 as "move the vendor portal pages to static server rendering with plain form posts". The spike shows a narrower fix is enough: keep Blazor Server, move only the file transfer to chunked HTTP. Row 10 of the MVP feature list (F-22 to F-24) should be read with that change.
