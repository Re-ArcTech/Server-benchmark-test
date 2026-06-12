package main

import (
	"encoding/json"
	"io"
	"net/http"
)

// handleRestEcho は REST（HTTP POST）方式のエンドポイント。
// リクエストごとに新しいHTTP往復が発生する点が WebSocket / WebRTC との最大の違い。
// クライアントは1メッセージ = 1 POST で送り、ボディに Envelope を載せる。
func handleRestEcho(w http.ResponseWriter, r *http.Request) {
	if r.Method != http.MethodPost {
		http.Error(w, "POST only", http.StatusMethodNotAllowed)
		return
	}

	body, err := io.ReadAll(io.LimitReader(r.Body, 1<<20)) // 1MB上限
	if err != nil {
		http.Error(w, "read error", http.StatusBadRequest)
		return
	}
	defer r.Body.Close()

	var env Envelope
	if err := json.Unmarshal(body, &env); err != nil {
		http.Error(w, "invalid json", http.StatusBadRequest)
		return
	}

	out := process(&env)

	w.Header().Set("Content-Type", "application/json")
	// CORS（WebGLや別オリジンからのテストでも弾かれないように）。
	w.Header().Set("Access-Control-Allow-Origin", "*")
	_ = json.NewEncoder(w).Encode(out)
}

// handleCORS は OPTIONS プリフライトへの応答。
func handleCORS(w http.ResponseWriter, r *http.Request) {
	w.Header().Set("Access-Control-Allow-Origin", "*")
	w.Header().Set("Access-Control-Allow-Methods", "POST, GET, OPTIONS")
	w.Header().Set("Access-Control-Allow-Headers", "Content-Type")
	w.WriteHeader(http.StatusNoContent)
}
