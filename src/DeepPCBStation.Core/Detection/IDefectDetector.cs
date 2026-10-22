using DeepPCBStation.Core.Config;
using DeepPCBStation.Core.Imaging;

namespace DeepPCBStation.Core.Detection;

public interface IDefectDetector
{
    /// <summary>Halcon | Stub</summary>
    string EngineName { get; }
    void ApplyRecipe(RecipeConfig recipe);
    DetectionOutcome Detect(FrameBuffer frame);
}
