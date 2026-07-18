using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using NSMedieval;
using NSMedieval.Managers;
using NSMedieval.Controllers;
using UnityEngine;

namespace GoingCooperative.Plugin.BepInEx
{
    public sealed partial class GoingCooperativePlugin
    {
        private readonly MultiplayerSaveTransfer multiplayerSaveTransfer = new MultiplayerSaveTransfer();
        private int multiplayerSelectedSaveIndex;
        private bool multiplayerLoadInvoked;
        private int multiplayerHandledLoadGeneration;
        private int multiplayerHandledResumeGeneration;
        private bool multiplayerResyncCaptureInProgress;
        private static bool multiplayerLoadingInProgress;
        private VillageSaveInfo? multiplayerHostCheckpointToLoad;
        private float multiplayerNativeLoadStartedRealtime;
        private bool multiplayerWaitingForHomeScene;
        private VillageSaveInfo? multiplayerPendingNativeLoad;
        private float multiplayerHomeReadyRealtime;

        private List<VillageSaveInfo> GetMultiplayerSaves()
        {
            try
            {
                GlobalSaveController.Instance.SynchronizeWithFiles();
                return GlobalSaveController.Instance.SavesList ?? new List<VillageSaveInfo>();
            }
            catch (Exception ex)
            {
                LogReplicationWarning("Going Cooperative could not enumerate saves: " + FormatReflectionExceptionDetail(ex));
                return new List<VillageSaveInfo>();
            }
        }

        private VillageSaveInfo? GetSelectedMultiplayerSave()
        {
            var saves = GetMultiplayerSaves();
            if (saves.Count == 0) return null;
            if (multiplayerSelectedSaveIndex < 0 || multiplayerSelectedSaveIndex >= saves.Count) multiplayerSelectedSaveIndex = 0;
            return saves[multiplayerSelectedSaveIndex];
        }

        private string GetSelectedMultiplayerSaveLabel()
        {
            var save = GetSelectedMultiplayerSave();
            return save == null ? "No saves found" : save.VillageName + " / " + save.FileName;
        }

        private void SelectPreviousMultiplayerSave()
        {
            var saves = GetMultiplayerSaves();
            if (saves.Count == 0) return;
            multiplayerSelectedSaveIndex = (multiplayerSelectedSaveIndex + saves.Count - 1) % saves.Count;
            ShowMultiplayerCanvasPage(MultiplayerMenuPage.Host);
        }

        private void SelectNextMultiplayerSave()
        {
            var saves = GetMultiplayerSaves();
            if (saves.Count == 0) return;
            multiplayerSelectedSaveIndex = (multiplayerSelectedSaveIndex + 1) % saves.Count;
            ShowMultiplayerCanvasPage(MultiplayerMenuPage.Host);
        }

        private bool StartMultiplayerSaveHost(int port, out string detail)
        {
            var save = GetSelectedMultiplayerSave();
            if (save == null) { detail = "No local save is available to host."; return false; }
            try
            {
                multiplayerLoadInvoked = false;
                multiplayerHandledLoadGeneration = 0;
                multiplayerHandledResumeGeneration = 0;
                multiplayerHostCheckpointToLoad = save;
                multiplayerSaveTransfer.StartHost(port, save);
                detail = "Waiting for Connections. Selected load: " + save.VillageName + " / " + save.FileName;
                return true;
            }
            catch (Exception ex) { detail = "Save host failed: " + ex.Message; return false; }
        }

        private bool StartMultiplayerSaveClient(string host, int port, out string detail)
        {
            try
            {
                multiplayerLoadInvoked = false;
                multiplayerHandledLoadGeneration = 0;
                multiplayerHandledResumeGeneration = 0;
                var saveRoot = Path.Combine(Application.persistentDataPath, "VillageSaves");
                multiplayerSaveTransfer.StartClient(host, port, saveRoot);
                detail = "Connecting and waiting to receive the host load.";
                return true;
            }
            catch (Exception ex) { detail = "Save connection failed: " + ex.Message; return false; }
        }

        private void MarkMultiplayerReadyToPlay()
        {
            if (!multiplayerSaveTransfer.TransferComplete)
            {
                MultiplayerMenu.StatusMessage = "Play is available after the load is transferred and verified.";
                return;
            }
            multiplayerSaveTransfer.MarkReadyToPlay();
            MultiplayerMenu.StatusMessage = multiplayerSaveTransfer.Detail;
        }

        private void UpdateMultiplayerSaveWorkflow()
        {
            if (multiplayerWaitingForHomeScene
                && multiplayerMainMenuActive
                && multiplayerPendingNativeLoad != null)
            {
                if (multiplayerHomeReadyRealtime <= 0f)
                {
                    multiplayerHomeReadyRealtime = Time.realtimeSinceStartup;
                    LogReplicationInfo("Going Cooperative full resync Home scene ready; checkpoint load queued epoch="
                        + multiplayerSaveTransfer.Epoch);
                }
                if (Time.realtimeSinceStartup - multiplayerHomeReadyRealtime >= 0.5f)
                {
                    var pending = multiplayerPendingNativeLoad;
                    multiplayerPendingNativeLoad = null;
                    multiplayerWaitingForHomeScene = false;
                    multiplayerHomeReadyRealtime = 0f;
                    LogReplicationInfo("Going Cooperative full resync leaving pseudo-Home and starting checkpoint load epoch="
                        + multiplayerSaveTransfer.Epoch);
                    StartMultiplayerNativeCheckpointLoad(pending);
                }
            }

            if (multiplayerLoadingInProgress
                && multiplayerNativeLoadStartedRealtime > 0f
                && Time.realtimeSinceStartup - multiplayerNativeLoadStartedRealtime > 180f)
            {
                OnMultiplayerNativeLoadFailed("Timed out after 180 seconds.");
            }
            if (replicationConfigHostMode && multiplayerSaveTransfer.ResyncCaptureRequested && !multiplayerResyncCaptureInProgress)
            {
                CaptureAndQueueMultiplayerResync();
            }

            if (multiplayerSaveTransfer.ResumeGeneration > multiplayerHandledResumeGeneration)
            {
                multiplayerHandledResumeGeneration = multiplayerSaveTransfer.ResumeGeneration;
                replicationConfigEnabled = true;
                TryStartReplicationRuntime();
                if (replicationConfigHostMode)
                {
                    TryInvokeStoredGameSpeedManagerMethod("SetSpeedNormal", out var resumeDetail);
                    LogReplicationInfo("Going Cooperative host simulation resumed detail=" + resumeDetail);
                }
                LogReplicationInfo("Going Cooperative replication resumed epoch=" + multiplayerSaveTransfer.Epoch);
            }

            if (multiplayerSaveTransfer.LoadGeneration <= multiplayerHandledLoadGeneration || multiplayerLoadInvoked) return;
            multiplayerHandledLoadGeneration = multiplayerSaveTransfer.LoadGeneration;
            multiplayerLoadInvoked = true;
            try
            {
                VillageSaveInfo? save = replicationConfigHostMode ? multiplayerHostCheckpointToLoad : ImportAndFindReceivedMultiplayerSave();
                if (save == null) throw new InvalidOperationException("The synchronized save was not found in the game save catalog.");
                replicationConfigEnabled = false;
                SetMultiplayerCanvasOpen(false);
                if (multiplayerSaveTransfer.Epoch > 0 && !multiplayerMainMenuActive)
                {
                    multiplayerPendingNativeLoad = save;
                    multiplayerWaitingForHomeScene = true;
                    multiplayerHomeReadyRealtime = 0f;
                    LogReplicationInfo("Going Cooperative full resync unloading current village to Home before checkpoint load epoch=" + multiplayerSaveTransfer.Epoch);
                    AddressableSceneLoadingManager.Instance.LoadHomeScene();
                }
                else
                {
                    StartMultiplayerNativeCheckpointLoad(save);
                }
            }
            catch (Exception ex)
            {
                multiplayerLoadingInProgress = false;
                replicationConfigEnabled = false;
                MultiplayerMenu.StatusMessage = "Could not start loading: " + FormatReflectionExceptionDetail(ex);
                LogReplicationWarning("Going Cooperative synchronized load failed: " + MultiplayerMenu.StatusMessage);
            }
        }

        private void StartMultiplayerNativeCheckpointLoad(VillageSaveInfo save)
        {
            LogReplicationInfo("Going Cooperative synchronized native checkpoint loading save=" + save.FilePath + " epoch=" + multiplayerSaveTransfer.Epoch);
            ResetReplicationEventRuntimeState(ReplicationTraderPartyResetContext.WorldReloadPending);
            replicationClientEventAuthorityParked = !replicationConfigHostMode && FullEventGraphAuthorityEnabled();
            replicationClientWeatherAuthorityParked = !replicationConfigHostMode && WeatherScheduleLaneEnabled();
            multiplayerLoadingInProgress = true;
            multiplayerNativeLoadStartedRealtime = Time.realtimeSinceStartup;
            replicationConfigEnabled = false;
            LogReplicationInfo("Going Cooperative replication hooks gated off for native save loading.");
            SetMultiplayerCanvasOpen(false);
            SecureSaveLoadingManager.Instance.LoadVillageSaveData(save);
        }

        private VillageSaveInfo? ImportAndFindReceivedMultiplayerSave()
        {
            var receivedPath = multiplayerSaveTransfer.ReceivedSavePath;
            if (!File.Exists(receivedPath)) return null;
            var folderName = new DirectoryInfo(Path.GetDirectoryName(receivedPath)!).Name;
            var fileName = Path.GetFileName(receivedPath);
            var controller = GlobalSaveController.Instance;
            var import = typeof(GlobalSaveController).GetMethod("ImportSavFilesFromSubfolders", BindingFlags.Instance | BindingFlags.NonPublic);
            if (import == null) throw new MissingMethodException(typeof(GlobalSaveController).FullName, "ImportSavFilesFromSubfolders");
            import.Invoke(controller, null);
            var saves = controller.SavesList;
            for (var i = 0; i < saves.Count; i++)
            {
                var candidate = saves[i];
                if (string.Equals(candidate.FolderName, folderName, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(candidate.FileName, fileName, StringComparison.OrdinalIgnoreCase))
                {
                    LogReplicationInfo("Going Cooperative imported transferred save village=" + candidate.VillageName
                        + " folder=" + candidate.FolderName + " file=" + candidate.FileName + " path=" + candidate.FilePath);
                    return candidate;
                }
            }
            throw new InvalidOperationException("Going Medieval did not catalog transferred save folder=" + folderName
                + " file=" + fileName + " catalogCount=" + saves.Count);
        }

        private void OnMultiplayerGameLoadingFinished(bool afterLoad)
        {
            if (!multiplayerLoadingInProgress) return;
            multiplayerLoadingInProgress = false;
            multiplayerNativeLoadStartedRealtime = 0f;
            multiplayerLoadInvoked = false;
            replicationConfigEnabled = false;
            multiplayerSaveTransfer.NotifyNativeLoadFinished();
            replicationNextEventCheckpointRealtime = 0f;
            replicationNextWeatherCheckpointRealtime = 0f;
            replicationNextWeatherEnvironmentRealtime = 0f;
            replicationLastWeatherScheduleSignature = string.Empty;
            replicationLastWeatherEnvironmentSignature = string.Empty;
            LogReplicationInfo("Going Cooperative synchronized loading finished afterLoad=" + afterLoad);
        }

        private void OnMultiplayerNativeLoadFailed(string reason)
        {
            if (!multiplayerLoadingInProgress) return;
            multiplayerLoadingInProgress = false;
            multiplayerLoadInvoked = false;
            multiplayerNativeLoadStartedRealtime = 0f;
            replicationConfigEnabled = false;
            multiplayerSaveTransfer.ReportLoadFailure(reason);
            MultiplayerMenu.StatusMessage = "Native checkpoint load failed: " + reason;
            LogReplicationWarning(MultiplayerMenu.StatusMessage);
        }

        private void CaptureAndQueueMultiplayerResync()
        {
            multiplayerResyncCaptureInProgress = true;
            try
            {
                if (!FlushHostTraderPartyAbortsBeforeCheckpoint(out var abortFlushDetail))
                    throw new InvalidOperationException("Trader abort cleanup is incomplete before checkpoint: " + abortFlushDetail);
                replicationConfigEnabled = false;
                StopReplicationRuntime(ReplicationTraderPartyResetContext.WorldReloadPending);
                TryInvokeStoredGameSpeedManagerMethod("SetSpeedPause", out var pauseDetail);
                var filename = "GoingCooperative_Resync_" + DateTime.UtcNow.ToString("yyyyMMdd_HHmmssfff");
                var checkpoint = GlobalSaveController.Instance.SaveCurrentVillage(filename);
                if (checkpoint == null) throw new InvalidOperationException("Going Medieval did not create the resync checkpoint.");
                multiplayerHostCheckpointToLoad = checkpoint;
                LogReplicationInfo("Going Cooperative full resync checkpoint created path=" + checkpoint.FilePath + " pause=" + pauseDetail);
                multiplayerSaveTransfer.QueueResyncCheckpoint(checkpoint);
            }
            catch (Exception ex)
            {
                MultiplayerMenu.StatusMessage = "Full resync capture failed: " + FormatReflectionExceptionDetail(ex);
                LogReplicationWarning(MultiplayerMenu.StatusMessage);
                multiplayerSaveTransfer.RejectResync(MultiplayerMenu.StatusMessage);
            }
            finally
            {
                multiplayerResyncCaptureInProgress = false;
            }
        }

        private void RequestFullMultiplayerResync()
        {
            if (!TryRequestFullMultiplayerResync(out var error))
            {
                MultiplayerMenu.StatusMessage = error;
                SetMultiplayerCanvasMessage(error);
            }
        }

        private bool TryRequestFullMultiplayerResync(out string error)
        {
            if (!multiplayerSaveTransfer.RequestFullResync(out error))
            {
                return false;
            }

            replicationConfigEnabled = false;
            StopReplicationRuntime(ReplicationTraderPartyResetContext.WorldReloadPending);
            MultiplayerMenu.StatusMessage = "Full resync requested. Waiting for the host checkpoint.";
            error = string.Empty;
            return true;
        }
    }
}
