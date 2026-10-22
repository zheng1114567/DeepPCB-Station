namespace DeepPCBStation.Core.Tcp;

/// <summary>
/// 已废弃：W1 简易 TRIG 服务。交付握手请用 <see cref="Handshake.HandshakeServer"/>。
/// 保留空壳以免旧引用静默复活双协议。
/// </summary>
[Obsolete("W2+ 使用 DeepPCBStation.Core.Handshake.HandshakeServer；勿再实例化本类（双协议坑）。", error: true)]
public sealed class TcpTriggerServer
{
    private TcpTriggerServer() { }
}
