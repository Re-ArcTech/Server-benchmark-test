# ゆびサッカー Ver.3.0 — 通信方式ベンチマーク

Unity ↔ サーバー間の通信を **REST / WebSocket / WebRTC(DataChannel)** の3方式で実装し、
同一シナリオで RTT・ジッター・接続確立時間・サーバー処理時間を計測して、
**どの場面でどの方式が最適か**を判定するための検証プロジェクト。

> 結論を先に: ゲーム中のリアルタイム同期は **WebSocket**、ログイン等の単発処理は **REST**、
> 超低遅延が要る箇所だけ **WebRTC** という住み分けを、数値で裏付けるのが目的。

---

## ディレクトリ構成

```
Yubi-tmp/
├── server/                       # Go製ベンチサーバー（REST/WS/WebRTCを1本で提供）
│   ├── main.go                   # ルーティング・起動
│   ├── message.go                # 共通Envelope・サーバー処理の擬似再現
│   ├── rest.go                   # POST /api/echo
│   ├── ws.go                     # GET  /ws
│   ├── webrtc.go                 # POST /rtc/offer（pion、簡易シグナリング）
│   ├── Dockerfile
│   └── README.md
├── render.yaml                   # Renderデプロイ用Blueprint
└── My project/                   # Unityプロジェクト（6000.0.57f1）
    └── Assets/Scripts/Benchmark/
        ├── BenchmarkRunner.cs    # 計測本体（画面表示・スコア・CSV/JSON出力）
        ├── RestTransport.cs
        ├── WebSocketTransport.cs
        ├── WebRtcTransport.cs     # #if YUBI_WEBRTC でガード
        ├── MessageEnvelope.cs
        ├── LatencyStats.cs
        ├── ScoreCalculator.cs
        └── IBenchTransport.cs
```

---

## 計測シナリオ

| シナリオ | type | 想定 | サーバー処理 |
|---|---|---|---|
| ping | `echo` | 純粋なRTT計測 | なし |
| move | `player.move` | ゲーム中の位置同期（連続送信） | なし（中継のみ） |
| kick | `ball.kick` | サーバー権威でのボール物理 | 弾道計算60ステップ |
| goal | `goal.check` | サーバー権威でのゴール判定 | 領域判定 |

各シナリオを N 回ピンポンし、`min / avg / p50 / p95 / max / jitter` と
サーバー内処理時間（`serverSend - serverRecv`）を集計する。

RTT はクライアントの単一時計（Stopwatch）で計測するため、サーバーとの時計ズレの影響を受けない。
`netRTT ≈ clientRTT − serverProc` で純ネットワーク往復を分離できる。

---

## 1. サーバーを動かす

```bash
cd server
go run .
# => yubi-bench-server listening on :8080
```

動作確認:

```bash
curl http://localhost:8080/health
curl -X POST http://localhost:8080/api/echo \
  -H "Content-Type: application/json" \
  -d '{"type":"echo","seq":1,"timestamp":0,"payload":{}}'
```

---

## 2. Unityで計測する

1. `My project` を Unity 6000.0.57f1 で開く
2. 空の GameObject を作り `BenchmarkRunner` をアタッチ
3. Inspector で `serverBaseUrl` を設定
   - ローカル: `http://localhost:8080`
   - Render: `https://<your-app>.onrender.com`
4. Play を押し、画面の **Run Benchmark** ボタンを押す
5. 結果が画面に表示され、`Application.persistentDataPath` に
   `yubi_bench_result.csv` / `.json` が出力される

### WebRTC を有効にする場合（任意・簡易）

1. Package Manager → Add package by name → `com.unity.webrtc`
2. Project Settings → Player → Scripting Define Symbols に `YUBI_WEBRTC` を追加
3. BenchmarkRunner の `enableWebRtc` を ON

> WebRTC はローカル(localhost)では計測可能。Render の通常Web ServiceはUDPが通らないため、
> リモートのWebRTC計測には別途 TURN/UDP対応ホストが要る（後述）。

---

## 3. Renderへデプロイ（デプロイ可能な状態まで用意済み）

このリポジトリには `render.yaml` と `server/Dockerfile` が入っているので、
GitHubに push 後、Renderダッシュボードで Blueprint として読み込むだけでデプロイできる。

```bash
# リポジトリ初期化（まだの場合）
cd /Users/ryu/Yubi-tmp
git init && git add -A && git commit -m "init: yubi bench"
# GitHubにpush（リポジトリは適宜作成）
# その後 Render: New → Blueprint → このリポジトリ → 自動で yubi-bench-server が作られる
```

デプロイ後、Unity の `serverBaseUrl` を `https://<app>.onrender.com` に変えて計測する。
（iOS実機計測時は HTTPS/WSS のこのURLを使う。ATS的にも問題なし）

### WebRTCのリモート計測について（制約）

Render の標準Web ServiceはHTTP/TCP（REST・WebSocket）はそのまま通るが、
WebRTCはUDP/ICEを使うため標準環境ではDataChannelが張れない可能性が高い。
→ **REST/WebSocketはRenderでリモート計測可能。WebRTCはローカル計測 or UDP対応VPS+TURNが必要。**

---

## 4. 結果の読み方

- **総合スコア**: 各項目（レイテンシ40% / ジッター25% / 接続15% / 成功率20%）を
  「最良の方式を満点」とした相対評価で 0-100 点化。
- **シナリオ別推奨**: p95 RTT が最小の方式を「その場面の推奨」として表示。
- 一般的な予想:
  - `ping` / `move`: WebSocket・WebRTC が REST より明確に速い（接続維持の差）
  - `kick` / `goal`: サーバー処理時間が乗るので方式差は縮む
  - 接続確立: REST(=warm-up) < WebSocket < WebRTC（ICEの分WebRTCが重い）

---

## iOSビルドについて

このプロジェクトは iOS ビルド対応設定済み（Player Settings）。
ただし実機ビルド・署名には Apple Developer アカウントと Xcode 署名操作が必要なため、
本リポジトリでは **Editor Play モードでの計測** を前提にしている。
実機計測する場合は通常のUnity iOSビルド手順（Build → Xcode → 署名 → 実機）で実行する。
