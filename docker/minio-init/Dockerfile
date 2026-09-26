# minio/mc is no longer published on Docker Hub and Chainguard's minio-client image has no shell,
# so the provisioning script runs on wolfi-base with mc copied in.
FROM cgr.dev/chainguard/minio-client:latest AS mc

FROM cgr.dev/chainguard/wolfi-base:latest
COPY --from=mc /usr/bin/mc /usr/bin/mc
COPY provision.sh /provision.sh
ENTRYPOINT ["/bin/sh", "/provision.sh"]
