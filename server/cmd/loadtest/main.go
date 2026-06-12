// loadtest - yubi-bench-server の負荷・特性計測ツール
//
// サブコマンド:
//   ccu       -url U -conns N -duration 10s -rate 5   同時接続テスト（各接続が rate msg/s で ping）
//   stream    -url U -conns N -fps 30 -seconds 10     fire-and-forget 連続送信（取りこぼし・ジッター）
//   reconnect -url U -times 10                        再接続(TCP+TLS+Upgrade)時間の分布
//   sizes     -url U                                  ペイロードサイズ別 RTT（64B〜64KB）
//   stall     -url U(ローカルのみ)                      TCPストールプロキシでHOLブロッキング実証
//   scenarios -url U -n 20                            全シナリオの RTT + サーバー処理時間
//
// 例:
//   go run ./cmd/loadtest ccu -url ws://localhost:8080/ws -conns 500 -duration 10s
//   go run ./cmd/loadtest scenarios -url https://yubi-bench-server.onrender.com -n 20
package main

import (
	"bytes"
	"encoding/json"
	"flag"
	"fmt"
	"io"
	"math"
	"math/rand"
	"net"
	"net/http"
	"os"
	"sort"
	"strings"
	"sync"
	"sync/atomic"
	"time"

	"github.com/gorilla/websocket"
)

func main() {
	if len(os.Args) < 2 {
		fmt.Println("usage: loadtest <ccu|stream|reconnect|sizes|stall|scenarios> [flags]")
		os.Exit(1)
	}
	cmd := os.Args[1]
	args := os.Args[2:]

	switch cmd {
	case "ccu":
		cmdCCU(args)
	case "stream":
		cmdStream(args)
	case "reconnect":
		cmdReconnect(args)
	case "sizes":
		cmdSizes(args)
	case "stall":
		cmdStall(args)
	case "scenarios":
		cmdScenarios(args)
	default:
		fmt.Println("unknown command:", cmd)
		os.Exit(1)
	}
}

// ---------- 共通 ----------

type stats struct {
	mu      sync.Mutex
	samples []float64
}

func (s *stats) add(ms float64) {
	s.mu.Lock()
	s.samples = append(s.samples, ms)
	s.mu.Unlock()
}

func (s *stats) report(label string) {
	s.mu.Lock()
	defer s.mu.Unlock()
	if len(s.samples) == 0 {
		fmt.Printf("%-22s no samples\n", label)
		return
	}
	xs := append([]float64(nil), s.samples...)
	sort.Float64s(xs)
	var sum float64
	for _, x := range xs {
		sum += x
	}
	avg := sum / float64(len(xs))
	p := func(q float64) float64 { return xs[int(q*float64(len(xs)-1))] }
	var sq float64
	for _, x := range xs {
		sq += (x - avg) * (x - avg)
	}
	jit := math.Sqrt(sq / float64(len(xs)))
	fmt.Printf("%-22s n=%-6d avg=%8.2f p50=%8.2f p95=%8.2f p99=%8.2f max=%8.2f jit=%6.2f (ms)\n",
		label, len(xs), avg, p(0.50), p(0.95), p(0.99), xs[len(xs)-1], jit)
}

func wsURL(base string) string {
	u := strings.TrimSuffix(base, "/")
	u = strings.Replace(u, "https://", "wss://", 1)
	u = strings.Replace(u, "http://", "ws://", 1)
	if !strings.HasSuffix(u, "/ws") {
		u += "/ws"
	}
	return u
}

func httpURL(base string) string {
	u := strings.TrimSuffix(base, "/")
	u = strings.Replace(u, "wss://", "https://", 1)
	u = strings.Replace(u, "ws://", "http://", 1)
	u = strings.TrimSuffix(u, "/ws")
	return u
}

func fetchStats(base string) string {
	resp, err := http.Get(httpURL(base) + "/stats")
	if err != nil {
		return "(stats unavailable: " + err.Error() + ")"
	}
	defer resp.Body.Close()
	b, _ := io.ReadAll(resp.Body)
	return string(bytes.TrimSpace(b))
}

func envMsg(typ string, seq int64, payload string) []byte {
	if payload == "" {
		payload = "{}"
	}
	return []byte(fmt.Sprintf(`{"type":"%s","seq":%d,"timestamp":%d,"payload":%s}`,
		typ, seq, time.Now().UnixMilli(), payload))
}

// ---------- ccu: 同時接続テスト ----------

func cmdCCU(args []string) {
	fs := flag.NewFlagSet("ccu", flag.ExitOnError)
	url := fs.String("url", "ws://localhost:8080/ws", "WebSocket URL or base URL")
	conns := fs.Int("conns", 100, "同時接続数")
	duration := fs.Duration("duration", 10*time.Second, "計測時間")
	rate := fs.Float64("rate", 5, "1接続あたりの毎秒メッセージ数")
	_ = fs.Parse(args)

	target := wsURL(*url)
	fmt.Printf("=== CCU test: %d conns, %.0f msg/s each, %v ===\n", *conns, *rate, *duration)
	fmt.Println("stats before:", fetchStats(*url))

	var connected, failed atomic.Int64
	rtt := &stats{}
	var wg sync.WaitGroup
	stop := make(chan struct{})

	// 接続を段階的に張る（一斉だと接続自体が詰まる）
	dialStart := time.Now()
	for i := 0; i < *conns; i++ {
		wg.Add(1)
		go func(id int) {
			defer wg.Done()
			d := websocket.Dialer{HandshakeTimeout: 20 * time.Second}
			c, _, err := d.Dial(target, nil)
			if err != nil {
				failed.Add(1)
				return
			}
			connected.Add(1)
			defer c.Close()

			interval := time.Duration(float64(time.Second) / *rate)
			// 開始タイミングをばらす
			time.Sleep(time.Duration(rand.Int63n(int64(interval))))
			ticker := time.NewTicker(interval)
			defer ticker.Stop()
			var seq int64
			for {
				select {
				case <-stop:
					return
				case <-ticker.C:
					t0 := time.Now()
					if err := c.WriteMessage(websocket.TextMessage, envMsg("player.move", seq, "")); err != nil {
						return
					}
					_ = c.SetReadDeadline(time.Now().Add(15 * time.Second))
					if _, _, err := c.ReadMessage(); err != nil {
						return
					}
					rtt.add(float64(time.Since(t0).Microseconds()) / 1000.0)
					seq++
				}
			}
		}(i)
		if i%50 == 49 {
			time.Sleep(100 * time.Millisecond) // 50接続ごとに少し待つ
		}
	}

	// 接続完了を待ってから計測時間ぶん流す
	time.Sleep(2 * time.Second)
	fmt.Printf("connected=%d failed=%d (dial total %.1fs)\n",
		connected.Load(), failed.Load(), time.Since(dialStart).Seconds())
	fmt.Println("stats during:", fetchStats(*url))

	time.Sleep(*duration)
	close(stop)
	wg.Wait()

	rtt.report(fmt.Sprintf("RTT @%dconns", *conns))
	fmt.Println("stats after:", fetchStats(*url))
}

// ---------- stream: fire-and-forget ----------

func cmdStream(args []string) {
	fs := flag.NewFlagSet("stream", flag.ExitOnError)
	url := fs.String("url", "ws://localhost:8080/ws", "URL")
	conns := fs.Int("conns", 1, "同時ストリーム数")
	fps := fs.Int("fps", 30, "送信レート")
	seconds := fs.Int("seconds", 10, "送信時間")
	_ = fs.Parse(args)

	target := wsURL(*url)
	fmt.Printf("=== stream test: %d conns x %dfps x %ds (fire-and-forget) ===\n", *conns, *fps, *seconds)

	var wg sync.WaitGroup
	for i := 0; i < *conns; i++ {
		wg.Add(1)
		go func(id int) {
			defer wg.Done()
			c, _, err := websocket.DefaultDialer.Dial(target, nil)
			if err != nil {
				fmt.Printf("[%d] dial err: %v\n", id, err)
				return
			}
			defer c.Close()

			// stream.start → ACK
			_ = c.WriteMessage(websocket.TextMessage, envMsg("stream.start", 0, ""))
			_, _, _ = c.ReadMessage()

			// fps で seconds 秒間投げ続ける（返事は待たない）
			interval := time.Second / time.Duration(*fps)
			total := *fps * *seconds
			ticker := time.NewTicker(interval)
			sendJitter := &stats{}
			last := time.Time{}
			for seq := 0; seq < total; seq++ {
				<-ticker.C
				now := time.Now()
				if !last.IsZero() {
					sendJitter.add(float64(now.Sub(last).Microseconds()) / 1000.0)
				}
				last = now
				if err := c.WriteMessage(websocket.TextMessage,
					envMsg("stream.move", int64(seq), `{"x":1.2,"y":0,"z":3.4}`)); err != nil {
					fmt.Printf("[%d] write err at seq=%d: %v\n", id, seq, err)
					ticker.Stop()
					return
				}
			}
			ticker.Stop()

			// stream.end → サーバー集計を受け取る
			_ = c.WriteMessage(websocket.TextMessage, envMsg("stream.end", int64(total), ""))
			_ = c.SetReadDeadline(time.Now().Add(10 * time.Second))
			_, data, err := c.ReadMessage()
			if err != nil {
				fmt.Printf("[%d] end err: %v\n", id, err)
				return
			}
			var env struct {
				Payload json.RawMessage `json:"payload"`
			}
			_ = json.Unmarshal(data, &env)
			fmt.Printf("[conn %d] sent=%d server-report=%s\n", id, total, env.Payload)
		}(i)
	}
	wg.Wait()
}

// ---------- reconnect ----------

func cmdReconnect(args []string) {
	fs := flag.NewFlagSet("reconnect", flag.ExitOnError)
	url := fs.String("url", "ws://localhost:8080/ws", "URL")
	times := fs.Int("times", 10, "再接続回数")
	_ = fs.Parse(args)

	target := wsURL(*url)
	fmt.Printf("=== reconnect test: %d times ===\n", *times)

	connect := &stats{}
	firstMsg := &stats{}
	for i := 0; i < *times; i++ {
		t0 := time.Now()
		c, _, err := websocket.DefaultDialer.Dial(target, nil)
		if err != nil {
			fmt.Println("dial err:", err)
			continue
		}
		connect.add(float64(time.Since(t0).Microseconds()) / 1000.0)

		// 接続後の最初のメッセージ往復（=復帰後すぐ動けるまで）
		t1 := time.Now()
		_ = c.WriteMessage(websocket.TextMessage, envMsg("timer.sync", 0, ""))
		_, _, _ = c.ReadMessage()
		firstMsg.add(float64(time.Since(t1).Microseconds()) / 1000.0)

		c.Close()
		time.Sleep(200 * time.Millisecond)
	}
	connect.report("connect(TCP+TLS+WS)")
	firstMsg.report("first message RTT")
}

// ---------- sizes ----------

func cmdSizes(args []string) {
	fs := flag.NewFlagSet("sizes", flag.ExitOnError)
	url := fs.String("url", "http://localhost:8080", "base URL")
	n := fs.Int("n", 15, "サイズごとの試行回数")
	_ = fs.Parse(args)

	fmt.Println("=== payload size sweep (WebSocket, echo.size) ===")
	target := wsURL(*url)
	c, _, err := websocket.DefaultDialer.Dial(target, nil)
	if err != nil {
		fmt.Println("dial err:", err)
		return
	}
	defer c.Close()

	for _, kb := range []int{0, 1, 4, 16, 64} {
		size := kb * 1024
		if size == 0 {
			size = 64 // 64B
		}
		blob := strings.Repeat("a", size)
		payload := fmt.Sprintf(`{"data":"%s"}`, blob)
		st := &stats{}
		for i := 0; i < *n; i++ {
			t0 := time.Now()
			if err := c.WriteMessage(websocket.TextMessage, envMsg("echo.size", int64(i), payload)); err != nil {
				fmt.Println("write err:", err)
				return
			}
			_, _, err := c.ReadMessage()
			if err != nil {
				fmt.Println("read err:", err)
				return
			}
			st.add(float64(time.Since(t0).Microseconds()) / 1000.0)
		}
		label := fmt.Sprintf("%dB", size)
		if kb > 0 {
			label = fmt.Sprintf("%dKB", kb)
		}
		st.report("size=" + label)
	}
}

// ---------- stall: HOLブロッキング実証 ----------

// cmdStall は「パケットロス時のTCP再送詰まり」を擬似再現する。
// ローカルにTCPプロキシを立て、転送を時々250msフリーズさせる。
// TCP上のWebSocketは1箇所詰まると後続メッセージが全部巻き添えになる
// (head-of-line blocking) ことを、フリーズ中に送った全メッセージの
// RTTが連鎖的に悪化することで実証する。
//
// UDPベースのWebRTC(unreliableモード)ならフリーズしたパケットだけ捨てて
// 後続は流れるので、この巻き添えが起きない（=ロスの多いモバイル回線での優位性）。
func cmdStall(args []string) {
	fs := flag.NewFlagSet("stall", flag.ExitOnError)
	upstream := fs.String("upstream", "localhost:8080", "本物のサーバー host:port")
	stallEvery := fs.Duration("every", 2*time.Second, "ストール間隔")
	stallFor := fs.Duration("for", 250*time.Millisecond, "ストール時間")
	_ = fs.Parse(args)

	// プロキシ起動
	ln, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		fmt.Println("listen err:", err)
		return
	}
	proxyAddr := ln.Addr().String()
	fmt.Printf("=== stall test: proxy %s -> %s (%vごとに%vフリーズ) ===\n",
		proxyAddr, *upstream, *stallEvery, *stallFor)

	var stalling atomic.Bool
	go func() {
		for {
			time.Sleep(*stallEvery)
			stalling.Store(true)
			time.Sleep(*stallFor)
			stalling.Store(false)
		}
	}()

	go func() {
		for {
			client, err := ln.Accept()
			if err != nil {
				return
			}
			server, err := net.Dial("tcp", *upstream)
			if err != nil {
				client.Close()
				continue
			}
			pipe := func(dst, src net.Conn) {
				buf := make([]byte, 32*1024)
				for {
					n, err := src.Read(buf)
					if err != nil {
						dst.Close()
						return
					}
					// ストール中は転送を止める（=パケット再送待ちの擬似再現）
					for stalling.Load() {
						time.Sleep(5 * time.Millisecond)
					}
					if _, err := dst.Write(buf[:n]); err != nil {
						src.Close()
						return
					}
				}
			}
			go pipe(server, client)
			go pipe(client, server)
		}
	}()

	// 直結 vs プロキシ経由で 30fps x 8秒 ping して比較
	run := func(label, addr string) {
		c, _, err := websocket.DefaultDialer.Dial("ws://"+addr+"/ws", nil)
		if err != nil {
			fmt.Println(label, "dial err:", err)
			return
		}
		defer c.Close()
		st := &stats{}
		affected := 0
		total := 240 // 30fps x 8s
		ticker := time.NewTicker(time.Second / 30)
		defer ticker.Stop()
		for i := 0; i < total; i++ {
			<-ticker.C
			t0 := time.Now()
			_ = c.WriteMessage(websocket.TextMessage, envMsg("player.move", int64(i), ""))
			_ = c.SetReadDeadline(time.Now().Add(10 * time.Second))
			if _, _, err := c.ReadMessage(); err != nil {
				fmt.Println(label, "read err:", err)
				return
			}
			ms := float64(time.Since(t0).Microseconds()) / 1000.0
			st.add(ms)
			if ms > 50 {
				affected++
			}
		}
		st.report(label)
		fmt.Printf("%-22s 50ms超のメッセージ: %d/%d (%.0f%%)\n", "", affected, total,
			float64(affected)/float64(total)*100)
	}

	run("direct (no loss)", *upstream)
	run("via stall proxy", proxyAddr)

	fmt.Println()
	fmt.Println("解説: プロキシ経由では250msのフリーズ(=ロス再送待ち)のたびに")
	fmt.Println("      後続メッセージ全部が巻き添えで遅延する(TCPのHOLブロッキング)。")
	fmt.Println("      UDPベースのWebRTC unreliableモードはこの巻き添えが起きない。")
}

// ---------- scenarios: 全シナリオ計測 ----------

func cmdScenarios(args []string) {
	fs := flag.NewFlagSet("scenarios", flag.ExitOnError)
	url := fs.String("url", "http://localhost:8080", "base URL")
	n := fs.Int("n", 20, "シナリオごとの試行回数")
	_ = fs.Parse(args)

	scenarios := []struct {
		typ     string
		payload string
	}{
		{"echo", `{}`},
		{"player.move", `{"pos":{"x":1.2,"y":0,"z":3.4},"rot":{"y":90}}`},
		{"ball.kick", `{"force":{"x":0,"y":2.5,"z":12},"chargeRate":0.85,"ballPos":{"x":5,"y":0,"z":6}}`},
		{"goal.check", `{"ballPos":{"x":20,"y":1,"z":0}}`},
		{"auth.login", `{"user":"bench","pass":"secret123"}`},
		{"auth.verify", `{}`},
		{"match.quick", `{"playerId":"bench"}`},
		{"room.create", `{"name":"bench"}`},
		{"state.sync", `{}`},
		{"timer.sync", `{}`},
		{"asset.load", `{"sizeKb":64}`},
	}

	// --- WebSocket ---
	fmt.Println("=== scenarios via WebSocket ===")
	c, _, err := websocket.DefaultDialer.Dial(wsURL(*url), nil)
	if err != nil {
		fmt.Println("ws dial err:", err)
	} else {
		for _, sc := range scenarios {
			st := &stats{}
			srv := &stats{}
			for i := 0; i < *n; i++ {
				t0 := time.Now()
				_ = c.WriteMessage(websocket.TextMessage, envMsg(sc.typ, int64(i), sc.payload))
				_ = c.SetReadDeadline(time.Now().Add(20 * time.Second))
				_, data, err := c.ReadMessage()
				if err != nil {
					fmt.Println("read err:", err)
					break
				}
				st.add(float64(time.Since(t0).Microseconds()) / 1000.0)
				var env struct {
					ServerRecv int64 `json:"serverRecv"`
					ServerSend int64 `json:"serverSend"`
				}
				_ = json.Unmarshal(data, &env)
				srv.add(float64(env.ServerSend-env.ServerRecv) / 1000.0)
			}
			st.report("WS " + sc.typ)
			if len(srv.samples) > 0 {
				var sum float64
				for _, x := range srv.samples {
					sum += x
				}
				fmt.Printf("%-22s srvProc avg=%.3fms\n", "", sum/float64(len(srv.samples)))
			}
		}
		c.Close()
	}

	// --- REST ---
	fmt.Println()
	fmt.Println("=== scenarios via REST ===")
	client := &http.Client{Timeout: 30 * time.Second}
	for _, sc := range scenarios {
		st := &stats{}
		for i := 0; i < *n; i++ {
			body := envMsg(sc.typ, int64(i), sc.payload)
			t0 := time.Now()
			resp, err := client.Post(httpURL(*url)+"/api/echo", "application/json", bytes.NewReader(body))
			if err != nil {
				fmt.Println("post err:", err)
				break
			}
			_, _ = io.Copy(io.Discard, resp.Body)
			resp.Body.Close()
			st.add(float64(time.Since(t0).Microseconds()) / 1000.0)
		}
		st.report("REST " + sc.typ)
	}
}
