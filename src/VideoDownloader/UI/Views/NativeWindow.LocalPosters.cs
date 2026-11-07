namespace VideoDownloader.Native;

public sealed partial class NativeWindow
{
    private bool AllowLocalPosters()=>!_disposed && !_closing && !_appUpdateHandoff &&
        !_restoringSession && !_checkpointReadOnly && !_legacyEngineBlocked && !_queueItemActionBusy && !_dependencyOperationRunning &&
        !_profileBackupBusy && !_profileBackupRecoveryRequired && !_profileBackupReplaying;
}
