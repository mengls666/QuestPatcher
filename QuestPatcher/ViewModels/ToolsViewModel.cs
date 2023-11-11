using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using QuestPatcher.Core;
using QuestPatcher.Core.Models;
using QuestPatcher.Models;
using QuestPatcher.Services;
using QuestPatcher.Views;
using ReactiveUI;
using Serilog;
using JetBrains.Annotations;
using System.Collections.Generic;

namespace QuestPatcher.ViewModels
{
    public class ToolsViewModel : ViewModelBase
    {
        public Config Config { get; }

        public ProgressViewModel ProgressView { get; }

        public OperationLocker Locker { get; }

        public ThemeManager ThemeManager { get; }

        public string AdbButtonText => _isAdbLogging ? "Stop ADB Log" : "Start ADB Log";

        private bool _isAdbLogging;

        private readonly Window _mainWindow;
        private readonly SpecialFolders _specialFolders;
        private readonly InstallManager _installManager;
        private readonly AndroidDebugBridge _debugBridge;
        private readonly QuestPatcherUiService _uiService;
        private readonly InfoDumper _dumper;

        public ToolsViewModel(Config config, ProgressViewModel progressView, OperationLocker locker, Window mainWindow, SpecialFolders specialFolders, InstallManager installManager, AndroidDebugBridge debugBridge, QuestPatcherUiService uiService, InfoDumper dumper, ThemeManager themeManager)
        {
            Config = config;
            ProgressView = progressView;
            Locker = locker;
            ThemeManager = themeManager;

            _mainWindow = mainWindow;
            _specialFolders = specialFolders;
            _installManager = installManager;
            _debugBridge = debugBridge;
            _uiService = uiService;
            _dumper = dumper;

            _debugBridge.StoppedLogging += (_, _) =>
            {
                Log.Information("ADB log exited");
                _isAdbLogging = false;
                this.RaisePropertyChanged(nameof(AdbButtonText));
            };
        }

        public async void UninstallApp()
        {
            try
            {
                DialogBuilder builder = new()
                {
                    Title = "Are you sure?",
                    Text = "Uninstalling your app will exit QuestPatcher, as it requires your app to be installed. If you ever reinstall your app, reopen QuestPatcher and you can repatch"
                };
                builder.OkButton.Text = "Uninstall App";
                if (await builder.OpenDialogue(_mainWindow))
                {
                    Locker.StartOperation();
                    try
                    {
                        Log.Information("Uninstalling app . . .");
                        await _installManager.UninstallApp();
                    }
                    finally
                    {
                        Locker.FinishOperation();
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to uninstall app");
            }
        }

        public void OpenLogsFolder()
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _specialFolders.LogsFolder,
                UseShellExecute = true,
                Verb = "open"
            });
        }

        public async void QuickFix()
        {
            Locker.StartOperation(true); // ADB is not available during a quick fix, as we redownload platform-tools
            try
            {
                await _uiService.QuickFix();
                Log.Information("Done!");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to clear cache");
                DialogBuilder builder = new()
                {
                    Title = "Failed to clear cache",
                    Text = "Running the quick fix failed due to an unhandled error",
                    HideCancelButton = true
                };
                builder.WithException(ex);

                await builder.OpenDialogue(_mainWindow);
            }
            finally
            {
                Locker.FinishOperation();
            }
        }

        public async void RemoveOldModDirectories()
        {
            try
            {
                List<string> modPaths = new List<string>
                {
                    "/sdcard/Android/data/com.beatgames.beatsaber/files/libs",
                    "/sdcard/Android/data/com.beatgames.beatsaber/files/mods"
                };

                await _debugBridge.Chmod(modPaths, "777");

                foreach(string modPath in modPaths)
                {
                    Log.Information(modPath);
                    await _debugBridge.RemoveDirectory(modPath);
                }

                Log.Information("Done!");

                DialogBuilder builder = new()
                {
                    Title = "Finished removing old mod folders!",
                    Text = $"The following mod folders were removed: \n{modPaths[0]}\n{modPaths[1]}",
                    HideCancelButton = true
                };
                await builder.OpenDialogue();
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to remove the old mod directories: {ex}");
                DialogBuilder builder = new()
                {
                    Title = "Failed to remove the old mod directories",
                    Text = "Removing the old mod directories failed due to an unhandled error",
                    HideCancelButton = true
                };
                builder.WithException(ex);

                await builder.OpenDialogue(_mainWindow);
            }
        }

        public async void FixModPermissions()
        {
            try
            {
                List<string> modPaths = new List<string>
                {
                    "/sdcard/Android/data/com.beatgames.beatsaber/files/libs/*",
                    "/sdcard/Android/data/com.beatgames.beatsaber/files/mods/*"
                };

                List<string> libsExist = await _debugBridge.ListDirectoryFiles(modPaths[0].Replace("/*",""));
                List<string> modsExist = await _debugBridge.ListDirectoryFiles(modPaths[1].Replace("/*", ""));

                if(libsExist.Count <= 0)
                    throw new Exception("Library files are not copied, ensure you have installed core mods successfully");

                if(modsExist.Count <= 0)
                    throw new Exception("Mod files are not copied, ensure you have installed core mods successfully");

                await _debugBridge.Chmod(modPaths, "+r");
                Log.Information("Done!");
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to fix the mod directory permissions: {ex}");
                DialogBuilder builder = new()
                {
                    Title = "Failed to fix the mod directory permissions",
                    Text = "Running the fix permissions failed due to an unhandled error",
                    HideCancelButton = true
                };
                builder.WithException(ex);

                await builder.OpenDialogue(_mainWindow);
            }
        }

        public async void ToggleAdbLog()
        {
            if (_isAdbLogging)
            {
                _debugBridge.StopLogging();
            }
            else
            {
                Log.Information("Starting ADB log");
                await _debugBridge.StartLogging(Path.Combine(_specialFolders.LogsFolder, "adb.log"));

                _isAdbLogging = true;
                this.RaisePropertyChanged(nameof(AdbButtonText));
            }
        }

        public async void CreateDump()
        {
            Locker.StartOperation();
            try
            {
                // Create the dump in the default location (the data directory)
                string dumpLocation = await _dumper.CreateInfoDump();

                string? dumpFolder = Path.GetDirectoryName(dumpLocation);
                if (dumpFolder != null)
                {
                    // Open the dump's directory for convenience
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = dumpFolder,
                        UseShellExecute = true,
                        Verb = "open"
                    });
                }
            }
            catch (Exception ex)
            {
                // Show a dialog with any errors
                Log.Error(ex, "Failed to create dump");
                DialogBuilder builder = new()
                {
                    Title = "Failed to create dump",
                    Text = "Creating the dump failed due to an unhandled error",
                    HideCancelButton = true
                };
                builder.WithException(ex);

                await builder.OpenDialogue(_mainWindow);
            }
            finally
            {
                Locker.FinishOperation();
            }
        }

        public void RepatchApp()
        {
            _uiService.OpenRepatchMenu();
        }

        public async void ChangeApp()
        {
            await _uiService.OpenChangeAppMenu(false);
        }

        public async void RestartApp()
        {
            try
            {
                Log.Information("Restarting app");
                Locker.StartOperation();
                await _debugBridge.ForceStop(Config.AppId);

                // Run the app once, wait, and run again.
                // This bypasses the restore app prompt
                await _debugBridge.RunUnityPlayerActivity(Config.AppId);
                await Task.Delay(1000);
                await _debugBridge.RunUnityPlayerActivity(Config.AppId);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "Failed to restart app");
                DialogBuilder builder = new()
                {
                    Title = "Failed to restart app",
                    Text = "Restarting the app failed due to an unhandled error",
                    HideCancelButton = true
                };
                builder.WithException(ex);

                await builder.OpenDialogue(_mainWindow);
            }
            finally
            {
                Locker.FinishOperation();
            }
        }

        public void OpenThemesFolder()
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = ThemeManager.ThemesDirectory,
                UseShellExecute = true,
                Verb = "open"
            });
        }
    }
}
