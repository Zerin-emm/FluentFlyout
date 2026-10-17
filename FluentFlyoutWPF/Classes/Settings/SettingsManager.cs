// Copyright (c) 2024-2026 The FluentFlyout Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FluentFlyoutWPF.ViewModels;
using System.IO;
using System.Xml.Serialization;

namespace FluentFlyout.Classes.Settings;

/// <summary>
/// Manages the application settings and saves them to a file in \AppData\FluentFlyout.
/// </summary>
public class SettingsManager
{
    private static readonly NLog.Logger Logger = NLog.LogManager.GetCurrentClassLogger();
    private static readonly Lock SettingsFileLock = new();

    private static string SettingsFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "FluentFlyout",
        "settings.xml"
    );

    private static UserSettings? _current;
    private static bool DeserializeSettings(string filePath, out UserSettings? settings)
    {
        settings = null;

        if (!File.Exists(filePath))
            return false;

        using StreamReader reader = new(filePath);
        XmlSerializer xmlSerializer = new(typeof(UserSettings));
        settings = (UserSettings?)xmlSerializer.Deserialize(reader);
        return settings != null;
    }

    /// <summary>
    /// The current user settings stored in the app.
    /// </summary>
    /// <returns>The current user settings.</returns>
    public static UserSettings Current
    {
        get
        {
            if (_current == null)
            {
                // CompleteInitialization() is what flips UserSettings out of its "still loading" state,
                // which suppresses every save. A lazily created instance that skipped it would accept
                // changes and silently never persist them.
                _current = new UserSettings();
                _current.CompleteInitialization();
            }

            return _current;
        }
        set => _current = value;
    }

    /// <summary>
    /// Restores the settings `SettingsManager.Current` from the settings file.
    /// </summary>
    /// <returns>The restored settings.</returns>
    public static UserSettings RestoreSettings(string? filePath = null)
    {
        filePath ??= SettingsFilePath;
        string backupPath = filePath + ".bak";

        try
        {
            if (DeserializeSettings(filePath, out var loadedSettings) && loadedSettings != null)
            {
                _current = loadedSettings;
                _current.CompleteInitialization();

                Logger.Info("Settings successfully restored");
                return _current;
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            Logger.Error(ex, "No permission to read in settings file");
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Error restoring settings");
        }

        // try restoring backup (version before the last save)
        try
        {
            if (DeserializeSettings(backupPath, out var backupSettings) && backupSettings != null)
            {
                _current = backupSettings;
                _current.CompleteInitialization();

                Logger.Warn("Could not restore primary settings file, restored settings from backup");
                return _current;
            }
        }
        catch (Exception backupEx)
        {
            Logger.Error(backupEx, "Error restoring settings from backup file");
        }

        // if the settings/backup file not found or cannot be read
        Logger.Warn("Settings & backup file not found or cannot be read, loading default settings");
        _current = new UserSettings();
        _current.CompleteInitialization();
        return _current;
    }

    /// <summary>
    /// Saves the app settings to the settings file.
    /// </summary>
    public static void SaveSettings(string? filePath = null)
    {
        filePath ??= SettingsFilePath;

        // A temp file unique to this call. The previous shared "settings.xml.tmp" was written by every
        // save, so a second save could overwrite the temp file while the first replacement was still
        // reading it, and the outcome depended on which of the two finished last.
        string tempPath = $"{filePath}.{Guid.NewGuid():N}.tmp";
        string backupPath = filePath + ".bak";

        try
        {
            lock (SettingsFileLock)
            {
                string? directory = Path.GetDirectoryName(filePath);
                if (directory != null && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                UserSettings settings = Current;

                using (var writer = new StreamWriter(tempPath, false))
                {
                    new XmlSerializer(typeof(UserSettings)).Serialize(writer, settings);
                }

                // The replacement deliberately stays inside the lock and on the calling thread. It used
                // to be dispatched with Task.Run, which ran it *after* the lock was released: two saves
                // could then interleave and the last writer won unpredictably, and a save issued right
                // before shutdown could simply be lost. The replacement is a few milliseconds of I/O on
                // a small XML file - a fair price for a guaranteed save order.
                if (File.Exists(filePath))
                {
                    TryReplaceSettingsFile(filePath, tempPath, backupPath);
                }
                else
                {
                    File.Move(tempPath, filePath, true);
                }

                Logger.Info("Settings successfully saved to {0}", filePath);
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            // if the app doesn't have permission to write to the settings file
            Logger.Error(ex, "No permission to write in settings file");
        }
        catch (Exception ex)
        {
            // if the settings file cannot be saved
            Logger.Error(ex, "Error saving settings");
        }
        finally
        {
            // the temp file is unique to this call, so cleaning it up here cannot race with another save
            TryDeleteFileIfExists(tempPath);
        }
    }

    private static void TryReplaceSettingsFile(string filePath, string tempPath, string backupPath)
    {
        Logger.Debug("Initializing replacing settings file at {0}", filePath);
        int maxAttempts = 5;
        // The following steps try to avoid issues with file locks and permissions on some systems.
        for (int attempts = 1; attempts <= maxAttempts; attempts++)
        {
            try
            {
                File.Replace(tempPath, filePath, backupPath, ignoreMetadataErrors: true);
                return;
            }
            catch (IOException ex) when (attempts < maxAttempts)
            {
                // if the file is locked, wait and retry
                Logger.Warn(ex, "Settings file is locked, retrying...");
                Thread.Sleep(75);
            }
            catch (IOException ex)
            {
                Logger.Warn(ex, "File.Replace failed after retries, manually replacing...");
                ManualReplace(filePath, tempPath, backupPath);
                return;
            }
        }
    }

    private static void ManualReplace(string filePath, string tempPath, string backupPath)
    {
        TryDeleteFileIfExists(backupPath);
        File.Copy(filePath, backupPath);
        TryDeleteFileIfExists(filePath);
        File.Move(tempPath, filePath);
    }

    private static void TryDeleteFileIfExists(string path)
    {
        // delete file if it still exists
        if (File.Exists(path))
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error deleting file at {0}", path);
            }
        }
    }
}