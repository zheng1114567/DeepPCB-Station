using System;

namespace DeepPCBStation.Core.Camera;

/// <summary>
/// 采图拿不到图（回放耗尽、相机未触发、取流超时等）。
///
/// 为什么单独建类型：流水线原来靠 <c>ex.Message.Contains("NO_IMAGE")</c> 判定无图——
/// 消息文案一改、本地化一换、或被 <see cref="System.Reflection.TargetInvocationException"/>
/// 之类包装一层，判定就静默失效，无图会被当成「检测异常」而不是可恢复的 NO_IMAGE 分支。
/// 类型化之后判定与文案解耦。
///
/// 继承 <see cref="InvalidOperationException"/>：保持对既有
/// <c>catch (InvalidOperationException)</c> 调用点的兼容（它们是「相机不可用」的同族错误）。
/// </summary>
public sealed class CameraNoImageException : InvalidOperationException
{
    /// <summary>对应的流水线故障码（<see cref="Pipeline.InspectionResult.FaultCode"/>）。</summary>
    public const string FaultCode = "NO_IMAGE";

    public CameraNoImageException(string message) : base(message) { }

    public CameraNoImageException(string message, Exception? inner) : base(message, inner) { }
}
