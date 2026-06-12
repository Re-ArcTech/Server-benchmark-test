// yubi-bench-server
//
// ゆびサッカー Ver.3.0 の通信方式検証用ベンチサーバー。
// 同一の Envelope メッセージを REST / WebSocket / WebRTC の3方式で受け取り、
// それぞれ同じ処理（process）をして返す。Unity 側からRTT等を計測して比較する。
//
//	GET  /health        ヘルスチェック（Render用）
//	POST /api/echo      REST方式
//	GET  /ws            WebSocket方式
//	POST /rtc/offer     WebRTC方式（簡易シグナリング）
package main

import (
	"log"
	"net/http"
	"os"
)

// serverVersion はデプロイ確認用（/health と /stats に出る）。
const serverVersion = "v2"

func main() {
	mux := http.NewServeMux()

	// ヘルスチェック。Render はこれが 200 を返すかで起動確認する。
	mux.HandleFunc("/health", func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Access-Control-Allow-Origin", "*")
		w.WriteHeader(http.StatusOK)
		_, _ = w.Write([]byte("ok " + serverVersion))
	})

	// リソース観測（負荷テスト用）。
	mux.HandleFunc("/stats", handleStats)

	// REST方式。OPTIONS（CORSプリフライト）にも対応。
	mux.HandleFunc("/api/echo", func(w http.ResponseWriter, r *http.Request) {
		if r.Method == http.MethodOptions {
			handleCORS(w, r)
			return
		}
		handleRestEcho(w, r)
	})

	// WebSocket方式。
	mux.HandleFunc("/ws", handleWS)

	// WebRTC方式（簡易）。
	mux.HandleFunc("/rtc/offer", handleRTCOffer)

	// ルート。動作確認用の簡単な案内。
	mux.HandleFunc("/", func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path != "/" {
			http.NotFound(w, r)
			return
		}
		w.Header().Set("Content-Type", "text/plain; charset=utf-8")
		_, _ = w.Write([]byte("yubi-bench-server: /health /api/echo /ws /rtc/offer\n"))
	})

	// Render は PORT 環境変数でリッスンするポートを指定してくる。
	port := os.Getenv("PORT")
	if port == "" {
		port = "8080"
	}
	addr := ":" + port

	log.Printf("yubi-bench-server listening on %s", addr)
	if err := http.ListenAndServe(addr, mux); err != nil {
		log.Fatalf("server error: %v", err)
	}
}
