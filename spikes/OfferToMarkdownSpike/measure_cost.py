"""Cost per offer when the original PDF is sent to the model (spike W-22).

  python measure_cost.py            estimate from the documented billing rules (no API key needed)
  python measure_cost.py --measure  exact input tokens from the free count_tokens endpoint (needs ANTHROPIC_API_KEY)

Documented rules (platform.claude.com, PDF support and Vision pages, read 2026-09-26):
- each PDF page is billed as its extracted text, "typically 1,500 to 3,000 tokens per page depending on content
  density", plus the page as an image;
- an image costs ceil(width/28) x ceil(height/28) tokens, capped at 4,784 tokens (2,576 px long edge) on Claude
  4.7 and later models, 1,568 on older ones; the resolution a PDF page is rendered at is not documented;
- an AWS example quotes about 7,000 tokens for a 3-page PDF with full visual understanding (about 2,300 a page).
"""
import base64
import json
import os
import sys
from pathlib import Path

HERE = Path(__file__).parent
PRICES = {"claude-sonnet-5": (2.00, 10.00), "claude-haiku-4-5": (1.00, 5.00)}  # USD per million, input and output
OUTPUT_TOKENS = 3000  # the review JSON came to 3.4 to 5.5 KB per offer
FIXED_TOKENS = 6000   # prompt v3 plus a short RFP in Markdown, sent with every offer
PER_PAGE = {"low": 2300, "typical": 1500 + 1568, "high": 3000 + 4784}  # text plus page image, from the rules above
PAGES = [3, 20, 50, 80]


def usd(tokens_in, model, batch=False):
    pin, pout = PRICES[model]
    cost = tokens_in / 1e6 * pin + OUTPUT_TOKENS / 1e6 * pout
    return cost / 2 if batch else cost


def estimate():
    print("Estimated cost per offer in USD, from the documented rules (input: pages x tokens per page, plus prompt and RFP)")
    print("| Pages | Tokens per page | Input tokens | Sonnet | Sonnet, Batch API | Haiku | Haiku, Batch API |")
    print("|---|---|---|---|---|---|---|")
    for pages in PAGES:
        for name, per_page in PER_PAGE.items():
            tin = pages * per_page + FIXED_TOKENS
            print(f"| {pages} | {name} {per_page:,} | {tin:,} | {usd(tin, 'claude-sonnet-5'):.2f} | "
                  f"{usd(tin, 'claude-sonnet-5', True):.2f} | {usd(tin, 'claude-haiku-4-5'):.2f} | "
                  f"{usd(tin, 'claude-haiku-4-5', True):.2f} |")
    md = 80 * 2000 + FIXED_TOKENS
    print(f"\nFor comparison, the same 80-page offer converted to Markdown (about 2,000 text tokens a page): "
          f"{md:,} input tokens, Sonnet {usd(md, 'claude-sonnet-5'):.2f}, Haiku {usd(md, 'claude-haiku-4-5'):.2f}")


def measure():
    import anthropic
    client = anthropic.Anthropic()
    prompt = (HERE / "prompts" / "offer-review-v3.md").read_text(encoding="utf-8")
    print("| Offer | Pages | Model | Input tokens | Tokens per page | USD per offer | USD, Batch API |")
    print("|---|---|---|---|---|---|---|")
    for pdf in sorted((HERE / "samples" / "tenders").glob("*/offers/*/technical.pdf")):
        tender = pdf.parents[2].name
        rfp = (HERE / "out" / "tenders" / tender / "rfp.md").read_text(encoding="utf-8")
        import pymupdf
        pages = len(pymupdf.open(pdf))
        doc = {"type": "document", "source": {"type": "base64", "media_type": "application/pdf",
                                              "data": base64.b64encode(pdf.read_bytes()).decode()}}
        for model in PRICES:
            base = client.messages.count_tokens(model=model, system=prompt,
                                                messages=[{"role": "user", "content": rfp}]).input_tokens
            full = client.messages.count_tokens(model=model, system=prompt, messages=[{"role": "user", "content": [
                {"type": "text", "text": rfp}, doc, {"type": "text", "text": "Review this offer against the RFP."}]}]).input_tokens
            print(f"| {tender}/{pdf.parent.name} | {pages} | {model} | {full:,} | {(full - base) // pages:,} | "
                  f"{usd(full, model):.3f} | {usd(full, model, True):.3f} |")


if __name__ == "__main__":
    if "--measure" in sys.argv:
        if not (os.environ.get("ANTHROPIC_API_KEY") or os.environ.get("ANTHROPIC_AUTH_TOKEN")):
            sys.exit("No API key: set ANTHROPIC_API_KEY. count_tokens is free, so measuring costs nothing.")
        measure()
    else:
        estimate()
