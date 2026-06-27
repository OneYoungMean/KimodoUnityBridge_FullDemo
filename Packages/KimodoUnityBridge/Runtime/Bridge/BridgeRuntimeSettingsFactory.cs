using System;
using System.Diagnostics;

namespace KimodoBridge
{
    public static class BridgeRuntimeSettingsFactory
    {
        public static BridgeRuntimeSettings Create(
            string runtimeRoot,
            string launcherPath,
            string modelName,
            bool highVram,
            bool forceSetup = false,
            bool forceCpu = false,
            string modelsRoot = "",
            int startupTimeoutMs = BridgeRuntimeSettings.DefaultStartupTimeoutMs)
        {
            return new BridgeRuntimeSettings
            {
                runtimeRoot = runtimeRoot,
                launcherPath = launcherPath,
                modelName = string.IsNullOrWhiteSpace(modelName)
                    ? "Kimodo-SOMA-RP-v1"
                    : modelName.Trim(),
                highVram = highVram,
                forceSetup = forceSetup,
                forceCpu = forceCpu,
                modelsRoot = modelsRoot,
                startupTimeoutMs = Math.Max(30000, startupTimeoutMs),
                connectTimeoutMs = BridgeRuntimeSettings.DefaultConnectTimeoutMs,
                ioTimeoutMs = BridgeRuntimeSettings.DefaultIoTimeoutMs,
                modelLoadingTimeoutMs = BridgeRuntimeSettings.DefaultModelLoadingTimeoutMs,
                modelLoadingPollIntervalMs = BridgeRuntimeSettings.DefaultModelLoadingPollIntervalMs,
                statusConnectTimeoutMs = BridgeRuntimeSettings.DefaultStatusConnectTimeoutMs,
                statusIoTimeoutMs = BridgeRuntimeSettings.DefaultStatusIoTimeoutMs,
                idleTimeoutSeconds = 0,
                ownerProcessId = Process.GetCurrentProcess().Id,
                preserveProcessOnCancellation = true
            };
        }
    }
}
