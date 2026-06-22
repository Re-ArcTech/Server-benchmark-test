// synclab-bot - 同期ラボの「2人目のプレイヤー」をするボット。
//
//	go run ./cmd/synclab-bot            # localhost:8090 に繋ぐ
//	go run ./cmd/synclab-bot -url ws://host:8090/synclab
//
// フィールドをうろつき、ボールに近づくと蹴る。これで Unity 1エディタだけでも
// 「他プレイヤーが見える・ボールの所有権が奪い合いになる」を試せる。
package main

import (
	"encoding/json"
	"flag"
	"math"
	"math/rand"
	"time"

	"github.com/gorilla/websocket"
)

type v3 struct {
	X float64 `json:"x"`
	Y float64 `json:"y"`
	Z float64 `json:"z"`
}

func main() {
	url := flag.String("url", "ws://localhost:8090/synclab", "synclab URL")
	flag.Parse()

	c, _, err := websocket.DefaultDialer.Dial(*url, nil)
	if err != nil {
		panic(err)
	}
	defer c.Close()

	// 最新ボール位置を受信
	var ballPos v3
	go func() {
		for {
			_, data, err := c.ReadMessage()
			if err != nil {
				return
			}
			var m struct {
				Type string `json:"type"`
				Pos  v3     `json:"pos"`
			}
			if json.Unmarshal(data, &m) == nil && m.Type == "ball.state" {
				ballPos = m.Pos
			}
		}
	}()

	// 20fpsでうろつき＋ボールに近ければ蹴る
	pos := v3{X: -3, Z: -3}
	target := randTarget()
	tick := time.NewTicker(time.Second / 20)
	defer tick.Stop()
	var seq int64

	for range tick.C {
		// ターゲットへ向かう（近づいたら次のターゲット）
		dx, dz := target.X-pos.X, target.Z-pos.Z
		d := math.Hypot(dx, dz)
		if d < 0.5 {
			target = randTarget()
		} else {
			sp := 5.0 * (1.0 / 20.0)
			pos.X += dx / d * sp
			pos.Z += dz / d * sp
		}

		_ = c.WriteJSON(map[string]any{
			"type": "move", "seq": seq, "pos": pos,
			"rotY": math.Atan2(dx, dz) * 180 / math.Pi,
			"vel":  v3{X: dx, Z: dz},
		})
		seq++

		// ボールが近ければ蹴る（所有権を奪いに行く）
		if math.Hypot(ballPos.X-pos.X, ballPos.Z-pos.Z) < 2.3 {
			_ = c.WriteJSON(map[string]any{"type": "kick", "pos": pos})
		}
	}
}

func randTarget() v3 {
	return v3{X: rand.Float64()*16 - 8, Z: rand.Float64()*16 - 8}
}
