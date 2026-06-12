package main

import (
	"encoding/json"
	"log"
	"math"
	"net/http"
	"time"

	"github.com/gorilla/websocket"
)

// upgrader は HTTP接続を WebSocket に昇格させる。
var upgrader = websocket.Upgrader{
	ReadBufferSize:  4096,
	WriteBufferSize: 4096,
	// テスト用途なので全オリジンを許可。本番は適切に絞ること。
	CheckOrigin: func(r *http.Request) bool { return true },
}

// streamState は fire-and-forget 計測（stream.move 連続送信）の接続ごとの集計。
//
// 実ゲームの位置同期は「返事を待たずに20-30fpsで送り続ける」ので、
// ping-pong 型の RTT とは別に、連続送信時の
//   - 取りこぼし（受信数 vs クライアント申告の送信数）
//   - 到着間隔の揺れ（ジッター）= TCP詰まりの間接観測
// を計測する。
type streamState struct {
	count        int64
	lastArrival  time.Time
	intervals    []float64 // 到着間隔(ms)
	maxSeq       int64
}

// handleWS は WebSocket 方式のエンドポイント。
// 通常メッセージは process して同じ接続で返す（ping-pong）。
// stream.move は返事をせず集計だけ行い、stream.end で集計結果を1回返す。
func handleWS(w http.ResponseWriter, r *http.Request) {
	conn, err := upgrader.Upgrade(w, r, nil)
	if err != nil {
		log.Printf("[ws] upgrade error: %v", err)
		return
	}
	defer conn.Close()

	statActiveWS.Add(1)
	defer statActiveWS.Add(-1)

	var stream *streamState

	for {
		mt, data, err := conn.ReadMessage()
		if err != nil {
			if websocket.IsUnexpectedCloseError(err, websocket.CloseGoingAway, websocket.CloseNormalClosure) {
				// 切断ログはCCUテストで大量に出るので抑制
			}
			break
		}
		if mt != websocket.TextMessage {
			continue
		}

		var env Envelope
		if err := json.Unmarshal(data, &env); err != nil {
			continue
		}

		switch env.Type {
		case "stream.start":
			stream = &streamState{}
			// 開始ACKを1回だけ返す
			out := process(&env)
			outBytes, _ := json.Marshal(out)
			_ = conn.WriteMessage(websocket.TextMessage, outBytes)

		case "stream.move":
			// fire-and-forget: 返事しない。集計のみ。
			statMsgProcessed.Add(1)
			if stream != nil {
				now := time.Now()
				if !stream.lastArrival.IsZero() {
					stream.intervals = append(stream.intervals,
						float64(now.Sub(stream.lastArrival).Microseconds())/1000.0)
				}
				stream.lastArrival = now
				stream.count++
				if env.Seq > stream.maxSeq {
					stream.maxSeq = env.Seq
				}
			}

		case "stream.end":
			// 集計結果を返す
			result := map[string]interface{}{}
			if stream != nil {
				result["received"] = stream.count
				result["maxSeq"] = stream.maxSeq
				result["lost"] = stream.maxSeq + 1 - stream.count // seqは0始まり想定
				avg, jit, max := intervalStats(stream.intervals)
				result["avgIntervalMs"] = round2(avg)
				result["jitterMs"] = round2(jit)
				result["maxIntervalMs"] = round2(max)
			}
			env.ServerRecv = nowMicro()
			env.ServerSend = env.ServerRecv
			env.Payload, _ = json.Marshal(result)
			outBytes, _ := json.Marshal(&env)
			_ = conn.WriteMessage(websocket.TextMessage, outBytes)
			stream = nil

		default:
			// 通常の ping-pong
			out := process(&env)
			outBytes, _ := json.Marshal(out)
			if err := conn.WriteMessage(websocket.TextMessage, outBytes); err != nil {
				return
			}
		}
	}
}

// intervalStats は到着間隔の平均・標準偏差(ジッター)・最大を返す。
func intervalStats(xs []float64) (avg, jitter, max float64) {
	if len(xs) == 0 {
		return 0, 0, 0
	}
	var sum float64
	for _, x := range xs {
		sum += x
		if x > max {
			max = x
		}
	}
	avg = sum / float64(len(xs))
	var sq float64
	for _, x := range xs {
		sq += (x - avg) * (x - avg)
	}
	jitter = math.Sqrt(sq / float64(len(xs)))
	return avg, jitter, max
}

func round2(v float64) float64 {
	return math.Round(v*100) / 100
}
