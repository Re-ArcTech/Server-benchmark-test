using System.Threading.Tasks;

namespace YubiBench
{
    /// <summary>
    /// REST / WebSocket / WebRTC を同じ手順で計測するための共通インターフェース。
    /// Runner はこのインターフェース越しに各方式を叩くので、方式追加が楽。
    /// </summary>
    public interface IBenchTransport
    {
        /// <summary>表示名（"REST" / "WebSocket" / "WebRTC"）。</summary>
        string Name { get; }

        /// <summary>
        /// 接続を確立する。確立にかかった時間は LastConnectMillis に入れる。
        /// REST のように都度接続の方式は warm-up（ヘルスチェック）時間を入れる。
        /// </summary>
        Task<bool> ConnectAsync();

        /// <summary>
        /// 1往復させて RTT（ミリ秒）を返す。失敗時は -1。
        /// out で最後に受け取ったサーバー処理時間（ミリ秒）も返す。
        /// </summary>
        Task<RoundTripResult> RoundTripAsync(string requestJson);

        /// <summary>接続確立にかかった時間（ミリ秒）。</summary>
        double LastConnectMillis { get; }

        void Close();
    }

    /// <summary>1往復の結果。</summary>
    public struct RoundTripResult
    {
        public double RttMillis;        // クライアント計測の往復時間
        public double ServerProcMillis; // サーバー内処理時間
        public bool Ok;

        public static RoundTripResult Fail => new RoundTripResult { RttMillis = -1, Ok = false };
    }
}
