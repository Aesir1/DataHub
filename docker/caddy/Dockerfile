# Stock caddy has no rate limiting; rate_limit comes from the maintainer's plugin.
# Pinned: images float only when the pin is bumped deliberately (keep both FROM lines on one version).
FROM caddy:2.11.7-builder AS builder
RUN xcaddy build --with github.com/mholt/caddy-ratelimit

FROM caddy:2.11.7-alpine
COPY --from=builder /usr/bin/caddy /usr/bin/caddy
