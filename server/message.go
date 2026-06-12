package main

import (
	"encoding/json"
	"math"
	"time"
)

// Envelope は全トランスポート（REST / WebSocket / WebRTC）共通のメッセージ形式。
// ゆびサッカー Ver.3.0 で想定しているプロトコルの簡易版で、
// ベンチマークではこれに送受信のタイムスタンプを載せて計測する。
type Envelope struct {
	Type       string          `json:"type"`       // 例: "echo" / "player.move" / "ball.kick" / "goal.check"
	Seq        int64           `json:"seq"`        // クライアントが採番する連番
	Timestamp  int64           `json:"timestamp"`  // クライアント送信時刻（ミリ秒・クライアント時計）。サーバーは触らない
	ServerRecv int64           `json:"serverRecv"` // サーバー受信時刻（マイクロ秒・サーバー時計）
	ServerSend int64           `json:"serverSend"` // サーバー送信直前時刻（マイクロ秒・サーバー時計）
	Payload    json.RawMessage `json:"payload,omitempty"`
}

// Vec3 は3次元ベクトル。
type Vec3 struct {
	X float64 `json:"x"`
	Y float64 `json:"y"`
	Z float64 `json:"z"`
}

// nowMicro はサーバー時計のマイクロ秒。サーバー内の処理時間計測に使う。
func nowMicro() int64 {
	return time.Now().UnixMicro()
}

// process は受信したメッセージにサーバー側のタイムスタンプを刻み、
// type に応じて「サーバー権威でやるならこれくらいの処理が走る」を擬似的に再現する。
//
// 重要: RTT はクライアント側の単一時計で計測する（送信時刻と受信時刻を両方クライアントで取る）。
// サーバーが返す ServerRecv / ServerSend は「サーバー内の処理時間」を出すためだけに使い、
// netRTT = clientRTT - serverProc で純粋なネットワーク往復を分離できるようにする。
func process(in *Envelope) *Envelope {
	in.ServerRecv = nowMicro()
	statMsgProcessed.Add(1)

	switch in.Type {
	case "echo", "player.move", "echo.size":
		// 純粋なリレー（中継）。サーバーはほぼ何もしない。
		// player.move は「他プレイヤーへ転送するだけ」のクライアント権威モデルを想定。
		// echo.size はクライアント指定サイズのpayloadをそのまま返す（上り+下り帯域計測）。

	case "ball.kick":
		// サーバー権威でボール物理を計算する場合に走る処理を擬似再現。
		simulateBallPhysics(in.Payload)

	case "goal.check":
		// サーバー権威でのゴール判定を擬似再現（座標がゴール領域に入ったかの判定）。
		simulateGoalCheck(in.Payload)

	case "auth.login":
		in.Payload = handleLogin(in.Payload)

	case "auth.verify":
		in.Payload = handleVerify(in.Payload)

	case "match.quick":
		in.Payload = handleMatchQuick(in.Payload)

	case "room.create":
		in.Payload = handleRoomCreate(in.Payload)

	case "state.sync":
		in.Payload = handleStateSync(in.Payload)

	case "timer.sync":
		in.Payload = handleTimerSync(in.Payload)

	case "asset.load":
		in.Payload = handleAssetLoad(in.Payload)
	}

	in.ServerSend = nowMicro()
	return in
}

// kickPayload は ball.kick の payload。
type kickPayload struct {
	Force      Vec3    `json:"force"`
	ChargeRate float64 `json:"chargeRate"`
	BallPos    Vec3    `json:"ballPos"`
}

// goalPayload は goal.check の payload。
type goalPayload struct {
	BallPos Vec3 `json:"ballPos"`
}

// simulateBallPhysics は軽い弾道計算を回して「サーバー権威の物理」の負荷感を出す。
// 結果は使わない（負荷を再現するのが目的）。
func simulateBallPhysics(payload json.RawMessage) {
	var p kickPayload
	_ = json.Unmarshal(payload, &p)

	// 60ステップ（約1秒ぶん）を重力・摩擦込みで前進させる。
	const dt = 1.0 / 60.0
	const gravity = -9.81
	const friction = 0.98
	vx := p.Force.X * (0.5 + p.ChargeRate)
	vy := p.Force.Y * (0.5 + p.ChargeRate)
	vz := p.Force.Z * (0.5 + p.ChargeRate)
	x, y, z := p.BallPos.X, p.BallPos.Y, p.BallPos.Z
	var acc float64
	for i := 0; i < 60; i++ {
		vy += gravity * dt
		x += vx * dt
		y += vy * dt
		z += vz * dt
		if y < 0 {
			y = 0
			vy = -vy * 0.6 // 跳ね返り
		}
		vx *= friction
		vz *= friction
		acc += math.Sqrt(vx*vx + vz*vz) // それっぽい計算負荷
	}
	_ = acc
	_ = x
	_ = z
}

// simulateGoalCheck はボール座標がゴール領域に入ったかを判定する擬似処理。
func simulateGoalCheck(payload json.RawMessage) bool {
	var p goalPayload
	_ = json.Unmarshal(payload, &p)
	// ゴール領域（適当な箱）に入っているか。
	inX := p.BallPos.X > 18 && p.BallPos.X < 22
	inY := p.BallPos.Y > 0 && p.BallPos.Y < 3
	inZ := p.BallPos.Z > -4 && p.BallPos.Z < 4
	return inX && inY && inZ
}
