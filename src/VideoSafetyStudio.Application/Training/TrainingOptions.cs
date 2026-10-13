namespace VideoSafetyStudio.Application.Training;

public sealed class TrainingOptions
{
    public const string Section = "Training";

    /// <summary>Longest a training run may take before its container is killed and the run is marked failed.</summary>
    public double MaxRunHours { get; set; } = 12;
}
