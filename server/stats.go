package main

import (
	"encoding/json"
	"net/http"
	"runtime"
	"sync/atomic"
	"time"
)

// サーバーリソースの観測用カウンタ。
// CCU負荷テストで「接続数を増やすとサーバーがどれだけ食うか」を見るために使う。
var (
	statMsgProcessed atomic.Int64 // 処理した総メッセージ数
	statActiveWS     atomic.Int64 // 現在のWebSocket接続数
	statStartTime    = time.Now()
)

type statsResponse struct {
	UptimeSec      int64  `json:"uptimeSec"`
	Goroutines     int    `json:"goroutines"`
	HeapAllocMB    float64 `json:"heapAllocMB"`
	SysMB          float64 `json:"sysMB"`
	NumGC          uint32 `json:"numGC"`
	ActiveWSConns  int64  `json:"activeWSConns"`
	MsgsProcessed  int64  `json:"msgsProcessed"`
	NumCPU         int    `json:"numCPU"`
	Version        string `json:"version"`
}

// handleStats は GET /stats。負荷テスト中に外から叩いてリソースを観測する。
func handleStats(w http.ResponseWriter, r *http.Request) {
	var m runtime.MemStats
	runtime.ReadMemStats(&m)

	resp := statsResponse{
		UptimeSec:     int64(time.Since(statStartTime).Seconds()),
		Goroutines:    runtime.NumGoroutine(),
		HeapAllocMB:   float64(m.HeapAlloc) / 1024 / 1024,
		SysMB:         float64(m.Sys) / 1024 / 1024,
		NumGC:         m.NumGC,
		ActiveWSConns: statActiveWS.Load(),
		MsgsProcessed: statMsgProcessed.Load(),
		NumCPU:        runtime.NumCPU(),
		Version:       serverVersion,
	}
	w.Header().Set("Content-Type", "application/json")
	w.Header().Set("Access-Control-Allow-Origin", "*")
	_ = json.NewEncoder(w).Encode(resp)
}
