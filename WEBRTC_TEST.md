# WebRTC ロス耐性テスト 手順書

## このテストの目的（一言で）

良い回線では WebSocket で十分。でも**スマホの悪い電波（パケットロス）では TCP/WebSocket が「先頭詰まり(HOLブロッキング)」で詰まる**。
それを **UDPベースの WebRTC（unreliableモード）が回避できるか**を、わざと悪い回線を作って確かめる。

- **ロスあり**で `WebSocket は p99 が跳ねる / WebRTC は跳ねない` が見えたら → WebRTCに価値あり
- 差が出なければ → WebSocketで十分（WebRTCの複雑さは不要）

> Renderは UDP を通さないので、このテストだけは **生IPのVPS** が要る。
> サーバー側(公開IP対応)・Unity側(unreliableモード)のコードは準備済み。

---

## 準備するもの

1. UDP が使えるVPS（無料なら **Oracle Cloud Always Free / AWS無料枠**、楽さなら **ConoHa時間課金**）
2. そのVPSの**公開IPアドレス**
3. ロス注入手段（**iPhoneのNetwork Link Conditioner** か、Macの`dnctl`）

---

## STEP 1: VPSにサーバーを立てる

```bash
# VPSにSSHログイン後
sudo apt update && sudo apt install -y git golang   # Go未導入なら
git clone https://github.com/Re-ArcTech/Server-benchmark-test.git
cd Server-benchmark-test/server
go build -buildvcs=false -o yubi-bench-server .

# 公開IPを教えて起動（★PUBLIC_IPが超重要）
PUBLIC_IP=<VPSの公開IP> PORT=8080 ./yubi-bench-server
```

### ファイアウォール開放
TCP 8080（REST/WS/シグナリング）と UDP 50000-50100（WebRTCの実データ）を開ける。

```bash
# ufw の例
sudo ufw allow 8080/tcp
sudo ufw allow 50000:50100/udp
```
※クラウドの場合、VPS管理画面の「セキュリティグループ/ファイアウォール」でも同じポートを開ける必要あり（Oracle/AWSは特に）。

### 疎通確認（手元のMacから）
```bash
curl http://<VPSの公開IP>:8080/health   # → ok v2 が返ればOK
```

---

## STEP 2: Unityで WebRTC を有効化

```
1. Package Manager > + > Add package by name > com.unity.webrtc
2. Project Settings > Player > Scripting Define Symbols に  YUBI_WEBRTC  を追加
3. BenchScene を開く → URL欄を  http://<VPSの公開IP>:8080  に
   WebRTCトグルを ON（REST/WSもONのままでOK、3方式比較になる）
```

> WebRtcTransport は既に **unreliable（ordered=false, maxRetransmits=0）** に設定済み。
> これが「投げっぱなしUDP」の核。ここが reliable だとTCPと同じ挙動になり差が出ない。

---

## STEP 3: まず「ロスなし」で基準を取る

ロス注入せずに計測スタート。
→ この時点では **WS ≒ WebRTC** になるはず（良回線では差が出ない）。これが基準線。

---

## STEP 4: ロスを注入して再計測

### 方法A: iPhone内蔵（推奨・sudo不要・実機テスト）
```
設定 > デベロッパ > Network Link Conditioner > ON
プロファイル: 「100% Loss」ではなく、まず「3G」や「Very Bad Network」、
            または Profiles を追加して Packet Loss 5〜10% を設定
```
※「デベロッパ」メニューは、一度Xcodeから実機にビルドすると出てくる（A手順参照）。

### 方法B: Macで注入（Editor計測用・sudo要）
```bash
# 80ms遅延 + 8%ロスのパイプを作る
sudo dnctl pipe 1 config delay 80 plr 0.08
# VPS宛のtcp/udpをそのパイプに通す
echo 'dummynet out proto { tcp udp } from any to <VPSの公開IP> pipe 1' | sudo pfctl -f -
sudo pfctl -E

# 計測が終わったら解除
sudo pfctl -d ; sudo dnctl -q flush
```

ロスを入れた状態で**もう一度計測スタート**。

---

## STEP 5: 結果を比較する

ロスなし → ロスありで、各方式の **p95 / p99 / max** を比べる。

```
              ロスなし        ロスあり
WebSocket :   p99 低い   →   p99 ドカンと跳ねる（例 200ms超）← TCPの先頭詰まり
WebRTC    :   p99 低い   →   p99 あまり跳ねない・滑らか     ← UDPは詰まらない
```

**この差が見えたら実証成功。** ロス回線でのWebRTCの価値が数字で確認できる。
（ローカルの `loadtest stall` で出た「TCPのp99が229msに跳躍」と同じ現象を、実回線+UDPで再現するイメージ）

---

## トラブルシュート

| 症状 | 原因 / 対処 |
|---|---|
| WebRTCがconnectで失敗 | `PUBLIC_IP`未設定 / UDPポート未開放。STEP1のファイアウォールを確認 |
| connectは成功するがメッセージが来ない | UDP 50000-50100 がクラウド側セキュリティグループで閉じている |
| ロスを入れてもWebRTCも詰まる | unreliable設定が効いていない。`com.unity.webrtc`のバージョンとDataChannelInitを確認 |
| `curl http://IP:8080/health` が無反応 | TCP 8080 が閉じている / サーバー未起動 |

---

## 環境変数まとめ（サーバー）

| 変数 | 既定 | 用途 |
|---|---|---|
| `PORT` | 8080 | HTTP(REST/WS/シグナリング)ポート |
| `PUBLIC_IP` | (なし) | ★VPSの公開IP。WebRTCに必須 |
| `UDP_PORT_MIN` | 50000 | WebRTC UDPポート範囲下限 |
| `UDP_PORT_MAX` | 50100 | WebRTC UDPポート範囲上限 |
