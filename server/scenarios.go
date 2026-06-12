package main

import (
	"crypto/hmac"
	"crypto/sha256"
	"encoding/base64"
	"encoding/json"
	"fmt"
	"math/rand"
	"strings"
	"sync"
	"time"
)

// このファイルは「実ゲームで発生するやり取り」を擬似再現するシナリオ群。
// 速度だけでなく、type ごとのサーバー処理コスト（srvProc）の違いを見るのが目的。
//
//   auth.login   : KDF(パスワードハッシュ) + JWT発行 … CPU重い処理の代表
//   auth.verify  : JWT検証 … 毎リクエスト発生する軽いCPU処理
//   match.quick  : マッチングキュー操作 … 排他制御を伴う状態変更
//   room.create  : ルーム作成 … 状態変更 + ID払い出し
//   state.sync   : ワールドスナップショット生成 … レスポンスが大きい(~2KB)
//   timer.sync   : サーバー時刻返却 … 最小レスポンス
//   asset.load   : 大きなデータ配信(~KB指定) … 帯域・ダウンロード系
//   echo.size    : クライアント指定サイズのままecho … 上り帯域系

var jwtSecret = []byte("yubi-bench-secret-key-2026")

// --- auth.login ---

type loginPayload struct {
	User string `json:"user"`
	Pass string `json:"pass"`
}

type loginResult struct {
	Token string `json:"token"`
	User  string `json:"user"`
}

// kdfHash はパスワードKDFの負荷を擬似再現（sha256 x 10,000回 ≒ 数ms）。
// 本物は bcrypt/argon2 でさらに重い（50-100ms級）。
func kdfHash(pass string) []byte {
	h := sha256.Sum256([]byte(pass))
	for i := 0; i < 10000; i++ {
		h = sha256.Sum256(h[:])
	}
	return h[:]
}

func signJWT(user string) string {
	header := base64.RawURLEncoding.EncodeToString([]byte(`{"alg":"HS256","typ":"JWT"}`))
	claims := base64.RawURLEncoding.EncodeToString([]byte(fmt.Sprintf(
		`{"sub":"%s","iat":%d,"exp":%d}`, user, time.Now().Unix(), time.Now().Add(time.Hour).Unix())))
	mac := hmac.New(sha256.New, jwtSecret)
	mac.Write([]byte(header + "." + claims))
	sig := base64.RawURLEncoding.EncodeToString(mac.Sum(nil))
	return header + "." + claims + "." + sig
}

func verifyJWT(token string) bool {
	parts := strings.Split(token, ".")
	if len(parts) != 3 {
		return false
	}
	mac := hmac.New(sha256.New, jwtSecret)
	mac.Write([]byte(parts[0] + "." + parts[1]))
	expected := base64.RawURLEncoding.EncodeToString(mac.Sum(nil))
	return hmac.Equal([]byte(expected), []byte(parts[2]))
}

func handleLogin(payload json.RawMessage) json.RawMessage {
	var p loginPayload
	_ = json.Unmarshal(payload, &p)
	if p.User == "" {
		p.User = "guest"
	}
	_ = kdfHash(p.Pass) // KDF負荷（結果は使わない）
	token := signJWT(p.User)
	out, _ := json.Marshal(loginResult{Token: token, User: p.User})
	return out
}

// handleVerify は「トークン付きリクエストの検証」のCPUコストを計測する。
// ベンチ用にサーバー側で発行→検証を1往復ぶん実行する
// （クライアントが本物のトークンを持っていなくても計測できるようにするため）。
func handleVerify(payload json.RawMessage) json.RawMessage {
	var p struct {
		Token string `json:"token"`
	}
	_ = json.Unmarshal(payload, &p)
	token := p.Token
	if token == "" {
		token = signJWT("bench")
	}
	ok := verifyJWT(token)
	out, _ := json.Marshal(map[string]bool{"valid": ok})
	return out
}

// --- match.quick / room.create ---

var (
	matchMu    sync.Mutex
	matchQueue []string
	roomMu     sync.Mutex
	rooms      = map[string]int{} // roomId -> メンバー数
	roomSeq    int64
)

type matchResult struct {
	RoomID   string `json:"roomId"`
	Opponent string `json:"opponent"`
	Waited   bool   `json:"waited"`
}

func handleMatchQuick(payload json.RawMessage) json.RawMessage {
	var p struct {
		PlayerID string `json:"playerId"`
	}
	_ = json.Unmarshal(payload, &p)
	if p.PlayerID == "" {
		p.PlayerID = fmt.Sprintf("p%d", rand.Int63())
	}

	matchMu.Lock()
	var res matchResult
	if len(matchQueue) > 0 {
		// 待っている人がいる → 即マッチ成立
		opp := matchQueue[0]
		matchQueue = matchQueue[1:]
		roomSeq++
		res = matchResult{RoomID: fmt.Sprintf("room_%d", roomSeq), Opponent: opp, Waited: false}
	} else {
		// 誰もいない → キューに入れて即BOTマッチ（ベンチは待たない）
		matchQueue = append(matchQueue, p.PlayerID)
		matchQueue = matchQueue[1:] // すぐ取り出してBOTと組む
		roomSeq++
		res = matchResult{RoomID: fmt.Sprintf("room_%d", roomSeq), Opponent: "BOT", Waited: true}
	}
	matchMu.Unlock()

	out, _ := json.Marshal(res)
	return out
}

func handleRoomCreate(payload json.RawMessage) json.RawMessage {
	var p struct {
		Name string `json:"name"`
	}
	_ = json.Unmarshal(payload, &p)

	roomMu.Lock()
	roomSeq++
	id := fmt.Sprintf("room_%d", roomSeq)
	rooms[id] = 1
	// メモリリーク防止: ベンチで無限に増えないよう上限管理
	if len(rooms) > 10000 {
		rooms = map[string]int{}
	}
	roomMu.Unlock()

	out, _ := json.Marshal(map[string]string{"roomId": id, "name": p.Name})
	return out
}

// --- state.sync ---

type playerState struct {
	ID  string  `json:"id"`
	X   float64 `json:"x"`
	Y   float64 `json:"y"`
	Z   float64 `json:"z"`
	RotY float64 `json:"rotY"`
	VX  float64 `json:"vx"`
	VZ  float64 `json:"vz"`
	Anim string `json:"anim"`
}

type ballState struct {
	ID string  `json:"id"`
	X  float64 `json:"x"`
	Y  float64 `json:"y"`
	Z  float64 `json:"z"`
	VX float64 `json:"vx"`
	VY float64 `json:"vy"`
	VZ float64 `json:"vz"`
}

type worldSnapshot struct {
	Tick    int64         `json:"tick"`
	Players []playerState `json:"players"`
	Balls   []ballState   `json:"balls"`
	ScoreA  int           `json:"scoreA"`
	ScoreB  int           `json:"scoreB"`
}

// handleStateSync は6人+3ボールのワールドスナップショット(~1.5KB)を生成して返す。
// Ver.3.0の「サーバー権威の全体同期」1tickぶんのレスポンスサイズ感を再現する。
func handleStateSync(payload json.RawMessage) json.RawMessage {
	snap := worldSnapshot{Tick: time.Now().UnixMilli(), ScoreA: 2, ScoreB: 1}
	for i := 0; i < 6; i++ {
		snap.Players = append(snap.Players, playerState{
			ID: fmt.Sprintf("p%d", i),
			X:  rand.Float64() * 40, Y: 0, Z: rand.Float64() * 20,
			RotY: rand.Float64() * 360,
			VX:   rand.Float64() * 5, VZ: rand.Float64() * 5,
			Anim: "run",
		})
	}
	for i := 0; i < 3; i++ {
		snap.Balls = append(snap.Balls, ballState{
			ID: fmt.Sprintf("b%d", i),
			X:  rand.Float64() * 40, Y: rand.Float64() * 3, Z: rand.Float64() * 20,
			VX: rand.Float64() * 10, VY: rand.Float64() * 5, VZ: rand.Float64() * 10,
		})
	}
	out, _ := json.Marshal(snap)
	return out
}

// --- timer.sync ---

func handleTimerSync(payload json.RawMessage) json.RawMessage {
	now := time.Now().UnixMilli()
	out, _ := json.Marshal(map[string]int64{
		"serverNow": now,
		"matchEnds": now + 60_000,
	})
	return out
}

// --- asset.load ---

// assetBlob は起動時に作る最大512KBのダミーデータ（base64文字列）。
var assetBlob = func() string {
	b := make([]byte, 512*1024*3/4) // base64で512KBになるサイズ
	rand.Read(b)
	return base64.StdEncoding.EncodeToString(b)
}()

func handleAssetLoad(payload json.RawMessage) json.RawMessage {
	var p struct {
		SizeKb int `json:"sizeKb"`
	}
	_ = json.Unmarshal(payload, &p)
	if p.SizeKb <= 0 {
		p.SizeKb = 64
	}
	if p.SizeKb > 512 {
		p.SizeKb = 512
	}
	n := p.SizeKb * 1024
	if n > len(assetBlob) {
		n = len(assetBlob)
	}
	out, _ := json.Marshal(map[string]string{"data": assetBlob[:n]})
	return out
}
