package main

import (
	"encoding/json"
	"log"
	"net/http"
	"os"
	"strconv"
	"sync"

	"github.com/pion/webrtc/v4"
)

// rtcOfferRequest はクライアントから来る SDP offer。
type rtcOfferRequest struct {
	SDP string `json:"sdp"`
}

// rtcAnswerResponse はサーバーが返す SDP answer。
type rtcAnswerResponse struct {
	SDP string `json:"sdp"`
}

var (
	rtcAPIOnce sync.Once
	rtcAPI     *webrtc.API
)

// getRTCAPI は SettingEngine を仕込んだ *webrtc.API を1回だけ作って使い回す。
//
// VPS/クラウドで動かす場合の2つのお作法:
//   - PUBLIC_IP 環境変数を設定すると、その公開IPを 1:1 NAT として ICE 候補に広告する。
//     これが無いとクラウド上のサーバーは「自分の公開住所」を相手に教えられず接続できない。
//   - UDPポートを 50000-50100 に固定する。ファイアウォールはこの範囲だけ開ければよい。
//     （例: ufw allow 50000:50100/udp）
//
// ローカル(PUBLIC_IP未設定)ではそのまま動く。
func getRTCAPI() *webrtc.API {
	rtcAPIOnce.Do(func() {
		s := webrtc.SettingEngine{}

		if ip := os.Getenv("PUBLIC_IP"); ip != "" {
			s.SetNAT1To1IPs([]string{ip}, webrtc.ICECandidateTypeHost)
			log.Printf("[rtc] advertising public IP: %s", ip)
		}

		minPort, maxPort := uint16(50000), uint16(50100)
		if v := os.Getenv("UDP_PORT_MIN"); v != "" {
			if n, err := strconv.Atoi(v); err == nil {
				minPort = uint16(n)
			}
		}
		if v := os.Getenv("UDP_PORT_MAX"); v != "" {
			if n, err := strconv.Atoi(v); err == nil {
				maxPort = uint16(n)
			}
		}
		if err := s.SetEphemeralUDPPortRange(minPort, maxPort); err != nil {
			log.Printf("[rtc] SetEphemeralUDPPortRange error: %v", err)
		}

		rtcAPI = webrtc.NewAPI(webrtc.WithSettingEngine(s))
	})
	return rtcAPI
}

// handleRTCOffer は WebRTC の「簡易シグナリング」エンドポイント。
//
// 本来 WebRTC は P2P で、接続確立には offer/answer + ICE 候補の交換（シグナリング）が要る。
// ここでは最小構成として「offerを1回POST → answerを1回返す」だけにし、
// ICE候補はサーバー側で全部集め終えてから answer に含めて返す（non-trickle）。
//
// DataChannel が開いたら、受け取った Envelope を process して同じチャネルで返すだけ。
// これで REST / WebSocket と同じ土俵で RTT を計測できる。
func handleRTCOffer(w http.ResponseWriter, r *http.Request) {
	if r.Method == http.MethodOptions {
		handleCORS(w, r)
		return
	}
	if r.Method != http.MethodPost {
		http.Error(w, "POST only", http.StatusMethodNotAllowed)
		return
	}

	var req rtcOfferRequest
	if err := json.NewDecoder(r.Body).Decode(&req); err != nil {
		http.Error(w, "invalid json", http.StatusBadRequest)
		return
	}

	// STUNサーバーを指定（NAT越えのため）。テストはローカルでも公開STUNを使う。
	config := webrtc.Configuration{
		ICEServers: []webrtc.ICEServer{
			{URLs: []string{"stun:stun.l.google.com:19302"}},
		},
	}

	pc, err := getRTCAPI().NewPeerConnection(config)
	if err != nil {
		log.Printf("[rtc] new peer connection error: %v", err)
		http.Error(w, "rtc error", http.StatusInternalServerError)
		return
	}

	// クライアントが作った DataChannel を受けて、echo ハンドラを張る。
	pc.OnDataChannel(func(dc *webrtc.DataChannel) {
		log.Printf("[rtc] data channel opened: %s", dc.Label())
		dc.OnMessage(func(msg webrtc.DataChannelMessage) {
			var env Envelope
			if err := json.Unmarshal(msg.Data, &env); err != nil {
				return
			}
			out := process(&env)
			outBytes, _ := json.Marshal(out)
			_ = dc.SendText(string(outBytes))
		})
	})

	pc.OnConnectionStateChange(func(s webrtc.PeerConnectionState) {
		log.Printf("[rtc] connection state: %s", s.String())
		if s == webrtc.PeerConnectionStateFailed || s == webrtc.PeerConnectionStateClosed {
			_ = pc.Close()
		}
	})

	offer := webrtc.SessionDescription{
		Type: webrtc.SDPTypeOffer,
		SDP:  req.SDP,
	}
	if err := pc.SetRemoteDescription(offer); err != nil {
		log.Printf("[rtc] set remote desc error: %v", err)
		http.Error(w, "rtc error", http.StatusInternalServerError)
		return
	}

	answer, err := pc.CreateAnswer(nil)
	if err != nil {
		log.Printf("[rtc] create answer error: %v", err)
		http.Error(w, "rtc error", http.StatusInternalServerError)
		return
	}

	// non-trickle: ICE候補を全部集め終わるまで待ってから answer を返す。
	gatherComplete := webrtc.GatheringCompletePromise(pc)
	if err := pc.SetLocalDescription(answer); err != nil {
		log.Printf("[rtc] set local desc error: %v", err)
		http.Error(w, "rtc error", http.StatusInternalServerError)
		return
	}
	<-gatherComplete

	w.Header().Set("Content-Type", "application/json")
	w.Header().Set("Access-Control-Allow-Origin", "*")
	_ = json.NewEncoder(w).Encode(rtcAnswerResponse{SDP: pc.LocalDescription().SDP})
}
