using DeepPCBStation.Core.Imaging;

namespace DeepPCBStation.Core.Abstractions;

/// <summary>
/// 工业相机抽象。MVP 主路径为 PairedFilePlaybackCamera。
/// </summary>
public interface ICamera : IDisposable
{
    string Name { get; }
    bool IsOpened { get; }
    int FrameWidth { get; }
    int FrameHeight { get; }

    void Open();
    void Close();

    /// <summary>取一帧灰度。调用方 Dispose 返回的 FrameBuffer。</summary>
    FrameBuffer Grab();
}

/// <summary>文件回放扩展：重置播放指针。</summary>
public interface IPlaybackCamera : ICamera
{
    void ResetPlayback();
    int FrameCount { get; }
    int CurrentIndex { get; }
}
