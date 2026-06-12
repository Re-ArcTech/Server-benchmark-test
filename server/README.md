# yubi-bench-server

ゆびサッカー Ver.3.0 の通信方式検証用ベンチサーバー（Go）。
同一の `Envelope` メッセージを **REST / WebSocket / WebRTC** の3方式で受け取り、
同じ `process()` をして返す。Unity 側から各方式の RTT 等を計測して比較する。

## エンドポイント

| メソッド | パス | 方式 | 説明 |
|---|---|---|---|
| GET | `/health` | - | ヘルスチェック（Render用、200 "ok"） |
| POST | `/api/echo` | REST | ボディの Envelope を処理して返す |
| GET | `/ws` | WebSocket | 接続維持。受け取った Envelope を同接続で返す |
| POST | `/rtc/offer` | WebRTC | SDP offer を受け取り answer を返す（簡易シグナリング） |

## メッセージ形式（Envelope）

```json
{
  "type": "echo | player.move | ball.kick | goal.check",
  "seq": 1,
  "timestamp": 1717513200000,   // クライアント送信時刻(ms)。サーバーは触らない
  "serverRecv": 0,              // サーバー受信時刻(μs)。サーバーが埋める
  "serverSend": 0,             // サーバー送信時刻(μs)。サーバーが埋める
  "payload": { }
}
```

`type` ごとのサーバー処理（サーバー権威の負荷感を擬似再現）:

- `echo` / `player.move`: 何もしない（純粋な中継）
- `ball.kick`: 弾道計算を60ステップ（重力・摩擦・跳ね返り）
- `goal.check`: ゴール領域判定

`serverSend - serverRecv` がサーバー内処理時間（μs）。

## ローカル実行

```bash
go run .
# yubi-bench-server listening on :8080

# 動作確認
curl http://localhost:8080/health
curl -X POST http://localhost:8080/api/echo \
  -H "Content-Type: application/json" \
  -d '{"type":"ball.kick","seq":1,"timestamp":0,"payload":{"force":{"x":0,"y":2,"z":10},"chargeRate":0.8,"ballPos":{"x":5,"y":0,"z":6}}}'
```

## ビルド

```bash
go build -o server .
PORT=8080 ./server
```

## Docker

```bash
docker build -t yubi-bench-server .
docker run -p 8080:8080 yubi-bench-server
```

## デプロイ（Render）

リポジトリ直下の `render.yaml` を Render の Blueprint で読み込む。
`rootDir: server` / `dockerfilePath: ./Dockerfile` で本ディレクトリがビルドされる。

> WebRTC は UDP/ICE を使うため Render 標準環境ではリモート接続が確立できない可能性が高い。
> REST/WebSocket はそのままリモート計測可能。

## 依存

- `github.com/gorilla/websocket` — WebSocket
- `github.com/pion/webrtc/v4` — WebRTC（DataChannel）
