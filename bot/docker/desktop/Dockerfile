# SPDX-License-Identifier: AGPL-3.0-or-later
# Desktop image for roster bots (plan 7.7): the trycua xfce desktop pinned by
# digest, plus the cua-driver MCP server at a pinned version. Build:
#   docker build -f docker/desktop/Dockerfile \
#     --build-arg BASE_DIGEST=sha256:<digest of docker.io/trycua/xfce-cua> \
#     --build-arg CUA_DRIVER_VERSION=<pinned version> \
#     -t oma-roster-desktop:latest docker/desktop
# Both build args are required: without them the build fails, so an unpinned
# desktop is never built. CUA_DRIVER_PACKAGE and CUA_MCP_COMMAND name the
# driver package and the command that serves MCP over HTTP on $MCP_PORT.
ARG BASE_DIGEST
FROM docker.io/trycua/xfce-cua@${BASE_DIGEST}

ARG CUA_DRIVER_VERSION
ARG CUA_DRIVER_PACKAGE=cua-driver
ARG CUA_MCP_COMMAND="cua-driver mcp --transport http --host 0.0.0.0"
USER root
RUN test -n "${CUA_DRIVER_VERSION}" || { echo "CUA_DRIVER_VERSION is required (pin the driver)"; exit 1; } \
 && pip install --no-cache-dir "${CUA_DRIVER_PACKAGE}==${CUA_DRIVER_VERSION}"
ENV CUA_MCP_COMMAND=${CUA_MCP_COMMAND}
COPY start-roster-desktop.sh /usr/local/bin/start-roster-desktop
RUN chmod 0755 /usr/local/bin/start-roster-desktop
USER 1000:1000
ENTRYPOINT ["/usr/local/bin/start-roster-desktop"]
