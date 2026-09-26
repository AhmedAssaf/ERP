# ADR-0011: Stay outside the finance licence perimeter

Date: 2026-09-26
Status: Accepted
Deciders: Ahmed Assaf
Related: N-11, F-64, F-65, F-66; docs/12 sections 5 to 7

## Context

Path C (docs/12) keeps the option to add financing on top of the tender platform and decides at year 3 whether to become a licensed lender. Until then WaslaBid must not do anything that needs a SAMA licence, and guarantees must be handled without issuing them. The user adopted these rules as invariants on 2026-09-26.

## Decision

These are fixed points, like sealed envelopes and score locking. No feature, tenant setting, or partner agreement may break them:

1. WaslaBid never holds, receives, or moves funds on behalf of buyers, vendors, or financiers.
2. WaslaBid never underwrites, prices risk, or decides whether anyone is financed.
3. WaslaBid never ranks or recommends one financier over another.
4. A licensed partner, not WaslaBid, files the SAMA outsourcing non-objection for any finance referral.
5. A Saudi fintech lawyer's written opinion comes before signing any finance partner agreement.
6. Bank guarantees are verified, never issued (F-66).

## Consequences

- The platform stays a software company with no capital or licensing requirement until the year-3 decision.
- Changing any rule needs a new ADR and a legal opinion.
- Changes: docs/02 N-11 and F-66; CLAUDE.md decisions.

## Alternatives considered

| Option | Why not now |
|---|---|
| Keep the option to handle funds or underwrite ourselves | Requires a SAMA licence and SAR 5M+ capital (docs/12 section 1) |
