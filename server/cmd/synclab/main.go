// synclab - 位置・ボール同期の手法検証用サーバー（ハブ型）
//
// 複数クライアントが /synclab (:8090) に繋ぐと、全員が同じ部屋・同じボールを共有する。
//   - 各プレイヤーの位置を他クライアントへ中継（player.state）
//   - ボールは共有1個。サーバーが物理を回し、所有権(最後に蹴った人)を裁定（ball.state.owner）
//   - キックは「ボールに近ければ」サーバーが受理 → 所有権がその人に移る
//   - 送信には各クライアント個別の 遅延/ジッター/ロス を注入できる（デバッグ用）
//
// これで「サーバーが複数クライアントを仲介・裁定する」本来の役割が出る。
package main

import (
	"encoding/json"
	"fmt"
	"log"
	"math"
	"math/rand"
	"net/http"
	"os"
	"sync"
	"sync/atomic"
	"time"

	"github.com/gorilla/websocket"
)

type vec3 struct {
	X float64 `json:"x"`
	Y float64 `json:"y"`
	Z float64 `json:"z"`
}

type inMsg struct {
	Type       string  `json:"type"`
	LatencyMs  float64 `json:"latencyMs"`
	JitterMs   float64 `json:"jitterMs"`
	LossRate   float64 `json:"lossRate"`
	SendRateHz float64 `json:"sendRateHz"`
	Seq        int64   `json:"seq"`
	Pos        vec3    `json:"pos"`
	RotY       float64 `json:"rotY"`
	Vel        vec3    `json:"vel"`
}

const (
	fieldHalf = 10.0
	kickRange = 2.5
	kickPower = 12.0
)

var upgrader = websocket.Upgrader{CheckOrigin: func(r *http.Request) bool { return true }}
var idSeq atomic.Int64

type outItem struct {
	sendAt time.Time
	data   []byte
}

// client は1接続。送信は遅延キュー付きの1goroutineで行う。
type client struct {
	id   string
	conn *websocket.Conn

	mu        sync.Mutex
	latencyMs float64
	jitterMs  float64
	lossRate  float64

	pos    vec3
	rotY   float64
	vel    vec3
	sendCh chan outItem
}

func (c *client) sendRaw(data []byte) {
	c.mu.Lock()
	lat, jit, loss := c.latencyMs, c.jitterMs, c.lossRate
	c.mu.Unlock()
	if loss > 0 && rand.Float64() < loss {
		return
	}
	extra := lat
	if jit > 0 {
		extra += rand.Float64() * jit
	}
	select {
	case c.sendCh <- outItem{sendAt: time.Now().Add(time.Duration(extra) * time.Millisecond), data: data}:
	default:
	}
}

func (c *client) send(v any) {
	if data, err := json.Marshal(v); err == nil {
		c.sendRaw(data)
	}
}

func (c *client) writer(done chan struct{}) {
	for {
		select {
		case <-done:
			return
		case it := <-c.sendCh:
			if d := time.Until(it.sendAt); d > 0 {
				select {
				case <-done:
					return
				case <-time.After(d):
				}
			}
			if c.conn.WriteMessage(websocket.TextMessage, it.data) != nil {
				return
			}
		}
	}
}

// hub は全クライアントと共有ボールを束ねる。
type hub struct {
	mu       sync.Mutex
	clients  map[string]*client
	ballPos  vec3
	ballVel  vec3
	ballOwner string
	start    time.Time
}

func newHub() *hub {
	return &hub{
		clients: make(map[string]*client),
		ballPos: vec3{X: 3, Z: 3},
		ballVel: vec3{X: 4, Z: 2},
		start:   time.Now(),
	}
}

func (h *hub) add(c *client) {
	h.mu.Lock()
	h.clients[c.id] = c
	n := len(h.clients)
	h.mu.Unlock()
	log.Printf("[synclab] %s connected (clients=%d)", c.id, n)
}

func (h *hub) remove(c *client) {
	h.mu.Lock()
	delete(h.clients, c.id)
	n := len(h.clients)
	h.mu.Unlock()
	// 退出を全員に通知
	h.broadcast(map[string]any{"type": "player.left", "id": c.id}, "")
	log.Printf("[synclab] %s disconnected (clients=%d)", c.id, n)
}

// broadcast は1回marshalして、except以外の全員へ（各自の遅延で）送る。
func (h *hub) broadcast(v any, exceptID string) {
	data, err := json.Marshal(v)
	if err != nil {
		return
	}
	h.mu.Lock()
	targets := make([]*client, 0, len(h.clients))
	for id, c := range h.clients {
		if id != exceptID {
			targets = append(targets, c)
		}
	}
	h.mu.Unlock()
	for _, c := range targets {
		c.sendRaw(data)
	}
}

func (h *hub) ballLoop(done chan struct{}) {
	tick := time.NewTicker(time.Second / 60)
	defer tick.Stop()
	last := time.Now()
	var since float64
	for {
		select {
		case <-done:
			return
		case <-tick.C:
		}
		now := time.Now()
		dt := now.Sub(last).Seconds()
		last = now

		h.mu.Lock()
		h.ballPos.X += h.ballVel.X * dt
		h.ballPos.Z += h.ballVel.Z * dt
		decay := math.Pow(0.7, dt)
		h.ballVel.X *= decay
		h.ballVel.Z *= decay
		bounce(&h.ballPos.X, &h.ballVel.X)
		bounce(&h.ballPos.Z, &h.ballVel.Z)
		pos, vel, owner := h.ballPos, h.ballVel, h.ballOwner
		h.mu.Unlock()

		since += dt
		if since >= 1.0/20.0 { // ボールは20Hzで配信
			since = 0
			h.broadcast(map[string]any{
				"type": "ball.state", "t": time.Since(h.start).Milliseconds(),
				"pos": pos, "vel": vel, "owner": owner,
			}, "")
		}
	}
}

func bounce(p, v *float64) {
	if *p > fieldHalf {
		*p = fieldHalf
		*v = -*v
	} else if *p < -fieldHalf {
		*p = -fieldHalf
		*v = -*v
	}
}

func (h *hub) handle(c *client, m *inMsg) {
	switch m.Type {
	case "config":
		c.mu.Lock()
		c.latencyMs, c.jitterMs, c.lossRate = m.LatencyMs, m.JitterMs, m.LossRate
		c.mu.Unlock()

	case "move":
		c.mu.Lock()
		c.pos, c.rotY, c.vel = m.Pos, m.RotY, m.Vel
		c.mu.Unlock()
		// 他の全員へ中継（補間用に t も付ける）
		h.broadcast(map[string]any{
			"type": "player.state", "id": c.id, "t": time.Since(h.start).Milliseconds(),
			"pos": m.Pos, "rotY": m.RotY, "vel": m.Vel,
		}, c.id)

	case "kick":
		h.mu.Lock()
		dx := h.ballPos.X - m.Pos.X
		dz := h.ballPos.Z - m.Pos.Z
		d := math.Hypot(dx, dz)
		took := false
		if d < kickRange { // 近ければ蹴れる＝所有権を獲得（サーバーが裁定）
			if d < 0.001 {
				d = 0.001
			}
			h.ballVel.X = dx / d * kickPower
			h.ballVel.Z = dz / d * kickPower
			h.ballOwner = c.id
			took = true
		}
		h.mu.Unlock()
		if took {
			log.Printf("[synclab] %s kicked the ball (owner -> %s)", c.id, c.id)
		}
	}
}

func main() {
	h := newHub()
	done := make(chan struct{})
	go h.ballLoop(done)

	mux := http.NewServeMux()
	mux.HandleFunc("/health", func(w http.ResponseWriter, r *http.Request) {
		w.Write([]byte("synclab ok"))
	})
	mux.HandleFunc("/synclab", func(w http.ResponseWriter, r *http.Request) {
		conn, err := upgrader.Upgrade(w, r, nil)
		if err != nil {
			return
		}
		defer conn.Close()

		c := &client{
			id:     fmt.Sprintf("p%d", idSeq.Add(1)),
			conn:   conn,
			sendCh: make(chan outItem, 256),
		}
		cdone := make(chan struct{})
		go c.writer(cdone)
		h.add(c)
		c.send(map[string]any{"type": "welcome", "id": c.id})
		defer func() {
			close(cdone)
			h.remove(c)
		}()

		for {
			_, data, err := conn.ReadMessage()
			if err != nil {
				return
			}
			var m inMsg
			if json.Unmarshal(data, &m) != nil {
				continue
			}
			h.handle(c, &m)
		}
	})

	port := os.Getenv("PORT")
	if port == "" {
		port = "8090"
	}
	log.Printf("synclab (hub) listening on :%s", port)
	log.Fatal(http.ListenAndServe(":"+port, mux))
}
