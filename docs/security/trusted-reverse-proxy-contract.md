# FC2-044 — Trusted reverse-proxy deployment contract

Status: proposed, not yet qualified for deployment.

## Threat boundary

FactoryConnect does not trust arbitrary client-supplied `Forwarded`, `X-Forwarded-For`,
`X-Forwarded-Proto`, or `X-Forwarded-Host` headers. Direct TLS hosting is the
current supported baseline. A reverse-proxy deployment must establish and test
the exact trusted network hop before forwarded headers are enabled.

## Required configuration and ordering

1. Configure explicit `KnownProxies` or narrowly bounded `KnownNetworks`
   from operator-controlled deployment configuration. Do not use wildcard
   networks or clear these collections to accept any source.
2. Permit only required forwarded fields (for example, `X-Forwarded-For`
   and `X-Forwarded-Proto`); do not accept forwarded host without an
   explicit allowed-host policy. Bound `ForwardLimit` to the known hop count.
3. Place forwarded-header processing before HTTPS decisions, HSTS,
   authentication, rate limiting and route handling.
4. Require the edge proxy to strip incoming client forwarding headers and
   emit its own verified values. Restrict network access to the application
   listener to the trusted proxy; do not assume middleware alone provides
   network isolation.
5. Fail closed if trusted proxy settings are absent or invalid. Preserve
   direct HTTPS hosting without proxy forwarding.
6. Rate-limit using the verified effective remote address only after the
   trusted-forwarding middleware; do not partition by raw header strings.

## Acceptance gates

- Direct HTTPS: spoofed forwarding headers cannot alter scheme, host,
  remote address or rate-limit partition.
- Trusted single-hop proxy: verified HTTPS scheme and effective client IP
  are honored, including HSTS and secure-cookie behavior.
- Untrusted peer: forged headers are ignored and do not bypass rate limits.
- Malformed/multi-hop chains: forwarding is bounded to the configured hop
  count, with no attacker-controlled host interpretation.
- Unauthenticated bursts: HTTP 429 occurs before SQL authority access.
- Deployment: record proxy IP/network, hop count, TLS termination model,
  header-stripping behavior and host allowlist as environment-specific
  approval evidence.

No production proxy configuration is authorized by this document.
