namespace DynamicPill.Services;

using System;
using System.Diagnostics;
using System.IO;
using DynamicPill.Interop;
using DynamicPill.Models;

public static class AppLauncher
{
    public static void ActivateApp(PillItem item)
    {
        if (TryForegroundExistingProcess(item.AppId, item.AppName))
            return;

        if (TryLaunchViaAppsFolder(item.AppId))
            return;

        OpenActionCenter();
    }

    private static bool TryForegroundExistingProcess(string appId, string appName = "")
    {
        try
        {
            string targetName = appId;
            if (appId.StartsWith("DynamicPill.", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(appName) && !appName.Equals("DynamicPill", StringComparison.OrdinalIgnoreCase))
                {
                    targetName = appName;
                }
                else
                {
                    return false;
                }
            }

            if (targetName.Equals("DynamicPill", StringComparison.OrdinalIgnoreCase))
                return false;

            string exeName = ExtractExeName(targetName);
            if (string.IsNullOrEmpty(exeName))
                return false;

            string procName = Path.GetFileNameWithoutExtension(exeName);
            var processes = Process.GetProcessesByName(procName);

            foreach (var proc in processes)
            {
                if (proc.MainWindowHandle != IntPtr.Zero)
                {
                    NativeMethods.AllowSetForegroundWindow(proc.Id);
                    NativeMethods.ShowWindow(proc.MainWindowHandle, NativeMethods.SW_RESTORE);
                    NativeMethods.SetForegroundWindow(proc.MainWindowHandle);
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AppLauncher] TryForegroundExistingProcess failed: {ex.Message}");
        }
        return false;
    }

    private static bool TryLaunchViaAppsFolder(string appId)
    {
        try
        {
            string target = $"shell:AppsFolder\\{appId}";
            var result = NativeMethods.ShellExecute(IntPtr.Zero, "open", target, null, null, NativeMethods.SW_SHOW);
            return result.ToInt64() > 32;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AppLauncher] TryLaunchViaAppsFolder failed: {ex.Message}");
            return false;
        }
    }

    public static void OpenActionCenter()
    {
        try
        {
            NativeMethods.ShellExecute(IntPtr.Zero, "open", "ms-actioncenter:", null, null, NativeMethods.SW_SHOW);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AppLauncher] OpenActionCenter failed: {ex.Message}");
        }
    }

    private static string ExtractExeName(string appId)
    {
        if (appId.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            return appId;

        int bang = appId.IndexOf('!');
        if (bang > 0)
            return appId[..bang];

        return appId;
    }
}
