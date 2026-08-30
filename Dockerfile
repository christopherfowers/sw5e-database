# sw5e-database content publisher.
#
# An init container for the QA stack: it copies the canonical game content and
# the JSON schemas into a shared volume for the API container to read, verifies
# the copy, and exits. It is not a long-running service.
#
# There is deliberately no .NET runtime and no build stage here. Nothing in this
# image executes managed code - the API reads content through its own
# FileContentRepository - so a runtime base would add roughly 90 MB and a much
# larger patch surface without being used. Alpine supplies the POSIX shell and
# coreutils that the verified copy genuinely needs, in about 8 MB.
#
# Applying SQL migrations is deliberate follow-on work that lands with the
# content graph; this image does not talk to a database and takes no connection
# string.
FROM alpine:3.22@sha256:14358309a308569c32bdc37e2e0e9694be33a9d99e68afb0f5ff33cc1f695dce

LABEL org.opencontainers.image.title="sw5e-database content publisher" \
      org.opencontainers.image.description="Init container that publishes canonical SW5e content and JSON schemas into a shared volume." \
      org.opencontainers.image.source="https://github.com/christopherfowers/sw5e-database" \
      org.opencontainers.image.licenses="MIT"

# Matches the conventional non-root uid used by distroless images, so the API
# container and this one can agree on a single owner for the shared volume.
ARG PUBLISH_UID=65532
ARG PUBLISH_GID=65532

# Copied as root and left read-only for the unprivileged runtime user: the
# baked-in canonical content is the source of truth, and the entrypoint has no
# reason to be able to mutate it.
COPY content/ /opt/sw5e/content/
COPY schemas/ /opt/sw5e/schemas/
COPY LICENSE CONTENT-LICENSE.md /opt/sw5e/
COPY docker/publish-content.sh /usr/local/bin/sw5e-publish-content

RUN addgroup -g "${PUBLISH_GID}" -S sw5e \
 && adduser -u "${PUBLISH_UID}" -G sw5e -S -H -s /sbin/nologin sw5e \
 && chmod 0755 /usr/local/bin/sw5e-publish-content \
 && mkdir -p /srv \
 && chown sw5e:sw5e /srv

USER sw5e:sw5e

ENV SW5E_CONTENT_SOURCE=/opt/sw5e/content \
    SW5E_SCHEMA_SOURCE=/opt/sw5e/schemas \
    SW5E_CONTENT_TARGET=/srv/content \
    SW5E_SCHEMA_TARGET=/srv/schemas

ENTRYPOINT ["/usr/local/bin/sw5e-publish-content"]
