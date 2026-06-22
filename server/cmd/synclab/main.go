// synclab - 位置同期の手法検証用リレーサーバー
//
// /synclab (WebSocket, :8090) に1クライアントが繋ぐと:
//   - BOTが8の字に動き、その位置を sendRateHz で配信（=観測用の「他者」）
//   - 自キャラは権威モード別に扱う（client=中継 / server=サーバー計算 / hybrid=訂正）
//   - すべての送信に latencyMs の人工遅延を乗せる（手法の差を出すため）
// 設定(config)はデバッグUIから動的に変更できる。
package main

import (
	"encoding/json"
	"log"
	"math"
	"math/rand"
	"net/http"
	"os"
	"sync"
	"time"

	"github.com/gorilla/websocket"
)

type vec3 struct {
	X float64 `json:"x"`
	Y float64 `json:"y"`
	Z float64 `json:"z"`
}

// 受信メッセージ（type で分岐）
type inMsg struct {
	Type string `json:"type"`
	// config
	Authority  string  `json:"authority"`
	SendRateHz float64 `json:"sendRateHz"`
	LatencyMs  float64 `json:"latencyMs"`
	JitterMs   float64 `json:"jitterMs"`
	LossRate   float64 `json:"lossRate"`
	// move / input
	Seq  int64   `json:"seq"`
	Pos  vec3    `json:"pos"`
	RotY float64 `json:"rotY"`
	Vel  vec3    `json:"vel"`
	Dir  vec3    `json:"dir"`
	Dt   float64 `json:"dt"`
}

var upgrader = websocket.Upgrader{CheckOrigin: func(r *http.Request) bool { return true }}

const (
	fieldHalf  = 10.0 // フィールドは [-10,10]
	playerSpd  = 6.0  // サーバー権威時の移動速度
	corrThresh = 1.5  // ハイブリッド: このズレを超えたら訂正
	kickRange  = 2.5  // この距離以内ならボールを蹴れる
	kickPower  = 12.0 // キックの初速
)

type session struct {
	conn *websocket.Conn

	mu         sync.Mutex
	authority  string
	sendRateHz float64
	latencyMs  float64
	jitterMs   float64
	lossRate   float64

	// サーバー権威用のプレイヤー状態
	authPos vec3
	// ハイブリッド用の直近正当位置
	lastValidPos vec3

	// ボール（サーバーが簡易物理を回す＝観測対象）
	ballPos vec3
	ballVel vec3

	sendCh chan outItem
	start  time.Time
}

type outItem struct {
	sendAt time.Time
	data   []byte
}

func main() {
	mux := http.NewServeMux()
	mux.HandleFunc("/health", func(w http.ResponseWriter, r *http.Request) {
		w.Write([]byte("synclab ok"))
	})
	mux.HandleFunc("/synclab", handleSync)

	port := os.Getenv("PORT")
	if port == "" {
		port = "8090"
	}
	log.Printf("synclab listening on :%s", port)
	log.Fatal(http.ListenAndServe(":"+port, mux))
}

func handleSync(w http.ResponseWriter, r *http.Request) {
	conn, err := upgrader.Upgrade(w, r, nil)
	if err != nil {
		return
	}
	defer conn.Close()

	s := &session{
		conn:       conn,
		authority:  "client",
		sendRateHz: 20,
		latencyMs:  0,
		sendCh:     make(chan outItem, 256),
		start:      time.Now(),
	}
	log.Printf("[synclab] client connected: %s", r.RemoteAddr)

	// ボール初期状態（動いてる方が外挿/補間の差が見える）
	s.ballPos = vec3{X: 3, Z: 3}
	s.ballVel = vec3{X: 4, Z: 2}

	done := make(chan struct{})
	go s.writer(done)    // 遅延キュー付きの送信ループ（順序＋スレッド安全）
	go s.ballLoop(done)  // ボール物理＋配信

	// 受信ループ
	for {
		_, data, err := conn.ReadMessage()
		if err != nil {
			break
		}
		var m inMsg
		if json.Unmarshal(data, &m) != nil {
			continue
		}
		s.handle(&m)
	}
	close(done)
	log.Printf("[synclab] client disconnected")
}

// writer は sendCh から取り出し、各メッセージの sendAt まで待ってから書く。
// latency が一定なら順序は保たれる。書き込みは1goroutineに限定（gorillaの制約）。
func (s *session) writer(done chan struct{}) {
	for {
		select {
		case <-done:
			return
		case it := <-s.sendCh:
			d := time.Until(it.sendAt)
			if d > 0 {
				select {
				case <-done:
					return
				case <-time.After(d):
				}
			}
			if err := s.conn.WriteMessage(websocket.TextMessage, it.data); err != nil {
				return
			}
		}
	}
}

// send は latency を乗せてキューに入れる。
func (s *session) send(v any) {
	data, err := json.Marshal(v)
	if err != nil {
		return
	}
	s.mu.Lock()
	lat := s.latencyMs
	jit := s.jitterMs
	loss := s.lossRate
	s.mu.Unlock()

	if loss > 0 && rand.Float64() < loss {
		return // パケットロス: 送らない
	}
	extra := lat
	if jit > 0 {
		extra += rand.Float64() * jit // ジッター: 0〜jit の追加遅延（到着順が乱れることもある＝実回線的）
	}
	select {
	case s.sendCh <- outItem{sendAt: time.Now().Add(time.Duration(extra) * time.Millisecond), data: data}:
	default: // バッファ溢れは捨てる（過負荷時）
	}
}

// ballLoop はボールの簡易物理を60Hzで回し、sendRateHz でボール状態を配信する。
func (s *session) ballLoop(done chan struct{}) {
	tick := time.NewTicker(time.Second / 60)
	defer tick.Stop()
	last := time.Now()
	var sinceBroadcast float64

	for {
		select {
		case <-done:
			return
		case <-tick.C:
		}
		now := time.Now()
		dt := now.Sub(last).Seconds()
		last = now

		s.mu.Lock()
		// 等速移動＋摩擦＋壁で反射
		s.ballPos.X += s.ballVel.X * dt
		s.ballPos.Z += s.ballVel.Z * dt
		decay := math.Pow(0.7, dt) // 1秒で30%減速
		s.ballVel.X *= decay
		s.ballVel.Z *= decay
		if s.ballPos.X > fieldHalf {
			s.ballPos.X = fieldHalf
			s.ballVel.X = -s.ballVel.X
		} else if s.ballPos.X < -fieldHalf {
			s.ballPos.X = -fieldHalf
			s.ballVel.X = -s.ballVel.X
		}
		if s.ballPos.Z > fieldHalf {
			s.ballPos.Z = fieldHalf
			s.ballVel.Z = -s.ballVel.Z
		} else if s.ballPos.Z < -fieldHalf {
			s.ballPos.Z = -fieldHalf
			s.ballVel.Z = -s.ballVel.Z
		}
		hz := s.sendRateHz
		pos := s.ballPos
		vel := s.ballVel
		s.mu.Unlock()

		if hz <= 0 {
			hz = 20
		}
		sinceBroadcast += dt
		if sinceBroadcast >= 1.0/hz {
			sinceBroadcast = 0
			s.send(map[string]any{
				"type": "ball.state",
				"t":    time.Since(s.start).Milliseconds(),
				"pos":  pos, "vel": vel,
			})
		}
	}
}

func (s *session) handle(m *inMsg) {
	switch m.Type {
	case "config":
		s.mu.Lock()
		if m.Authority != "" {
			s.authority = m.Authority
		}
		if m.SendRateHz > 0 {
			s.sendRateHz = m.SendRateHz
		}
		s.latencyMs = m.LatencyMs
		s.jitterMs = m.JitterMs
		s.lossRate = m.LossRate
		s.mu.Unlock()
		log.Printf("[synclab] config: authority=%s rate=%.0f latency=%.0f jitter=%.0f loss=%.2f",
			m.Authority, m.SendRateHz, m.LatencyMs, m.JitterMs, m.LossRate)

	case "move": // クライアント/ハイブリッド権威
		s.mu.Lock()
		auth := s.authority
		s.mu.Unlock()
		if auth == "hybrid" {
			s.handleHybridMove(m)
		} else { // client
			// 「相手から見えるあなた」を描くため、受信時刻tと速度velも返す（補間用）
			s.send(map[string]any{
				"type": "self.echo", "seq": m.Seq,
				"t": time.Since(s.start).Milliseconds(), "pos": m.Pos, "vel": m.Vel,
			})
		}

	case "input": // サーバー権威
		s.handleServerInput(m)

	case "kick": // ボールを蹴る（プレイヤー位置がボールに近ければ）
		s.mu.Lock()
		dx := s.ballPos.X - m.Pos.X
		dz := s.ballPos.Z - m.Pos.Z
		d := math.Hypot(dx, dz)
		if d < kickRange {
			if d < 0.001 {
				d = 0.001
			}
			s.ballVel.X = dx / d * kickPower
			s.ballVel.Z = dz / d * kickPower
		}
		s.mu.Unlock()
	}
}

// handleServerInput はサーバー側でプレイヤーを動かして権威位置を返す。
func (s *session) handleServerInput(m *inMsg) {
	dt := m.Dt
	if dt <= 0 || dt > 0.1 {
		dt = 1.0 / 60
	}
	// 入力方向を正規化して等速移動
	dx, dz := m.Dir.X, m.Dir.Z
	l := math.Hypot(dx, dz)
	s.mu.Lock()
	if l > 0.001 {
		s.authPos.X += (dx / l) * playerSpd * dt
		s.authPos.Z += (dz / l) * playerSpd * dt
	}
	s.authPos = clampField(s.authPos)
	pos := s.authPos
	s.mu.Unlock()

	vel := vec3{X: dx, Z: dz}
	s.send(map[string]any{"type": "self.auth", "seq": m.Seq, "pos": pos, "vel": vel})
}

// handleHybridMove はクライアントの予測位置を検証し、ズレていれば訂正を返す。
func (s *session) handleHybridMove(m *inMsg) {
	s.mu.Lock()
	clamped := clampField(m.Pos)
	// フィールド外 or 前回正当位置から飛びすぎ → 訂正
	jump := dist(m.Pos, s.lastValidPos)
	needCorrection := clamped != m.Pos || jump > corrThresh
	if !needCorrection {
		s.lastValidPos = m.Pos
	} else {
		s.lastValidPos = clamped
	}
	pos := s.lastValidPos
	s.mu.Unlock()

	if needCorrection {
		s.send(map[string]any{"type": "self.correction", "ackSeq": m.Seq, "pos": pos, "vel": m.Vel})
	}
}

func clampField(p vec3) vec3 {
	if p.X > fieldHalf {
		p.X = fieldHalf
	} else if p.X < -fieldHalf {
		p.X = -fieldHalf
	}
	if p.Z > fieldHalf {
		p.Z = fieldHalf
	} else if p.Z < -fieldHalf {
		p.Z = -fieldHalf
	}
	return p
}

func dist(a, b vec3) float64 {
	return math.Sqrt((a.X-b.X)*(a.X-b.X) + (a.Z-b.Z)*(a.Z-b.Z))
}
