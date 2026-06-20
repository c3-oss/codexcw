# syntax=docker/dockerfile:1
ARG GO_VERSION=1.26.2

FROM --platform=$BUILDPLATFORM golang:${GO_VERSION}-bookworm AS build
ARG TARGETOS
ARG TARGETARCH
ARG VERSION=dev
ARG COMMIT=none
ARG BUILD_DATE=unknown
WORKDIR /src
COPY go.mod go.sum ./
RUN go mod download
COPY . .
RUN mkdir -p /out && CGO_ENABLED=0 GOOS=$TARGETOS GOARCH=$TARGETARCH \
    go build \
      -ldflags="-s -w \
        -X github.com/c3-oss/codexcw/internal/buildinfo.Version=${VERSION} \
        -X github.com/c3-oss/codexcw/internal/buildinfo.Commit=${COMMIT} \
        -X github.com/c3-oss/codexcw/internal/buildinfo.BuildDate=${BUILD_DATE}" \
      -o /out/ ./cmd/...

FROM gcr.io/distroless/static-debian12 AS codexcw
COPY --from=build /out/codexcw /usr/local/bin/codexcw
ENTRYPOINT ["/usr/local/bin/codexcw"]
