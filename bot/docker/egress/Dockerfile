# SPDX-License-Identifier: AGPL-3.0-or-later
# The roster egress gate (plan 7.7). Build from the bot folder:
#   docker build -f docker/egress/Dockerfile -t oma-roster-egress:latest .
# Pin the two base images by digest for a release:
#   --build-arg SDK_IMAGE=mcr.microsoft.com/dotnet/sdk@sha256:... --build-arg RUNTIME_IMAGE=mcr.microsoft.com/dotnet/runtime-deps@sha256:...
ARG SDK_IMAGE=mcr.microsoft.com/dotnet/sdk:10.0
ARG RUNTIME_IMAGE=mcr.microsoft.com/dotnet/runtime-deps:10.0

FROM ${SDK_IMAGE} AS build
WORKDIR /src
COPY Directory.Build.props Omabot.sln ./
COPY src/Omabot.Core src/Omabot.Core
COPY src/Omabot.EgressGate src/Omabot.EgressGate
COPY templates templates
RUN dotnet publish src/Omabot.EgressGate/Omabot.EgressGate.csproj -c Release -r linux-x64 --self-contained true \
      -p:PublishSingleFile=true -p:InvariantGlobalization=true -o /out

FROM ${RUNTIME_IMAGE}
COPY --from=build /out/Omabot.EgressGate /usr/local/bin/oma-bot-egress
USER 1000:1000
EXPOSE 3128
ENTRYPOINT ["/usr/local/bin/oma-bot-egress"]
