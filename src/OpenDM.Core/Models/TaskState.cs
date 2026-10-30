namespace OpenDM.Core.Models;

public enum TaskState
{
    Queued,
    Probing,
    Downloading,
    Paused,
    Completed,
    Error
}
