using System;

namespace YubiBench
{
    /// <summary>
    /// サーバーから返ってくる Envelope の解析用。
    /// JsonUtility は未知フィールド（payload 等）を無視するので、
    /// 計測に必要なタイムスタンプ系だけ持てばよい。
    /// </summary>
    [Serializable]
    public class RespEnvelope
    {
        public string type;
        public long seq;
        public long timestamp;
        public long serverRecv; // サーバー受信時刻（マイクロ秒）
        public long serverSend; // サーバー送信時刻（マイクロ秒）

        /// <summary>サーバー内処理時間（ミリ秒）。</summary>
        public double ServerProcessMillis => (serverSend - serverRecv) / 1000.0;
    }

    /// <summary>
    /// 各シナリオのリクエストJSONを組み立てるヘルパー。
    /// payload は小さく固定なので文字列で直接組む（JsonUtility の payload 制約を避ける）。
    /// </summary>
    public static class Messages
    {
        private static long NowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        public static string Echo(long seq)
            => $"{{\"type\":\"echo\",\"seq\":{seq},\"timestamp\":{NowMs},\"payload\":{{}}}}";

        public static string PlayerMove(long seq)
            => $"{{\"type\":\"player.move\",\"seq\":{seq},\"timestamp\":{NowMs}," +
               "\"payload\":{\"pos\":{\"x\":1.2,\"y\":0.0,\"z\":3.4},\"rot\":{\"y\":90.0}," +
               "\"velocity\":{\"x\":0.5,\"z\":1.2},\"isMoving\":true}}";

        public static string BallKick(long seq)
            => $"{{\"type\":\"ball.kick\",\"seq\":{seq},\"timestamp\":{NowMs}," +
               "\"payload\":{\"force\":{\"x\":0.0,\"y\":2.5,\"z\":12.0},\"chargeRate\":0.85," +
               "\"ballPos\":{\"x\":5.0,\"y\":0.0,\"z\":6.0}}}";

        public static string GoalCheck(long seq)
            => $"{{\"type\":\"goal.check\",\"seq\":{seq},\"timestamp\":{NowMs}," +
               "\"payload\":{\"ballPos\":{\"x\":20.0,\"y\":1.0,\"z\":0.0}}}";

        public static string AuthLogin(long seq)
            => $"{{\"type\":\"auth.login\",\"seq\":{seq},\"timestamp\":{NowMs}," +
               "\"payload\":{\"user\":\"bench\",\"pass\":\"secret123\"}}";

        public static string AuthVerify(long seq)
            => $"{{\"type\":\"auth.verify\",\"seq\":{seq},\"timestamp\":{NowMs},\"payload\":{{}}}}";

        public static string MatchQuick(long seq)
            => $"{{\"type\":\"match.quick\",\"seq\":{seq},\"timestamp\":{NowMs}," +
               "\"payload\":{\"playerId\":\"bench\"}}";

        public static string RoomCreate(long seq)
            => $"{{\"type\":\"room.create\",\"seq\":{seq},\"timestamp\":{NowMs}," +
               "\"payload\":{\"name\":\"bench\"}}";

        public static string StateSync(long seq)
            => $"{{\"type\":\"state.sync\",\"seq\":{seq},\"timestamp\":{NowMs},\"payload\":{{}}}}";

        public static string TimerSync(long seq)
            => $"{{\"type\":\"timer.sync\",\"seq\":{seq},\"timestamp\":{NowMs},\"payload\":{{}}}}";

        public static string AssetLoad(long seq)
            => $"{{\"type\":\"asset.load\",\"seq\":{seq},\"timestamp\":{NowMs}," +
               "\"payload\":{\"sizeKb\":64}}";

        /// <summary>シナリオ名から対応するリクエストビルダーを引く。</summary>
        public static string Build(string scenario, long seq)
        {
            switch (scenario)
            {
                case "ping": return Echo(seq);
                case "move": return PlayerMove(seq);
                case "kick": return BallKick(seq);
                case "goal": return GoalCheck(seq);
                case "login": return AuthLogin(seq);
                case "verify": return AuthVerify(seq);
                case "match": return MatchQuick(seq);
                case "room": return RoomCreate(seq);
                case "sync": return StateSync(seq);
                case "timer": return TimerSync(seq);
                case "load": return AssetLoad(seq);
                default: return Echo(seq);
            }
        }
    }

    /// <summary>WebRTC 簡易シグナリングの offer リクエスト。</summary>
    [Serializable]
    public class RtcOffer
    {
        public string sdp;
    }

    /// <summary>WebRTC 簡易シグナリングの answer レスポンス。</summary>
    [Serializable]
    public class RtcAnswer
    {
        public string sdp;
    }
}
