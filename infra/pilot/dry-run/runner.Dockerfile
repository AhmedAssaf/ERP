# Local rehearsal ONLY (dry-run/README.md, docs/19 section 10): the "VM" in which the real pilot scripts run as root.
# Ubuntu 24.04 like the VM, with the tools bootstrap.sh would install (jq, curl, git, python3, openssl) and the Docker CLI
# with the Compose plugin, talking to the laptop's Docker engine through its socket. Never used on the VM.
FROM docker:29-cli AS cli

FROM ubuntu:24.04
RUN apt-get update \
 && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
      bash ca-certificates curl git jq openssl procps python3 rsync util-linux \
 && rm -rf /var/lib/apt/lists/*
COPY --from=cli /usr/local/bin/docker /usr/local/bin/docker
COPY --from=cli /usr/local/libexec/docker/cli-plugins/docker-compose /usr/local/libexec/docker/cli-plugins/docker-compose
CMD ["sleep", "infinity"]
