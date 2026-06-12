package main

import (
	"encoding/json"
	"log"
	"net/http"

	"github.com/gorilla/websocket"
)

// upgrader は HTTP接続を WebSocket に昇格させる。
var upgrader = websocket.Upgrader{
	ReadBufferSize:  4096,
	WriteBufferSize: 4096,
	// テスト用途なので全オリジンを許可。本番は適切に絞ること。
	CheckOrigin: func(r *http.Request) bool { return true },
}

// handleWS は WebSocket 方式のエンドポイント。
// REST と違い、一度ハンドシェイクしたら接続を張りっぱなしにして
// 何度でも双方向にメッセージを送れる。ゲーム中の位置同期はこれを使う想定。
func handleWS(w http.ResponseWriter, r *http.Request) {
	conn, err := upgrader.Upgrade(w, r, nil)
	if err != nil {
		log.Printf("[ws] upgrade error: %v", err)
		return
	}
	defer conn.Close()

	log.Printf("[ws] client connected: %s", r.RemoteAddr)

	for {
		mt, data, err := conn.ReadMessage()
		if err != nil {
			// 正常切断 or エラー。ループを抜けて接続を閉じる。
			if websocket.IsUnexpectedCloseError(err, websocket.CloseGoingAway, websocket.CloseNormalClosure) {
				log.Printf("[ws] read error: %v", err)
			}
			break
		}

		// テキストフレームの Envelope を処理して同じ接続で返す。
		if mt == websocket.TextMessage {
			var env Envelope
			if err := json.Unmarshal(data, &env); err != nil {
				continue
			}
			out := process(&env)
			outBytes, _ := json.Marshal(out)
			if err := conn.WriteMessage(websocket.TextMessage, outBytes); err != nil {
				log.Printf("[ws] write error: %v", err)
				break
			}
		}
	}

	log.Printf("[ws] client disconnected: %s", r.RemoteAddr)
}
