# ADR-0005: Review offers with Claude Sonnet on the original PDF, through the Anthropic SDK, with tenant consent

Date: 2026-09-26
Status: Accepted
Deciders: Ahmed Assaf
Related: F-45 to F-50, N-01, docs/02 section 5 item 7, spike W-22 (docs/06 section 7), spec `docs/superpowers/specs/2026-09-26-ai-offer-review-design.md`

## Context

Document 02 named the Claude API behind `Microsoft.Extensions.AI`, and document 01 planned parsing and OCR into page chunks before any model call. Spike W-22 tested that path on three fictional tenders with answer keys: converting Arabic PDFs lost letter or word order in most tools, OCR on scans lost whole lines and the evidence behind red flags, and Claude Sonnet reading the converted Markdown agreed with the key on 65 percent of requirements for PDF offers. Sending the original PDF instead reached 96 percent, 98 percent on scans, with no requirement wrongly called met. N-01 requires processing in Saudi Arabia, and the Claude API has no in-Kingdom endpoint today.

## Decision

1. Offer review uses Claude Sonnet (`claude-sonnet-5`, configurable) through the Claude API. PDFs go to the model as base64 PDF documents inside the batch request (nothing stored at the provider); DOCX goes as text from the Open XML SDK. No OCR or Markdown conversion for AI review.
2. The `Ai` module calls the official Anthropic .NET SDK directly behind its own `IOfferReviewModel` port, not through `Microsoft.Extensions.AI`, because the design needs the Batch API, API-enforced structured output, and token counting.
3. AI is off by default per tenant. A Tenant admin turns it on and records consent to processing outside the Kingdom; this is the accepted exception to N-01 for AI features only, recorded per tenant.
4. One review per offer, run at technical opening through the Batch API; financial envelopes never go to a model; price checks (F-48) and cross-offer integrity comparison (F-49) are code.

## Consequences

- Scans and Arabic PDFs are reviewed accurately without a conversion pipeline to build and maintain.
- Provider-specific code is limited to one adapter; replacing the provider (for example an in-Kingdom endpoint) means a new adapter and passing the evaluation gate, not a domain change.
- PDF input costs about 1.5 to 3.5 times Markdown input (about USD 0.53 for a typical 80-page offer, half through the Batch API); a per-tenant monthly budget caps it.
- AI features cannot be used by a tenant that does not accept processing outside the Kingdom until an in-Kingdom option exists.
- Changes: docs/02 stack row AI and document parsing, F-47 to F-49 acceptance, section 5 item 7; docs/01 section 5.4; docs/03 diagrams 2 and 10; CLAUDE.md decisions.

## Alternatives considered

| Option | Why not now |
|---|---|
| Convert to Markdown (Docling, PdfPig with a visual-to-logical pass, OCR) and send text | 65 percent agreement on PDF offers against 96 percent for the original PDF; scans lose the evidence behind red flags |
| `Microsoft.Extensions.AI` abstraction | Does not cover batches, enforced output schemas, or token counting; our own port gives the same swappability |
| Claude Haiku | About half the price, but repeated the same wrong "met" verdicts across runs (Excel reports as an electronic system, a split warranty); Sonnet made none |
| Self-hosted open model in Jeddah | In-Kingdom and free, but untested for this task and CPU-only on the pilot host |
| AI off until an in-Kingdom endpoint exists | Keeps N-01 whole but delays the main differentiator; stays available as the default for tenants that do not consent |
