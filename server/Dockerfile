# --- build stage ---
FROM golang:1.25-alpine AS build
WORKDIR /src
# 依存だけ先にコピーしてキャッシュを効かせる
COPY go.mod go.sum ./
RUN go mod download
COPY . .
# 静的バイナリをビルド
RUN CGO_ENABLED=0 GOOS=linux go build -o /out/server .

# --- run stage ---
FROM alpine:3.20
# WebRTC が外向き接続でTLS等を使うため証明書を入れておく
RUN apk add --no-cache ca-certificates
COPY --from=build /out/server /server
# Render は PORT を注入する。ローカル用のデフォルトも置いておく。
ENV PORT=8080
EXPOSE 8080
ENTRYPOINT ["/server"]
